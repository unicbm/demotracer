using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using DemoTracerBotHiderApi;
using HarmonyLib;

namespace BotHiderImpl;

public sealed class BotHiderImplPlugin : BasePlugin
{

    public override string ModuleName => "DemoTracer BotHider";
    public override string ModuleVersion => "0.1.8";
    public override string ModuleAuthor => "XBribo contributors, unicbm";
    public override string ModuleDescription =>
        "DemoTracer-managed bot identity and presentation runtime.";

    public static PluginCapability<IBotHiderApi> Capability { get; } =
        new(DemoTracerBotHiderContract.Capability);

    private NativePresentationClient? _client;
    private BotHiderPresentationService? _presentation;
    private bool _applyPending;
    private bool _fullApplyPending;
    private ulong _pendingPingSlots;
    private bool _unloaded;
    private int _mapGeneration;
    private Harmony? _harmony;

    public override void Load(bool hotReload)
    {
        _unloaded = false;
        WarnIfLegacyBotHiderPluginIsPresent();
        _client = new NativePresentationClient(OnNativePresentationChanged);
        _presentation = new BotHiderPresentationService(_client);
        _client.TryConnect();
        Capabilities.RegisterPluginCapability(Capability, () => _presentation);

        IsBotPatch.Api = _presentation;
        _harmony = new Harmony("org.unicbm.demotracer.bothider.isbot");
        _harmony.PatchAll(typeof(BotHiderImplPlugin).Assembly);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        ScheduleApply();
        Server.PrintToConsole(
            $"[DemoTracer BotHider] loaded api={DemoTracerBotHiderContract.ApiVersion} " +
            $"provider_epoch={_presentation.GetProviderInfo().ProviderEpoch} " +
            "crosshair_writer=networked_on_demand");
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        IsBotPatch.Api = null;
        _applyPending = false;
        _mapGeneration++;
        try
        {
            _harmony?.UnpatchAll(_harmony.Id);
        }
        finally
        {
            _harmony = null;
            try { _presentation?.Dispose(); }
            finally
            {
                _presentation = null;
                // Never leave a managed callback in native code after unload,
                // including when another cleanup step failed.
                _client?.Dispose();
                _client = null;
                DemoTracerBotHiderContract.NotifyProviderChanged();
            }
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _client?.TryConnect();
        ScheduleApply();
        DemoTracerBotHiderContract.NotifyProviderChanged();
    }

    [ConsoleCommand("bh_native_ready", "Rebind the native BotHider presentation lifecycle")]
    [CommandHelper(0, "", CommandUsage.SERVER_ONLY)]
    public void OnNativeReady(CCSPlayerController? player, CommandInfo command)
    {
        if (_client?.TryConnect() == true) OnNativePresentationChanged(1, -1);
    }

    private void OnNativePresentationChanged(uint reason, int slot)
    {
        if (_unloaded) return;
        if (reason == 1)
        {
            ScheduleApply();
            DemoTracerBotHiderContract.NotifyProviderChanged();
        }
        else if (reason == 2 && slot is >= 0 and < 64)
        {
            _pendingPingSlots |= 1UL << slot;
            SchedulePublication();
        }
    }

    private void OnMapStart(string mapName)
    {
        _applyPending = false;
        _fullApplyPending = false;
        _pendingPingSlots = 0;
        _mapGeneration++;
        _presentation?.ResetForMapBoundary();
        ScheduleApply();
    }

    private void OnMapEnd()
    {
        _applyPending = false;
        _fullApplyPending = false;
        _pendingPingSlots = 0;
        _mapGeneration++;
        _presentation?.ResetForMapBoundary();
    }

    private void OnClientDisconnect(int slot)
        => _presentation?.HandleClientDisconnect(slot);

    private void WarnIfLegacyBotHiderPluginIsPresent()
    {
        try
        {
            var pluginsDirectory = Directory.GetParent(ModuleDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(pluginsDirectory))
                return;

            foreach (var legacyDirectoryName in new[] { "BotHiderImpl", "BotHider", "DemoTracerBotHider" })
            {
                var legacyDirectory = Path.Combine(pluginsDirectory, legacyDirectoryName);
                if (string.Equals(Path.GetFullPath(legacyDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(ModuleDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Directory.Exists(legacyDirectory) ||
                    !Directory.EnumerateFiles(legacyDirectory, "*.dll").Any())
                    continue;
                Server.PrintToConsole(
                    "[DemoTracer BotHider] ERROR: another BotHider CSS plugin directory is present: " +
                    $"{legacyDirectoryName}. Remove it before runtime testing; multiple presentation writers are unsupported.");
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"[DemoTracer BotHider] legacy plugin check failed: {ex.Message}");
        }
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ScheduleApply();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
            SchedulePresentationReconcile(player.Slot);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
            SchedulePresentationReconcile(player.Slot);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
            SchedulePresentationReconcile(player.Slot);
        return HookResult.Continue;
    }

    private void SchedulePresentationReconcile(int slot) => ScheduleApply();

    private void ScheduleApply()
    {
        _fullApplyPending = true;
        SchedulePublication();
    }

    private void SchedulePublication()
    {
        if (_unloaded || _applyPending) return;
        _applyPending = true;
        var generation = _mapGeneration;
        Server.NextFrame(() =>
        {
            if (_unloaded || generation != _mapGeneration) return;
            _applyPending = false;
            var full = _fullApplyPending;
            var pingSlots = _pendingPingSlots;
            _fullApplyPending = false;
            _pendingPingSlots = 0;
            if (full) _presentation?.PublishManagedSlots();
            else
            {
                while (pingSlots != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(pingSlots);
                    pingSlots &= pingSlots - 1;
                    _presentation?.PublishPing(slot);
                }
            }
        });
    }

    [ConsoleCommand("bh_status", "Show DemoTracer BotHider provider and managed-slot status")]
    [CommandHelper(0, "", CommandUsage.SERVER_ONLY)]
    public void OnStatus(CCSPlayerController? player, CommandInfo command)
    {
        if (_presentation == null)
        {
            command.ReplyToCommand("[DemoTracer BotHider] not initialized");
            return;
        }

        var provider = _presentation.GetProviderInfo();
        var diagnostics = _presentation.GetDiagnostics();
        command.ReplyToCommand(
            $"[DemoTracer BotHider] api={provider.ApiVersion} connected={provider.Connected} " +
            $"epoch={provider.ProviderEpoch} map_epoch={provider.MapEpoch} " +
            $"managed={diagnostics.ManagedSlots} leases={diagnostics.ActiveLeases}/" +
            $"{diagnostics.LeasedSlots} writes={diagnostics.PublishedWrites} " +
            $"controller_repairs={diagnostics.ControllerRepairs} " +
            "crosshair_writer=networked_on_demand");
        if (diagnostics.Signatures.Length > 0)
            command.ReplyToCommand($"[DemoTracer BotHider] hooks: {string.Join(' ', diagnostics.Signatures)}");

        for (var slot = 0; slot < 64; slot++)
        {
            if (!_presentation.TryGetManagedSlot(slot, out var state))
                continue;
            var controller = Utilities.GetPlayerFromSlot(slot);
            var controllerName = controller is { IsValid: true }
                ? controller.PlayerName
                : "<invalid>";
            var controllerSteamId = controller is { IsValid: true }
                ? controller.SteamID
                : 0UL;
            var publishedName = _client?.GetPublishedPersonaName(slot) ?? string.Empty;
            var publishedSteamId = _client?.GetPublishedSteamId(slot) ?? 0UL;
            command.ReplyToCommand(
                $"  slot={slot} incarnation={state.Incarnation} " +
                $"controller='{controllerName}'/{controllerSteamId} " +
                $"published='{publishedName}'/{publishedSteamId} " +
                $"base='{state.BasePlayerName}'/{state.BaseSteamId} ping={state.BasePing} " +
                $"crosshair='{state.BaseCrosshairCode}' flair={state.BaseScoreboardFlair}");
        }
    }

    [ConsoleCommand("bh_disguise", "bh_disguise <0|1>")]
    [CommandHelper(0, "", CommandUsage.SERVER_ONLY)]
    public void OnDisguise(CCSPlayerController? player, CommandInfo command)
    {
        if (_client == null ||
            command.ArgCount < 2 ||
            !int.TryParse(command.GetArg(1), out var value))
        {
            command.ReplyToCommand("usage: bh_disguise <0|1>");
            return;
        }

        var enabled = value != 0;
        command.ReplyToCommand(
            $"[DemoTracer BotHider] disguise={(enabled ? "on" : "off")} ok={_client.SetDisguise(enabled)}");
    }

    [ConsoleCommand("bh_namesource", "bh_namesource <0|1>")]
    [CommandHelper(0, "", CommandUsage.SERVER_ONLY)]
    public void OnNameSource(CCSPlayerController? player, CommandInfo command)
    {
        if (_client == null ||
            command.ArgCount < 2 ||
            !int.TryParse(command.GetArg(1), out var value))
        {
            command.ReplyToCommand("usage: bh_namesource <0|1>");
            return;
        }

        var useBotInfo = value != 0;
        command.ReplyToCommand(
            $"[DemoTracer BotHider] name_source={(useBotInfo ? "bot_info" : "botprofile")} " +
            $"ok={_client.SetNameSource(useBotInfo)}");
    }
}
