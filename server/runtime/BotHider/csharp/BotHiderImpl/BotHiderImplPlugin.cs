using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using DemoTracerBotHiderApi;
using HarmonyLib;

namespace BotHiderImpl;

public sealed class BotHiderImplPlugin : BasePlugin
{
    private const float FastApplyIntervalSeconds = 0.25f;
    private const int FastApplyTicks = 12;

    public override string ModuleName => "DemoTracer BotHider";
    public override string ModuleVersion => "0.1.4";
    public override string ModuleAuthor => "XBribo contributors, unicbm";
    public override string ModuleDescription =>
        "DemoTracer-managed bot identity and presentation runtime.";

    public static PluginCapability<IBotHiderApi> Capability { get; } =
        new(DemoTracerBotHiderContract.Capability);

    private SharedMemoryClient? _client;
    private BotHiderPresentationService? _presentation;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _fastApplyTimer;
    private int _fastApplyRemaining;
    private Harmony? _harmony;

    public override void Load(bool hotReload)
    {
        WarnIfLegacyBotHiderPluginIsPresent();
        _client = new SharedMemoryClient();
        _presentation = new BotHiderPresentationService(_client);
        _client.TryConnect();
        Capabilities.RegisterPluginCapability(Capability, () => _presentation);

        IsBotPatch.Api = _presentation;
        _harmony = new Harmony("org.unicbm.demotracer.bothider.isbot");
        _harmony.PatchAll(typeof(BotHiderImplPlugin).Assembly);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        AddTimer(2.0f, ApplyManagedSlots, TimerFlags.REPEAT);
        StartFastApplyWindow();
        Server.PrintToConsole(
            $"[DemoTracer BotHider] loaded api={DemoTracerBotHiderContract.ApiVersion} " +
            $"provider_epoch={_presentation.GetProviderInfo().ProviderEpoch} " +
            "crosshair_writer=networked_on_demand");
    }

    public override void Unload(bool hotReload)
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        IsBotPatch.Api = null;
        StopFastApplyWindow();
        _presentation?.Dispose();
        _presentation = null;
        _client?.Dispose();
        _client = null;
    }

    private void OnMapStart(string mapName)
    {
        StopFastApplyWindow();
        _presentation?.ResetForMapBoundary();
        StartFastApplyWindow();
    }

    private void OnMapEnd()
    {
        StopFastApplyWindow();
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

            foreach (var legacyDirectoryName in new[] { "BotHiderImpl", "BotHider" })
            {
                var legacyDirectory = Path.Combine(pluginsDirectory, legacyDirectoryName);
                if (!Directory.Exists(legacyDirectory))
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
        _presentation?.InvalidateAll();
        StartFastApplyWindow();
        Server.NextFrame(ApplyManagedSlots);
        AddTimer(0.3f, RespawnDeadManagedBots, TimerFlags.STOP_ON_MAPCHANGE);
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

    private void SchedulePresentationReconcile(int slot)
    {
        _presentation?.InvalidateSlot(slot);
        StartFastApplyWindow();
        Server.NextFrame(() =>
        {
            _presentation?.InvalidateSlot(slot);
            ApplyManagedSlots();
        });
    }

    private void StartFastApplyWindow()
    {
        _fastApplyRemaining = Math.Max(_fastApplyRemaining, FastApplyTicks);
        if (_fastApplyTimer != null)
            return;
        _fastApplyTimer = AddTimer(
            FastApplyIntervalSeconds,
            RunFastApplyTick,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopFastApplyWindow()
    {
        _fastApplyTimer?.Kill();
        _fastApplyTimer = null;
        _fastApplyRemaining = 0;
    }

    private void RunFastApplyTick()
    {
        ApplyManagedSlots();
        _fastApplyRemaining--;
        if (_fastApplyRemaining > 0)
            return;
        _fastApplyTimer?.Kill();
        _fastApplyTimer = null;
    }

    private void ApplyManagedSlots()
        => _presentation?.PublishManagedSlots();

    private void RespawnDeadManagedBots()
    {
        if (_presentation == null)
            return;

        var tCount = 0;
        var ctCount = 0;
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true })
                continue;
            if (player.Team == CsTeam.Terrorist)
                tCount++;
            else if (player.Team == CsTeam.CounterTerrorist)
                ctCount++;
        }

        for (var slot = 0; slot < 64; slot++)
        {
            if (!_presentation.IsManagedBot(slot))
                continue;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || player.PawnIsAlive)
                continue;

            if (player.Team is not CsTeam.Terrorist and not CsTeam.CounterTerrorist)
            {
                var target = tCount <= ctCount ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                try
                {
                    player.SwitchTeam(target);
                    if (target == CsTeam.Terrorist)
                        tCount++;
                    else
                        ctCount++;
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole(
                        $"[DemoTracer BotHider] SwitchTeam failed slot={slot}: {ex.Message}");
                    continue;
                }
            }

            try
            {
                player.Respawn();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole(
                    $"[DemoTracer BotHider] respawn failed slot={slot}: {ex.Message}");
            }
        }
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
