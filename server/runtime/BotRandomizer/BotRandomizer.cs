using BotRandomizerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

public sealed partial class BotRandomizerPlugin : BasePlugin
{
    private static readonly PluginCapability<IBotRandomizerApi> ApiCapability =
        new(BotRandomizerContract.Capability);

    private readonly CosmeticStateStore _states = new();
    private readonly RandomizerOptions _options = new();
    private readonly HashSet<int> _pendingRerolls = [];
    private readonly string _providerEpoch = Guid.NewGuid().ToString("N");
    private readonly CosmeticWriteLeaseStore _writeLeases;
    private readonly BotRandomizerApiFacade _apiFacade;

    private CosmeticCatalog? _catalog;
    private ReplayEconIndex? _replayEconIndex;
    private CosmeticRoller? _roller;
    private CosmeticApplicator? _applicator;
    private WeaponItemViewStore? _weaponItemViews;
    private bool _giveNamedItemHooked;
    private bool _giveNamedItemErrorLogged;
    private bool _draining;
    private int _serverThreadId;
    private ulong _mapEpoch = 1;

    public BotRandomizerPlugin()
    {
        _writeLeases = new CosmeticWriteLeaseStore(_providerEpoch, InvalidateLeasePolicySlots);
        _apiFacade = new BotRandomizerApiFacade(this);
    }

    public override string ModuleName => "BotRandomizer";
    public override string ModuleVersion => "1.7.0";
    public override string ModuleAuthor => "ed0ard, Misaka17032, unicbm & XBribo";
    public override string ModuleDescription =>
        "Stable per-bot knives, gloves, weapon skins, stickers, charms, agents and music kits";

    public override void Load(bool hotReload)
    {
        _serverThreadId = Environment.CurrentManagedThreadId;
        _draining = false;
        LoadCatalog();
        LoadReplayEconIndex();
        LoadAttributeWriter();

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart, HookMode.Pre);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventItemPickup>(OnItemPickup);
        Capabilities.RegisterPluginCapability(ApiCapability, () => (IBotRandomizerApi)_apiFacade);
        if (_weaponItemViews?.NativeAvailable == true)
        {
            // The supported CSS host routes these hooks through Metamod's shared
            // KHook engine. Keep registration here; never add a private detour.
            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPre, HookMode.Pre);
            try
            {
                VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
            }
            catch
            {
                VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPre, HookMode.Pre);
                _weaponItemViews.Dispose();
                _weaponItemViews = null;
                _draining = true;
                throw;
            }
            _giveNamedItemHooked = true;
        }

        if (hotReload)
            RestoreAllBots(CosmeticScope.All);
    }

    public override void Unload(bool hotReload)
    {
        _draining = true;
        _writeLeases.Reset(countRevocation: true);
        _states.Reset();
        _pendingRerolls.Clear();
        if (_giveNamedItemHooked)
        {
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPre, HookMode.Pre);
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post);
            _giveNamedItemHooked = false;
        }

        _weaponItemViews?.Dispose();
        _weaponItemViews = null;
        BotRandomizerContract.NotifyProviderChanged();
    }

    public override void OnAllPluginsLoaded(bool hotReload)
        => BotRandomizerContract.NotifyProviderChanged();

    private void LoadCatalog()
    {
        try
        {
            var catalogPath = Path.Combine(ModuleDirectory, "cosmetic_catalog.json");
            var placementPath = Path.Combine(ModuleDirectory, "charm_placements.json");
            _catalog = CosmeticCatalog.Load(catalogPath);
            var charmPlacements = CharmPlacementCatalog.Load(placementPath, _catalog);
            _roller = new CosmeticRoller(_catalog, charmPlacements);
            Logger.LogInformation(
                "[BotRandomizer] Catalog {Commit}: {Weapons} weapons, {Paints} paints, {Stickers} stickers, {Charms} charms; {CharmPositions} charm positions for {CharmWeapons} weapons",
                _catalog.SourceCommit[..12],
                _catalog.WeaponCount,
                _catalog.WeaponPaintCount,
                _catalog.StickerKits.Count,
                _catalog.KeychainDefinitions.Count,
                charmPlacements.PlacementCount,
                charmPlacements.WeaponCount);
        }
        catch (Exception exception)
        {
            _catalog = null;
            _roller = null;
            Logger.LogError(
                exception,
                "[BotRandomizer] cosmetic_catalog.json or charm_placements.json is invalid; randomization disabled");
        }
    }

    private void LoadReplayEconIndex()
    {
        try
        {
            _replayEconIndex = ReplayEconIndex.Load(Path.Combine(ModuleDirectory, "cs2-lib-econ-index.v1.json"));
        }
        catch (Exception exception)
        {
            _replayEconIndex = null;
            Logger.LogError(exception,
                "[BotRandomizer] Replay econ index is invalid; replay plans disabled, randomization unaffected");
        }
    }

    private void OnMapStart(string mapName)
    {
        _mapEpoch++;
        _writeLeases.Reset(countRevocation: true);
        _states.Reset();
        _pendingRerolls.Clear();
        _roller?.ResetMap();
        // Keep constructed item-view storage alive across map transitions. Each view is
        // fully overwritten before reuse and is freed only at slot teardown or unload.
        _applicator?.Reset();
        foreach (var model in RandomizerAssets.CounterTerroristModels)
            Server.PrecacheModel(model);
        foreach (var model in RandomizerAssets.TerroristModels)
            Server.PrecacheModel(model);
    }

    private void OnClientDisconnect(int playerSlot)
    {
        _writeLeases.RevokeSlot(playerSlot);
        _states.Remove(playerSlot);
        _pendingRerolls.Remove(playerSlot);
        _weaponItemViews?.ClearSlot(playerSlot);
        _applicator?.ClearSlot(playerSlot);
    }

    private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        foreach (var player in Utilities.GetPlayers())
            ConsumePendingReroll(player);
        Server.NextFrame(ApplyIntroAgents);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        ConsumePendingReroll(@event.Userid);
        var state = GetOrCreateState(@event.Userid);
        if (state is not null)
            ScheduleRestore(state, CosmeticScope.All);

        return HookResult.Continue;
    }

    private HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        if (_applicator is null)
            return HookResult.Continue;
        if (string.IsNullOrEmpty(@event.Item)
            || (!@event.Item.Contains("knife", StringComparison.Ordinal)
                && !@event.Item.Contains("bayonet", StringComparison.Ordinal)))
        {
            return HookResult.Continue;
        }

        var state = GetOrCreateState(@event.Userid);
        if (state is null)
            return HookResult.Continue;

        if (TryGetWritePolicy(state, out var writePolicy) && writePolicy.Knife is not null)
        {
            ScheduleWearableRetry(state.Slot, state.UserId, state.Generation, 0.0f, CosmeticScope.Knife);
            return HookResult.Continue;
        }

        if (!_options.Knives)
            return HookResult.Continue;

        ScheduleKnifeSync(state.Slot, state.UserId, state.Generation, nextFrame: true);
        ScheduleKnifeSync(state.Slot, state.UserId, state.Generation, delay: 0.10f);
        ScheduleKnifeSync(state.Slot, state.UserId, state.Generation, delay: 0.25f);
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true, IsBot: true } || player.UserId is not int userId)
            return HookResult.Continue;

        if (@event.Disconnect)
        {
            OnClientDisconnect(player.Slot);
            return HookResult.Continue;
        }

        if (_roller is null || !IsPlayableTeam(@event.Team))
        {
            _states.BumpGeneration(player.Slot);
            return HookResult.Continue;
        }

        var state = _states.Reroll(
            player.Slot,
            userId,
            preserveMusic: true,
            music => _roller.RollLoadout((byte)@event.Team, music));

        var slot = player.Slot;
        var generation = state.Generation;
        AddTimer(
            0.10f,
            () =>
            {
                if (TryResolveCurrentBot(slot, userId, generation, out _, out _, out _))
                    RestoreBot(slot, CosmeticScope.Agent | CosmeticScope.Knife | CosmeticScope.Gloves);
            },
            TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var state = GetOrCreateState(@event.Userid);
        var player = @event.Userid;
        if (state is null || player is null)
        {
            return HookResult.Continue;
        }

        TryGetWritePolicy(state, out var writePolicy);
        var musicKit = _options.ResolveMusicKit(writePolicy, state.Loadout);
        ApplyMusicKit(player, musicKit, 0);
        @event.Musickitid = musicKit;
        @event.Musickitmvps = 0;
        @event.Nomusic = musicKit == 0 ? 1 : 0;
        return HookResult.Continue;
    }

    private SlotCosmeticState? GetOrCreateState(CCSPlayerController? player)
    {
        if (_draining || _roller is null
            || player is not { IsValid: true, IsBot: true, IsHLTV: false }
            || player.UserId is not int userId
            || !IsPlayableTeam(player.TeamNum)
            || player.PlayerPawn?.Value is { IsValid: true } pawn &&
               IsPawnControlledByAnotherController(player.Slot, pawn))
        {
            return null;
        }

        var team = (byte)player.TeamNum;
        return _states.GetOrCreate(
            player.Slot,
            userId,
            team,
            music => _roller.RollLoadout(team, music));
    }

    private void ApplyIdentity(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        SlotCosmeticState state,
        CosmeticScope scope)
    {
        if (_applicator is null)
            return;
        TryGetWritePolicy(state, out var writePolicy);

        if ((scope & CosmeticScope.Agent) != 0 &&
            _options.ResolveAgentModel(writePolicy, state.Loadout) is { } model)
        {
            _applicator.ApplyAgent(pawn, model);
        }

        if ((scope & CosmeticScope.MusicKit) != 0)
        {
            ApplyMusicKit(player, _options.ResolveMusicKit(writePolicy, state.Loadout), 0);
        }
    }

    private void ApplyWearables(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        SlotCosmeticState state,
        CosmeticScope scope = CosmeticScope.Knife | CosmeticScope.Gloves)
    {
        if (_applicator is null
            || player is not { IsValid: true, IsBot: true }
            || !pawn.IsValid)
        {
            return;
        }
        TryGetWritePolicy(state, out var writePolicy);

        if ((scope & CosmeticScope.Knife) != 0)
        {
            if (writePolicy?.Knife is { } replayKnife)
                _applicator.ApplyKnife(player, pawn, replayKnife);
            else if (_options.Knives)
                _applicator.ApplyKnife(player, pawn, state.Loadout.Knife);
        }

        if ((scope & CosmeticScope.Gloves) != 0)
        {
            var applied = writePolicy?.Gloves is { } replayGloves
                ? _applicator.ApplyGloves(player, pawn, replayGloves)
                : _options.Gloves && _applicator.ApplyGloves(player, pawn, state.Loadout.Glove);
            if (applied)
            {
                var generation = state.Generation;
                var pawnHandle = pawn.Handle;
                AddTimer(0.2f, () =>
                {
                    if (TryResolveCurrentBot(
                            state.Slot,
                            state.UserId,
                            generation,
                            out _,
                            out var currentPawn,
                            out _)
                        && currentPawn.Handle == pawnHandle)
                    {
                        _applicator.ShowGloves(currentPawn);
                    }
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
    }

    private void ScheduleWearableRetry(
        int slot,
        int userId,
        long generation,
        float delay,
        CosmeticScope scope = CosmeticScope.Knife | CosmeticScope.Gloves)
    {
        AddTimer(delay, () =>
        {
            if (TryResolveCurrentBot(slot, userId, generation, out var player, out var pawn, out var state))
                ApplyWearables(player, pawn, state, scope);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ScheduleKnifeSync(
        int slot,
        int userId,
        long generation,
        float? delay = null,
        bool nextFrame = false)
    {
        void Callback()
        {
            if (_options.Knives && _applicator is not null
                && TryResolveCurrentBot(slot, userId, generation, out _, out var pawn, out var state)
                && !(TryGetWritePolicy(state, out var writePolicy) && writePolicy.Knife is not null))
            {
                _applicator.SyncPickedUpKnife(pawn);
            }
        }

        if (nextFrame)
            Server.NextFrame(Callback);
        else if (delay is float seconds)
            AddTimer(seconds, Callback, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool TryResolveCurrentBot(
        int slot,
        int userId,
        long generation,
        out CCSPlayerController player,
        out CCSPlayerPawn pawn,
        out SlotCosmeticState state)
    {
        player = null!;
        pawn = null!;
        state = null!;
        if (_draining || !_states.IsCurrent(slot, userId, generation))
            return false;

        var resolved = Utilities.GetPlayerFromSlot(slot);
        if (resolved is not { IsValid: true, IsBot: true, IsHLTV: false }
            || resolved.UserId != userId
            || resolved.PlayerPawn?.Value is not { IsValid: true } resolvedPawn
            || IsPawnControlledByAnotherController(slot, resolvedPawn)
            || !_states.TryGet(slot, out var resolvedState))
        {
            return false;
        }

        player = resolved;
        pawn = resolvedPawn;
        state = resolvedState;
        return true;
    }

    private static bool IsPawnControlledByAnotherController(
        int botSlot,
        CCSPlayerPawn pawn)
    {
        foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>(
                     "cs_player_controller"))
        {
            if (controller is not { IsValid: true } || controller.Slot == botSlot)
                continue;

            bool controllingBot;
            try
            {
                controllingBot = controller.ControllingBot;
            }
            catch
            {
                continue;
            }

            if (!controllingBot)
                continue;

            if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } controlledPawn &&
                controlledPawn.Value.Index == pawn.Index)
            {
                return true;
            }

            if (controller.OriginalControllerOfCurrentPawn is { IsValid: true, Value.IsValid: true } original &&
                original.Value.Slot == botSlot)
            {
                return true;
            }
        }

        return false;
    }

    private void RestoreBot(int slot, CosmeticScope scope)
    {
        var state = GetOrCreateState(Utilities.GetPlayerFromSlot(slot));
        if (state is not null)
            ScheduleRestore(state, scope);
    }

    private void ScheduleRestore(SlotCosmeticState state, CosmeticScope scope)
    {
        var slot = state.Slot;
        var userId = state.UserId;
        var generation = state.Generation;
        Server.NextFrame(() =>
        {
            if (!TryResolveCurrentBot(slot, userId, generation, out var currentPlayer, out var pawn, out var current))
                return;

            ApplyIdentity(currentPlayer, pawn, current, scope);
            var wearableScope = scope & (CosmeticScope.Knife | CosmeticScope.Gloves);
            if (wearableScope != CosmeticScope.None)
            {
                ApplyWearables(currentPlayer, pawn, current, wearableScope);
                ScheduleWearableRetry(slot, userId, generation, 0.10f, wearableScope);
                ScheduleWearableRetry(slot, userId, generation, 0.25f, wearableScope);
            }
        });
    }

    private void RestoreAllBots(CosmeticScope scope)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is { IsValid: true, IsBot: true, IsHLTV: false })
                RestoreBot(player.Slot, scope);
        }
    }

    [ConsoleCommand("bot_randomizer", "Bot Improver Panel cosmetic controls")]
    public void OnControlCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not null || command.ArgCount < 4)
            return;

        // Pending spawn callbacks read the current options. Changing defaults
        // neither cancels DTR plans nor rebuilds an already constructed item.
        _options.TryApplyControl(command.GetArg(1), command.GetArg(2), command.GetArg(3));
    }

    [ConsoleCommand("br_reroll", "Queue new loadouts for the next safe spawn")]
    public void OnRerollCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_roller is null)
        {
            command.ReplyToCommand("Cosmetic catalog is unavailable.");
            return;
        }

        var target = command.ArgCount >= 2 ? command.GetArg(1) : "all";
        int? targetSlot = null;
        if (target != "all")
        {
            if (!int.TryParse(target, out var slot))
            {
                command.ReplyToCommand("Usage: br_reroll [all|bot slot]");
                return;
            }
            targetSlot = slot;
        }

        var bots = Utilities.GetPlayers()
            .Where(bot =>
                bot is { IsValid: true, IsBot: true, IsHLTV: false }
                && bot.UserId is not null
                && IsPlayableTeam(bot.TeamNum)
                && (targetSlot is null || bot.Slot == targetSlot.Value))
            .ToArray();

        if (bots.Length == 0)
        {
            command.ReplyToCommand("No matching bot slots.");
            return;
        }

        foreach (var bot in bots)
            _pendingRerolls.Add(bot.Slot);

        command.ReplyToCommand(
            $"Queued {bots.Length} bot loadout(s) for the next safe spawn.");
    }

    private void ConsumePendingReroll(CCSPlayerController? player)
    {
        if (_roller is null
            || player is not { IsValid: true, IsBot: true, IsHLTV: false }
            || player.UserId is not int userId
            || !IsPlayableTeam(player.TeamNum)
            || !_pendingRerolls.Remove(player.Slot))
        {
            return;
        }

        var team = (byte)player.TeamNum;
        _states.Reroll(
            player.Slot,
            userId,
            preserveMusic: false,
            music => _roller.RollLoadout(team, music));
    }

    private static bool IsPlayableTeam(int team)
        => team is RandomizerAssets.TerroristTeam or RandomizerAssets.CounterTerroristTeam;

    private static void ApplyMusicKit(CCSPlayerController player, int kitId, int musicKitMvps)
    {
        // These are engine-managed schema values in current CS2/CSS builds.
        // They are writable but not networked fields, so SetStateChanged would
        // only be rejected and logged by CounterStrikeSharp.
        var inventory = player.InventoryServices;
        if (inventory is not null)
            inventory.MusicID = checked((ushort)kitId);

        player.MusicKitID = kitId;
        player.MusicKitMVPs = musicKitMvps;
        player.MvpNoMusic = kitId == 0;
    }

    private bool TryGetWritePolicy(
        SlotCosmeticState state,
        out CosmeticWritePolicy policy)
    {
        if (_writeLeases.TryGetPolicy(state.Slot, state.Incarnation, out var resolved, out _) &&
            resolved.SpawnTeam == state.Loadout.Team)
        {
            policy = resolved;
            return true;
        }

        policy = null!;
        return false;
    }

    private static bool IsSwitchingTeamsAtRoundReset()
    {
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            return proxy is { IsValid: true } &&
                   proxy.GameRules != null &&
                   proxy.GameRules.SwitchingTeamsAtRoundReset;
        }
        catch
        {
            return false;
        }
    }

}
