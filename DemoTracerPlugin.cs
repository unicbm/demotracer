using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using DemoTracerBotHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin : BasePlugin
{
    public override string ModuleName => "CS2 DemoTracer";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "unicbm";
    public override string ModuleDescription => "Trace CS2 demos into bot-executable route replays.";

    public DemoTracerPlugin()
    {
        _apiFacade = new DemoTracerApiFacade(this);
    }

    private static readonly PluginCapability<IDemoTracerApi> ApiCapability = new("demotracer:api");
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static string _moduleDirectoryForPathResolution = string.Empty;
    private const float HandoffGraceSeconds = 0.25f;
    private const float BulletHandoffMatchSeconds = 0.25f;
    private const int BulletHandoffMinDamage = 1;
    private const float HandoffThreat360DefaultRange = 420.0f;
    private const float HandoffThreat360MinRange = 150.0f;
    private const float HandoffThreat360MaxRange = 800.0f;
    private const float HandoffThreat360ImmediateRange = 240.0f;
    private const float HandoffThreat360HoldSeconds = 0.08f;
    private const float HandoffThreat360MaxVerticalDelta = 128.0f;
    private const float HandoffThreat360ChestZScale = 0.62f;
    private const int ProjectileAlignMatchAttempts = 8;
    private const int ProjectileAlignDefaultTotalWrites = 1;
    private const int ProjectileAlignUntilDelete = -1;
    private const int ProjectileAlignMaxTotalWrites = 512;
    private const float ReplayMusicKitReconcileDelaySeconds = 0.10f;
    private const int ProjectileAlignLogMaxEntries = 128;
    private const float FireProjectileAlignMaxInitialPositionDistance = 128.0f;
    private const int MolotovPointAlignDefaultLeadTicks = 1;
    private const int MolotovPointAlignMaxLeadTicks = 8;
    private const int MinManifestAbiVersion = 12;
    private const int MaxManifestAbiVersion = 17;
    private const int MaxPlayerSlots = BotControllerNative.MaxSlots;
    private const int ReplayStartHealth = 100;
    private const int StandardTeamSize = 5;
    private const int InitialSpawnAssignmentMaxAttempts = 8;
    private const int WeaponSlotReplacementClearWaitFrames = 8;
    private const int WeaponSlotReplacementGrantWaitFrames = 4;
    private const int WeaponSlotReplacementFallbackWaitFrames = 4;
    private const float PlayerHullWidth = 32.0f;
    private const float PlayerHullHeight = 72.0f;
    private const string AvatarOverrideCacheDirectoryName = "avatar-cache";
    private const int AvatarOverrideMaxBytes = 16 * 1024;
    private static readonly byte[] AvatarPngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private const string FreezeTimeConVarName = "mp_freezetime";
    private static readonly Version MaxVerifiedManagedSchemaPatch = new(1, 41, 7, 3);
    private static readonly Lazy<(bool Allowed, string Patch)> ManagedSchemaRuntime =
        new(DetectManagedSchemaRuntime);
    private const string CosmeticRiskNotice = "[DTR WARN] cosmetic alignment consumes opt-in manifest cosmetics evidence and may carry Valve GSLT/server-guideline risk outside local/private replay validation.";
    private const string LeftHandDesiredFidelityNotice = "[DTR WARN] left_hand_desired=off 会降低保真度，但显著增高handoff流畅性。Reload loaded replays or plans for this setting to apply.";

    private readonly List<int> _loadedSlots = new();
    private readonly HashSet<int> _warmReplayBufferSlots = new();
    private readonly HashSet<int> _demoTracerOwnedSlots = new();
    private readonly HashSet<int> _freezePrerollSlots = new();
    private readonly HashSet<int> _resumedFreezePrerollSlots = new();
    private readonly Dictionary<int, LoadedReplay> _loadedReplays = new();
    private readonly Dictionary<int, int> _lastEnsuredWeaponDef = new();
    private readonly Dictionary<int, int> _lastReplayWeaponDef = new();
    private readonly Dictionary<int, int> _lastLockedWeaponTarget = new();
    private readonly Dictionary<int, PendingWeaponAlign> _pendingWeaponAlign = new();
    private readonly Dictionary<(int PlayerSlot, ReplayWeaponSlot WeaponSlot), PendingWeaponSlotReplacement>
        _pendingWeaponSlotReplacements = new();
    private readonly Dictionary<int, int> _projectileAlignNextBySlot = new();
    private readonly Dictionary<int, int> _replayHifiEventNextBySlot = new();
    private readonly Dictionary<int, long> _replayIdentityGenerationBySlot = new();
    private readonly Dictionary<int, long> _replayMutationGenerationBySlot = new();
    private readonly Dictionary<uint, PendingProjectileAlign> _pendingProjectileAlign = new();
    private readonly List<KeyValuePair<uint, PendingProjectileAlign>> _pendingProjectileAlignTickScratch = new();
    private readonly Queue<string> _projectileAlignLog = new();
    private readonly List<TrackedDroppedReplayItem> _trackedDroppedReplayItems = new();
    private readonly HashSet<int> _rebuiltInventorySlots = new();
    private readonly HashSet<int> _loadoutSyncedSlots = new();
    private readonly HashSet<int> _lastPlayingSlots = new();
    private readonly Dictionary<int, float> _replayStartedAt = new();
    private readonly Dictionary<int, uint> _replayPerceptionBaselineSerial = new();
    private readonly Dictionary<int, PendingBulletHit> _pendingBulletHits = new();
    private readonly Dictionary<int, PendingBulletDamage> _pendingBulletDamages = new();
    private readonly Dictionary<int, PendingThreat360> _pendingThreat360 = new();
    private readonly Dictionary<uint, UtilityProjectileTrace> _utilityTraceProjectiles = new();
    private readonly Dictionary<int, ReplayMusicKitBaseline> _replayMusicKitBaselines = new();
    private readonly Dictionary<int, long> _replayMusicKitRepairTokens = new();
    private long _nextReplayMusicKitRepairToken;
    private readonly HashSet<int> _cosmeticSyncedSlots = new();
    private readonly Dictionary<int, AppliedActiveWeaponCosmetic> _activeWeaponCosmetics = new();
    private readonly HashSet<int> _scoreboardSyncedSlots = new();
    private readonly Dictionary<int, ReplayViewmodel> _replayOriginalViewmodels = new();
    private readonly Dictionary<int, ReplayViewmodel> _replayAppliedViewmodels = new();
    private readonly HashSet<int> _replayFailedViewmodelSlots = new();
    private readonly DemoTracerBotHiderBridge _botHiderBridge = new();
    private readonly RayTraceLosProbe _rayTraceLosProbe = new();
    private bool _safeC4Aligned;
    private int _initialSpawnAssignmentToken;
    private bool _initialSpawnAssignmentComplete;
    private bool _initialSpawnAssignmentScheduled;
    private readonly DemoTracerApiFacade _apiFacade;
    private StreamWriter? _utilityTraceWriter;
    private string _utilityTracePath = string.Empty;
    private bool _utilityTraceEnabled;
    private ulong _lastReplayPovMask = ulong.MaxValue;

    private bool _armed;
    private bool _armedLoop;
    private string _armedLabel = string.Empty;
    private string _armedManifestPath = string.Empty;
    private int _armedSourceRound = -1;
    private bool _armedPrepared;
    private int _armedPreparePollToken;
    private int _freezePrerollToken;
    private bool _freezePrerollStarted;

    private bool _sequenceActive;
    private string _sequenceManifestPath = string.Empty;
    private int[] _sequenceRounds = [];
    private int _sequenceIndex;
    private bool _sequencePrepared;
    private int _sequencePreparedRound = -1;
    private int _sequencePreparePollToken;
    private ReplayRoundScoreboard? _loadedRoundScoreboard;
    private bool _weaponAlignEnabled = true;
    private bool _projectileAlignEnabled = true;
    private int _projectileAlignTotalWrites = ProjectileAlignDefaultTotalWrites;
    private MolotovPointAlignMode _molotovPointAlignMode = MolotovPointAlignMode.Detonate;
    private int _molotovPointAlignLeadTicks = MolotovPointAlignDefaultLeadTicks;
    private bool _cosmeticAlignEnabled;
    private bool _cosmeticWeaponsEnabled;
    private bool _cosmeticKnivesEnabled;
    private bool _cosmeticGlovesEnabled;
    private bool _cosmeticNamesEnabled;
    private bool _cosmeticAgentsEnabled;
    private bool _preserveNativeBotCosmetics;
    private bool _stickerAlignEnabled;
    private bool _charmAlignEnabled;
    private bool _crosshairAlignEnabled = true;
    private bool _scoreboardAlignEnabled;
    private bool _leftHandDesiredEnabled = true;
    private bool _weaponAlignFrameQueued;
    private int _cosmeticAppliedCount;
    private int _cosmeticSkippedCount;
    private int _stickerAppliedCount;
    private int _stickerSkippedCount;
    private int _charmAppliedCount;
    private int _charmSkippedCount;
    private int _scoreboardAppliedCount;
    private int _scoreboardSkippedCount;
    private HandoffMode _handoffMode = HandoffMode.DeathContactC4;
    private bool _handoffAllSlots;
    private bool _handoffThreat360Enabled = true;
    private float _handoffThreat360Range = HandoffThreat360DefaultRange;
    private bool _handoffThreat360LosEnabled = true;
    private bool _partialReplayEnabled = true;
    private ReplayIdentityMode _replayIdentityMode = ReplayIdentityMode.Steam;
    private long _nextReplayIdentityGeneration;
    private long _nextReplayMutationGeneration;
    private bool _mapActive = true;
    private bool _lifecycleResetInProgress;

    public override void Load(bool hotReload)
    {
        RegisterControlCommandAuthorization();
        RegisterReplayRetentionJoinHook();
        _moduleDirectoryForPathResolution = ModuleDirectory;
        LoadRuntimeConfig(message => Server.PrintToConsole(message), announceMissing: true);
        if (!ManagedSchemaWritesAllowed())
        {
            Server.PrintToConsole(
                $"dtr: compatibility safe mode active for CS2 {ManagedSchemaRuntime.Value.Patch}; " +
                $"managed presentation/schema writes are verified only through {MaxVerifiedManagedSchemaPatch}");
        }
        LoadDemoTracerEconIndex();
        HookCosmeticGiveNamedItem();
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        Capabilities.RegisterPluginCapability(ApiCapability, () => (IDemoTracerApi)_apiFacade);
        ConfigureNativeSafetyOffsets();
        ConfigureNativeProjectileBirthAlignOffsets();
        StartRuntimeHealthHeartbeat();
        Server.PrintToConsole("dtr: CSS control plugin loaded");
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _botHiderBridge.Refresh();
        _ = _botHiderBridge.ReleaseOwner(DemoTracerBotHiderContract.DemoTracerOwner);
        _ = SyncBotHiderPresentationLease(announce: _loadedSlots.Count > 0);
        RefreshRuntimeHealthHeartbeat();
    }

    public override void Unload(bool hotReload)
    {
        StopRuntimeHealthHeartbeat();
        UnregisterReplayRetentionJoinHook();
        UnhookCosmeticGiveNamedItem();
        ClearReplayStateForLifecycle(hotReload ? "plugin_reload" : "plugin_unload");
        StopUtilityTrace();
        BotControllerNative.ClearAllBuyPlans();
        _botHiderBridge.Refresh();
    }

    private void OnMapStart(string mapName)
    {
        _mapActive = true;
        ClearReplayStateForLifecycle($"map_start:{mapName}");
    }

    private void OnMapEnd()
    {
        _mapActive = false;
        ClearReplayStateForLifecycle("map_end");
    }

    private void OnClientDisconnect(int playerSlot)
    {
        if (playerSlot < 0 || playerSlot >= MaxPlayerSlots)
            return;

        ForgetRetainedBotHiderPresentation(playerSlot);
        ClearReplayCrosshairPresentationEntry(playerSlot);
        _slotCosmeticEvidenceKeys.Remove(playerSlot);
        _appliedGloveCosmetics.Remove(playerSlot);
        _gloveCosmeticTokens.Remove(playerSlot);
        _appliedKnifeCosmeticBirths.Remove(playerSlot);
        _pendingKnifeEntityRefreshes.Remove(playerSlot);
        _knifeEntityRefreshUnavailableWarnings.Remove(playerSlot);

        if (!HasReplayLifecycleState(includeNative: true))
            return;

        if (!IsDisconnectingReplaySlot(playerSlot))
            return;

        ClearDisconnectedReplaySlot(playerSlot, $"client_disconnect:{playerSlot}");
    }

    private bool IsDisconnectingReplaySlot(int slot)
    {
        if (_loadedSlots.Contains(slot) ||
            _warmReplayBufferSlots.Contains(slot) ||
            _demoTracerOwnedSlots.Contains(slot) ||
            _loadedReplays.ContainsKey(slot) ||
            _lastPlayingSlots.Contains(slot) ||
            _retainedReplayViewmodelSlots.Contains(slot))
        {
            return true;
        }

        if (!BotControllerNative.IsCompatible)
            return false;

        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing || state.Total > 0;
    }

    private void ClearDisconnectedReplaySlot(int slot, string reason)
    {
        BotControllerNative.UnloadReplay(slot);
        _warmReplayBufferSlots.Remove(slot);
        ReleaseReplaySlot(slot, reason);
        _loadedSlots.Remove(slot);
        ForgetLoadedReplayMetadata(slot);
    }

    private static void ConfigureNativeSafetyOffsets()
    {
        try
        {
            var offset = Schema.GetSchemaOffset("CCSPlayerController", "m_bControllingBot");
            var ok = BotControllerNative.SetControllerControllingBotOffset(offset);
            Server.PrintToConsole(ok
                ? $"dtr: native takeover guard enabled, ControllingBot offset=0x{offset:X}"
                : "dtr: native takeover guard unavailable; CSS safety checks remain active");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: native takeover guard unavailable: {ex.Message}");
        }
    }

    private static void ConfigureNativeProjectileBirthAlignOffsets()
    {
        try
        {
            var initialPositionOffset = Schema.GetSchemaOffset(
                "CBaseCSGrenadeProjectile",
                "m_vInitialPosition");
            var initialVelocityOffset = Schema.GetSchemaOffset(
                "CBaseCSGrenadeProjectile",
                "m_vInitialVelocity");
            var rc = BotControllerNative.SetProjectileBirthAlignOffsets(
                initialPositionOffset,
                initialVelocityOffset);
            Server.PrintToConsole(rc == 0
                ? $"dtr: native projectile birth align enabled, initial_position=0x{initialPositionOffset:X} initial_velocity=0x{initialVelocityOffset:X}"
                : $"dtr: native projectile birth align unavailable rc={rc}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: native projectile birth align unavailable: {ex.Message}");
        }
    }

    [ConsoleCommand("dtr_weapon_align", "dtr_weapon_align <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void WeaponAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            SetWeaponAlignEnabled(ParseOnOff(command.GetArg(1), _weaponAlignEnabled));

        command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align weapons <on|off>");
        command.ReplyToCommand($"dtr: weapon_align={_weaponAlignEnabled}");
    }

    [ConsoleCommand("dtr_projectile_align", "dtr_projectile_align <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ProjectileAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            SetProjectileAlignEnabled(ParseOnOff(command.GetArg(1), _projectileAlignEnabled));

        command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align projectiles <on|off>");
        command.ReplyToCommand($"dtr: projectile_align={_projectileAlignEnabled} ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks}");
    }

    [ConsoleCommand("dtr_projectile_align_ticks", "dtr_projectile_align_ticks <status|default|once|2..512|until_delete>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ProjectileAlignTicksCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            if (!TryParseProjectileAlignTicks(command.GetArg(1), out var ticks))
            {
                command.ReplyToCommand("usage: dtr_projectile_align_ticks <status|default|once|2..512|until_delete>");
                return;
            }

            if (ticks != int.MinValue)
                SetProjectileAlignTicks(ticks);
        }

        command.ReplyToCommand($"dtr: projectile_align_ticks={FormatProjectileAlignTicks()}");
    }

    [ConsoleCommand("dtr_molotov_align_point", "dtr_molotov_align_point <status|off|teleport|detonate> [lead_ticks]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void MolotovAlignPointCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            if (!TryParseMolotovPointAlignMode(command.GetArg(1), out var mode))
            {
                command.ReplyToCommand("usage: dtr_molotov_align_point <status|off|teleport|detonate> [lead_ticks]");
                return;
            }

            if (mode.HasValue)
                _molotovPointAlignMode = mode.Value;
        }

        if (command.ArgCount >= 3)
        {
            if (!int.TryParse(command.GetArg(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var leadTicks) ||
                leadTicks < 0 ||
                leadTicks > MolotovPointAlignMaxLeadTicks)
            {
                command.ReplyToCommand($"usage: dtr_molotov_align_point <status|off|teleport|detonate> [0..{MolotovPointAlignMaxLeadTicks}]");
                return;
            }

            _molotovPointAlignLeadTicks = leadTicks;
        }

        command.ReplyToCommand(
            $"dtr: molotov_align_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)} lead_ticks={_molotovPointAlignLeadTicks}");
    }

    [ConsoleCommand("dtr_projectile_align_log", "dtr_projectile_align_log [clear|all|molotov|fire]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ProjectileAlignLogCommand(CCSPlayerController? player, CommandInfo command)
    {
        var mode = command.ArgCount >= 2 ? command.GetArg(1).Trim().ToLowerInvariant() : "all";
        if (mode is "clear" or "reset")
        {
            _projectileAlignLog.Clear();
            command.ReplyToCommand("dtr: projectile_align_log cleared");
            return;
        }

        var filterFire = mode is "molotov" or "fire" or "incendiary" or "incgrenade";
        var lines = _projectileAlignLog
            .Where(line => !filterFire || line.Contains("kind=Molotov", StringComparison.OrdinalIgnoreCase))
            .TakeLast(20)
            .ToArray();
        if (lines.Length == 0)
        {
            command.ReplyToCommand(filterFire
                ? "dtr: no recent molotov projectile align events"
                : "dtr: no recent projectile align events");
            return;
        }

        command.ReplyToCommand($"dtr: projectile_align_log showing {lines.Length} recent event(s)");
        foreach (var line in lines)
            command.ReplyToCommand($"dtr: {line}");
    }

    [ConsoleCommand("dtr_cosmetic_align", "dtr_cosmetic_align <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void CosmeticAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            SetCosmeticAlignEnabled(ParseOnOff(command.GetArg(1), _cosmeticAlignEnabled));

        command.ReplyToCommand("[DTR WARN] legacy command: cosmetics moved out of align. Use dtr_cosmetics basic|full");
        command.ReplyToCommand($"dtr: cosmetic_align={_cosmeticAlignEnabled}");
        if (_cosmeticAlignEnabled)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    [ConsoleCommand("dtr_sticker_align", "dtr_sticker_align <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StickerAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            SetStickerAlignEnabled(ParseOnOff(command.GetArg(1), _stickerAlignEnabled));

        command.ReplyToCommand("[DTR WARN] legacy command: use dtr_cosmetics stickers <on|off>");
        command.ReplyToCommand($"dtr: sticker_align={_stickerAlignEnabled}");
        if (_stickerAlignEnabled)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    [ConsoleCommand("dtr_charm_align", "dtr_charm_align <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void CharmAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            SetCharmAlignEnabled(ParseOnOff(command.GetArg(1), _charmAlignEnabled));

        command.ReplyToCommand("[DTR WARN] legacy command: use dtr_cosmetics charms <on|off>");
        command.ReplyToCommand($"dtr: charm_align={_charmAlignEnabled}");
        if (_charmAlignEnabled)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    [ConsoleCommand("dtr_crosshair_align", "dtr_crosshair_align <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void CrosshairAlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            SetCrosshairAlignEnabled(ParseOnOff(command.GetArg(1), _crosshairAlignEnabled));

        command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align crosshair <on|off>");
        command.ReplyToCommand($"dtr: crosshair_align={_crosshairAlignEnabled}");
    }

    [ConsoleCommand("dtr_left_hand_desired", "dtr_left_hand_desired <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void LeftHandDesiredCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            ApplyLeftHandDesiredMode(ParseOnOff(command.GetArg(1), _leftHandDesiredEnabled), command.ReplyToCommand);

        command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align left_hand <on|off>");
        command.ReplyToCommand($"dtr: left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)}");
    }

    [ConsoleCommand("dtr_align", "dtr_align [status|default|full|handoff_safe|off|weapons|projectiles|left_hand|crosshair] [on|off]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void AlignCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2 ||
            command.GetArg(1).Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ReplyAlignStatus(command.ReplyToCommand);
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        switch (mode)
        {
            case "default":
                ApplyReplayFidelityPreset(
                    weapons: true,
                    projectiles: true,
                    leftHandDesired: true,
                    crosshair: true,
                    command.ReplyToCommand);
                return;
            case "full":
                ApplyReplayFidelityPreset(
                    weapons: true,
                    projectiles: true,
                    leftHandDesired: true,
                    crosshair: true,
                    command.ReplyToCommand);
                return;
            case "handoff_safe":
            case "handoff-safe":
            case "handoff":
                ApplyReplayFidelityPreset(
                    weapons: true,
                    projectiles: true,
                    leftHandDesired: false,
                    crosshair: true,
                    command.ReplyToCommand);
                return;
            case "off":
            case "none":
            case "movement":
            case "movement_only":
            case "movement-only":
                ApplyReplayFidelityPreset(
                    weapons: false,
                    projectiles: false,
                    leftHandDesired: false,
                    crosshair: false,
                    command.ReplyToCommand);
                return;
            default:
                if (command.ArgCount < 3)
                {
                    ReplyUnknownAlignTarget(command.GetArg(1), command.ReplyToCommand);
                    return;
                }
                if (SetAlignComponent(command.GetArg(1), ParseOnOff(command.GetArg(2), false), command.ReplyToCommand))
                {
                    ReplyAlignStatus(command.ReplyToCommand);
                    return;
                }
                ReplyUnknownAlignTarget(command.GetArg(1), command.ReplyToCommand);
                return;
        }
    }

    [ConsoleCommand("dtr_match", "dtr_match [status|off|scoreboard|scoreboard <on|off>|full]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void MatchCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2 ||
            command.GetArg(1).Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ReplyMatchStatus(command.ReplyToCommand);
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        switch (mode)
        {
            case "off":
            case "none":
                ApplyMatchPreset(scoreboard: false);
                ReplyMatchStatus(command.ReplyToCommand);
                return;
            case "full":
            case "all":
            case "scoreboard":
            case "scoreboards":
            case "scores":
            case "stats":
                var enabled = command.ArgCount >= 3
                    ? ParseOnOff(command.GetArg(2), _scoreboardAlignEnabled)
                    : true;
                ApplyMatchPreset(scoreboard: enabled);
                ReplyMatchStatus(command.ReplyToCommand);
                return;
            default:
                command.ReplyToCommand($"[DTR ERR] unknown dtr_match target: {mode}");
                command.ReplyToCommand("usage: dtr_match [status|off|scoreboard|scoreboard <on|off>|full]");
                command.ReplyToCommand("hint: replay fidelity settings moved to dtr_align");
                return;
        }
    }

    [ConsoleCommand("dtr_cosmetics", "dtr_cosmetics [status|off|weapons|basic|full|weapons|knives|gloves|names|agents|stickers|charms|preserve_native] [on|off]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void CosmeticsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2 ||
            command.GetArg(1).Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ReplyCosmeticsStatus(command.ReplyToCommand);
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        switch (mode)
        {
            case "off":
            case "none":
                ApplyCosmeticPreset(CosmeticPreset.Off);
                ReplyCosmeticsStatus(command.ReplyToCommand);
                return;
            case "weapons":
            case "weapon":
            case "skins":
            case "skin":
                if (command.ArgCount >= 3)
                {
                    SetCosmeticComponent(mode, ParseOnOff(command.GetArg(2), _cosmeticWeaponsEnabled), command.ReplyToCommand);
                }
                else
                {
                    ApplyCosmeticPreset(CosmeticPreset.Weapons);
                }
                ReplyCosmeticsStatus(command.ReplyToCommand);
                if (_cosmeticAlignEnabled)
                    command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "basic":
                ApplyCosmeticPreset(CosmeticPreset.Basic);
                ReplyCosmeticsStatus(command.ReplyToCommand);
                command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "full":
            case "all":
                ApplyCosmeticPreset(CosmeticPreset.Full);
                ReplyCosmeticsStatus(command.ReplyToCommand);
                command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "knives":
            case "knife":
            case "gloves":
            case "glove":
            case "names":
            case "name":
            case "custom_name":
            case "custom-name":
            case "agents":
            case "agent":
            case "models":
            case "model":
            case "stickers":
            case "sticker":
            case "charms":
            case "charm":
            case "keychains":
            case "keychain":
            case "preserve_native":
            case "preserve-native":
            case "preserve_bot":
            case "preserve-bot":
            case "native":
                if (command.ArgCount < 3)
                {
                    command.ReplyToCommand($"usage: dtr_cosmetics {mode} <on|off>");
                    return;
                }
                SetCosmeticComponent(mode, ParseOnOff(command.GetArg(2), false), command.ReplyToCommand);
                ReplyCosmeticsStatus(command.ReplyToCommand);
                if (_cosmeticAlignEnabled)
                    command.ReplyToCommand(CosmeticRiskNotice);
                return;
            default:
                command.ReplyToCommand($"[DTR ERR] unknown dtr_cosmetics preset: {mode}");
                command.ReplyToCommand("usage: dtr_cosmetics [status|off|weapons|basic|full]");
                command.ReplyToCommand("usage: dtr_cosmetics <weapons|knives|gloves|names|agents|stickers|charms|preserve_native> <on|off>");
                command.ReplyToCommand("hint: scoreboard moved to dtr_match");
                return;
        }
    }

    [ConsoleCommand("dtr_handoff", "dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [all|slot]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void HandoffCommand(CCSPlayerController? player, CommandInfo command)
        => SetHandoffMode(command, argOffset: 1);

    private void SetHandoffMode(CommandInfo command, int argOffset)
    {
        if (command.ArgCount > argOffset)
        {
            if (!TryParseHandoffMode(command.GetArg(argOffset), out var mode))
            {
                command.ReplyToCommand("usage: dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [all|slot]");
                return;
            }
            _handoffMode = mode;
        }

        if (command.ArgCount > argOffset + 1)
        {
            var scope = command.GetArg(argOffset + 1);
            if (scope.Equals("slot", StringComparison.OrdinalIgnoreCase))
                _handoffAllSlots = false;
            else if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
                _handoffAllSlots = true;
            else
            {
                command.ReplyToCommand("usage: dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [all|slot]");
                return;
            }
        }

        command.ReplyToCommand(
            $"[DTR OK] handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()}");
    }
    [ConsoleCommand("dtr_handoff_360", "dtr_handoff_360 [0|1] [range] [los|nolos]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void Handoff360Command(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            var enabled = command.GetArg(1);
            if (enabled is "0" or "off" or "false")
            {
                _handoffThreat360Enabled = false;
                _pendingThreat360.Clear();
            }
            else if (enabled is "1" or "on" or "true")
            {
                _handoffThreat360Enabled = true;
            }
            else
            {
                command.ReplyToCommand("usage: dtr_handoff_360 [0|1] [range] [los|nolos]");
                return;
            }
        }

        if (command.ArgCount >= 3)
        {
            if (!float.TryParse(command.GetArg(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var range))
            {
                command.ReplyToCommand("usage: dtr_handoff_360 [0|1] [range] [los|nolos]");
                return;
            }
            _handoffThreat360Range = Math.Clamp(range, HandoffThreat360MinRange, HandoffThreat360MaxRange);
            _pendingThreat360.Clear();
        }

        if (command.ArgCount >= 4)
        {
            var los = command.GetArg(3);
            if (los.Equals("los", StringComparison.OrdinalIgnoreCase) ||
                los.Equals("ray", StringComparison.OrdinalIgnoreCase) ||
                los.Equals("raytrace", StringComparison.OrdinalIgnoreCase) ||
                los is "1" or "on" or "true")
            {
                _handoffThreat360LosEnabled = true;
            }
            else if (los.Equals("nolos", StringComparison.OrdinalIgnoreCase) ||
                     los.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                     los is "0" or "false")
            {
                _handoffThreat360LosEnabled = false;
            }
            else
            {
                command.ReplyToCommand("usage: dtr_handoff_360 [0|1] [range] [los|nolos]");
                return;
            }
            _pendingThreat360.Clear();
        }

        BotControllerNative.SetReplayNativeFovOverride(_handoffThreat360Enabled);

        command.ReplyToCommand(
            $"dtr: handoff_360={_handoffThreat360Enabled} range={_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled} raytrace={_rayTraceLosProbe.ProbeStatus}");
    }

    private void SetIdentityMode(CommandInfo command)
    {
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_set identity <off|name|steam|avatar|full>");
            return;
        }

        switch (command.GetArg(2).ToLowerInvariant())
        {
            case "off":
            case "0":
            case "false":
                _replayIdentityMode = ReplayIdentityMode.Off;
                break;
            case "name":
                _replayIdentityMode = ReplayIdentityMode.Name;
                break;
            case "steam":
            case "sid":
            case "steamid":
            case "1":
            case "on":
            case "true":
                _replayIdentityMode = ReplayIdentityMode.Steam;
                break;
            case "avatar":
            case "avatars":
            case "event_avatar":
            case "event-avatar":
            case "full":
                _replayIdentityMode = ReplayIdentityMode.Avatar;
                break;
            default:
                command.ReplyToCommand("usage: dtr_set identity <off|name|steam|avatar|full>");
                return;
        }

        ApplyRuntimeConfigSideEffects();
        command.ReplyToCommand($"[DTR OK] identity={ReplayIdentityModeName()}");
    }

    private void SetAlignMode(CommandInfo command)
    {
        if (command.ArgCount < 4)
        {
            command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles|cosmetics|stickers|charms|crosshair|left_hand|scoreboard> <off|on>");
            return;
        }

        var enabled = ParseOnOff(command.GetArg(3), false);
        var target = command.GetArg(2);
        switch (target.ToLowerInvariant())
        {
            case "weapons":
            case "weapon":
            case "loadout":
            case "active_weapon":
            case "active-weapon":
            case "slot_lock":
            case "slot-lock":
                command.ReplyToCommand($"[DTR WARN] legacy command: use dtr_align {target} <on|off>");
                SetAlignComponent(target, enabled, command.ReplyToCommand);
                ReplyAlignStatus(command.ReplyToCommand);
                return;
            case "projectiles":
            case "projectile":
                command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align projectiles <on|off>");
                SetAlignComponent(target, enabled, command.ReplyToCommand);
                ReplyAlignStatus(command.ReplyToCommand);
                return;
            case "cosmetics":
            case "cosmetic":
            case "skins":
            case "skin":
                command.ReplyToCommand("[DTR WARN] legacy command: cosmetics moved out of align. Use dtr_cosmetics basic|full");
                SetCosmeticAlignEnabled(enabled);
                ReplyCosmeticsStatus(command.ReplyToCommand);
                if (_cosmeticAlignEnabled)
                    command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "stickers":
            case "sticker":
            case "charms":
            case "charm":
            case "keychains":
            case "keychain":
                command.ReplyToCommand($"[DTR WARN] legacy command: use dtr_cosmetics {target} <on|off>");
                SetCosmeticComponent(target, enabled, command.ReplyToCommand);
                ReplyCosmeticsStatus(command.ReplyToCommand);
                if (_cosmeticAlignEnabled)
                    command.ReplyToCommand(CosmeticRiskNotice);
                return;
            case "crosshair":
            case "crosshairs":
            case "view":
                command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align crosshair <on|off>");
                SetAlignComponent(target, enabled, command.ReplyToCommand);
                ReplyAlignStatus(command.ReplyToCommand);
                return;
            case "left_hand":
            case "left-hand":
            case "lefthand":
            case "left_hand_desired":
            case "left-hand-desired":
            case "lefthanddesired":
                command.ReplyToCommand("[DTR WARN] legacy command: use dtr_align left_hand <on|off>");
                SetAlignComponent(target, enabled, command.ReplyToCommand);
                ReplyAlignStatus(command.ReplyToCommand);
                return;
            case "scoreboard":
            case "scoreboards":
            case "scores":
            case "stats":
                command.ReplyToCommand("[DTR WARN] legacy command: scoreboard moved out of align. Use dtr_match scoreboard <on|off>");
                ApplyMatchPreset(scoreboard: enabled);
                ReplyMatchStatus(command.ReplyToCommand);
                return;
            default:
                command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles|cosmetics|stickers|charms|crosshair|left_hand|scoreboard> <off|on>");
                return;
        }
    }

    private enum CosmeticPreset
    {
        Off,
        Weapons,
        Basic,
        Full,
    }

    private void ReplyAlignStatus(Action<string> reply)
    {
        reply($"[DTR ALIGN] preset={AlignPresetName()}");
        reply($"[DTR ALIGN] weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand={FormatOnOff(_leftHandDesiredEnabled)}");
        reply("[DTR ALIGN] note: cosmetics moved to dtr_cosmetics; scoreboard moved to dtr_match");
    }

    private static void ReplyAlignUsage(Action<string> reply)
    {
        reply("usage: dtr_align [status|default|full|handoff_safe|off]");
        reply("usage: dtr_align <weapons|projectiles|crosshair|left_hand> <on|off>");
    }

    private void ReplyUnknownAlignTarget(string target, Action<string> reply)
    {
        reply($"[DTR ERR] unknown dtr_align target: {target}");
        ReplyAlignUsage(reply);
        if (target.Equals("scoreboard", StringComparison.OrdinalIgnoreCase))
            reply("hint: scoreboard is match presentation: dtr_match scoreboard on");
        if (target.Equals("cosmetics", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("skins", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("stickers", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("charms", StringComparison.OrdinalIgnoreCase))
        {
            reply("hint: cosmetics are high-risk: dtr_cosmetics basic|full");
        }
    }

    private string AlignPresetName()
    {
        if (_weaponAlignEnabled && _projectileAlignEnabled && _crosshairAlignEnabled && _leftHandDesiredEnabled)
            return "default";
        if (_weaponAlignEnabled && _projectileAlignEnabled && _crosshairAlignEnabled && !_leftHandDesiredEnabled)
            return "handoff_safe";
        if (!_weaponAlignEnabled && !_projectileAlignEnabled && !_crosshairAlignEnabled && !_leftHandDesiredEnabled)
            return "off";
        return "custom";
    }

    private void ApplyReplayFidelityPreset(
        bool weapons,
        bool projectiles,
        bool leftHandDesired,
        bool crosshair,
        Action<string> reply)
    {
        SetWeaponAlignEnabled(weapons);
        SetProjectileAlignEnabled(projectiles);
        ApplyLeftHandDesiredMode(leftHandDesired, reply);
        SetCrosshairAlignEnabled(crosshair);
        ReplyAlignStatus(reply);
    }

    private bool SetAlignComponent(string component, bool enabled, Action<string> reply)
    {
        switch (component.ToLowerInvariant())
        {
            case "weapons":
            case "weapon":
            case "loadout":
            case "active_weapon":
            case "active-weapon":
            case "slot_lock":
            case "slot-lock":
                SetWeaponAlignEnabled(enabled);
                reply($"[DTR OK] dtr_align weapons={FormatOnOff(_weaponAlignEnabled)}");
                if (component.Equals("loadout", StringComparison.OrdinalIgnoreCase) ||
                    component.Equals("active_weapon", StringComparison.OrdinalIgnoreCase) ||
                    component.Equals("slot_lock", StringComparison.OrdinalIgnoreCase) ||
                    component.Equals("active-weapon", StringComparison.OrdinalIgnoreCase) ||
                    component.Equals("slot-lock", StringComparison.OrdinalIgnoreCase))
                {
                    reply("[DTR WARN] loadout/active_weapon/slot_lock currently share the weapons align implementation.");
                }
                return true;
            case "projectiles":
            case "projectile":
            case "nades":
            case "grenades":
                SetProjectileAlignEnabled(enabled);
                reply($"[DTR OK] dtr_align projectiles={FormatOnOff(_projectileAlignEnabled)}");
                return true;
            case "left_hand":
            case "left-hand":
            case "lefthand":
            case "left_hand_desired":
            case "left-hand-desired":
            case "lefthanddesired":
                ApplyLeftHandDesiredMode(enabled, reply);
                return true;
            case "crosshair":
            case "crosshairs":
            case "view":
                SetCrosshairAlignEnabled(enabled);
                reply($"[DTR OK] dtr_align crosshair={FormatOnOff(_crosshairAlignEnabled)}");
                return true;
            default:
                return false;
        }
    }

    private void ReplyCosmeticsStatus(Action<string> reply)
    {
        reply($"[DTR COSMETICS] preset={CosmeticPresetName()} risk={FormatOnOff(_cosmeticAlignEnabled)}");
        reply($"[DTR COSMETICS] weapons={FormatOnOff(_cosmeticWeaponsEnabled)} knives={FormatOnOff(_cosmeticKnivesEnabled)} gloves={FormatOnOff(_cosmeticGlovesEnabled)} names={FormatOnOff(_cosmeticNamesEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)}");
        reply($"[DTR COSMETICS] {FormatCosmeticStatusCounts()}");
    }

    private void ApplyMatchPreset(bool scoreboard)
    {
        SetScoreboardAlignEnabled(scoreboard);
    }

    private void ReplyMatchStatus(Action<string> reply)
    {
        reply($"[DTR MATCH] preset={(_scoreboardAlignEnabled ? "scoreboard" : "off")}");
        reply($"[DTR MATCH] scoreboard={FormatOnOff(_scoreboardAlignEnabled)} {FormatScoreboardStatusCounts()}");
    }

    private void ApplyCosmeticPreset(CosmeticPreset preset)
    {
        switch (preset)
        {
            case CosmeticPreset.Off:
                _cosmeticWeaponsEnabled = false;
                _cosmeticKnivesEnabled = false;
                _cosmeticGlovesEnabled = false;
                _cosmeticNamesEnabled = false;
                _cosmeticAgentsEnabled = false;
                _stickerAlignEnabled = false;
                _charmAlignEnabled = false;
                break;
            case CosmeticPreset.Weapons:
                _cosmeticWeaponsEnabled = true;
                _cosmeticKnivesEnabled = false;
                _cosmeticGlovesEnabled = false;
                _cosmeticNamesEnabled = true;
                _cosmeticAgentsEnabled = false;
                _stickerAlignEnabled = false;
                _charmAlignEnabled = false;
                break;
            case CosmeticPreset.Basic:
                _cosmeticWeaponsEnabled = true;
                _cosmeticKnivesEnabled = true;
                _cosmeticGlovesEnabled = true;
                _cosmeticNamesEnabled = true;
                _cosmeticAgentsEnabled = true;
                _stickerAlignEnabled = false;
                _charmAlignEnabled = false;
                break;
            case CosmeticPreset.Full:
                _cosmeticWeaponsEnabled = true;
                _cosmeticKnivesEnabled = true;
                _cosmeticGlovesEnabled = true;
                _cosmeticNamesEnabled = true;
                _cosmeticAgentsEnabled = true;
                _stickerAlignEnabled = true;
                _charmAlignEnabled = true;
                break;
        }

        RefreshCosmeticAlignEnabled();
        if (!_cosmeticAlignEnabled)
        {
            ResetCosmeticAlignState();
            ResetStickerAlignState();
            ResetCharmAlignState();
        }
    }

    private bool SetCosmeticComponent(string component, bool enabled, Action<string> reply)
    {
        switch (component.ToLowerInvariant())
        {
            case "weapons":
            case "weapon":
            case "skins":
            case "skin":
                _cosmeticWeaponsEnabled = enabled;
                break;
            case "knives":
            case "knife":
                _cosmeticKnivesEnabled = enabled;
                break;
            case "gloves":
            case "glove":
                _cosmeticGlovesEnabled = enabled;
                break;
            case "names":
            case "name":
            case "custom_name":
            case "custom-name":
                _cosmeticNamesEnabled = enabled;
                break;
            case "agents":
            case "agent":
            case "models":
            case "model":
                _cosmeticAgentsEnabled = enabled;
                break;
            case "stickers":
            case "sticker":
                SetStickerAlignEnabled(enabled);
                return true;
            case "charms":
            case "charm":
            case "keychains":
            case "keychain":
                SetCharmAlignEnabled(enabled);
                return true;
            case "preserve_native":
            case "preserve-native":
            case "preserve_bot":
            case "preserve-bot":
            case "native":
                _preserveNativeBotCosmetics = enabled;
                break;
            default:
                reply($"[DTR ERR] unknown dtr_cosmetics component: {component}");
                return false;
        }

        RefreshCosmeticAlignEnabled();
        if (!_cosmeticAlignEnabled)
            ResetCosmeticAlignState();
        return true;
    }

    private string CosmeticPresetName()
    {
        if (!AnyCosmeticFeatureEnabled())
            return "off";
        if (_cosmeticWeaponsEnabled && !_cosmeticKnivesEnabled && !_cosmeticGlovesEnabled &&
            _cosmeticNamesEnabled && !_cosmeticAgentsEnabled && !_stickerAlignEnabled && !_charmAlignEnabled)
        {
            return "weapons";
        }
        if (_cosmeticWeaponsEnabled && _cosmeticKnivesEnabled && _cosmeticGlovesEnabled &&
            _cosmeticNamesEnabled && _cosmeticAgentsEnabled && !_stickerAlignEnabled && !_charmAlignEnabled)
        {
            return "basic";
        }
        if (_cosmeticWeaponsEnabled && _cosmeticKnivesEnabled && _cosmeticGlovesEnabled &&
            _cosmeticNamesEnabled && _cosmeticAgentsEnabled && _stickerAlignEnabled && _charmAlignEnabled)
        {
            return "full";
        }
        return "custom";
    }

    private bool AnyBaseCosmeticsEnabled()
        => _cosmeticWeaponsEnabled || _cosmeticKnivesEnabled || _cosmeticGlovesEnabled || _cosmeticNamesEnabled || _cosmeticAgentsEnabled;

    private bool AnyCosmeticFeatureEnabled()
        => AnyBaseCosmeticsEnabled() || _stickerAlignEnabled || _charmAlignEnabled;

    private bool WeaponCosmeticFeatureEnabled()
        => _cosmeticWeaponsEnabled || _cosmeticNamesEnabled || _stickerAlignEnabled || _charmAlignEnabled;

    private bool GivenItemCosmeticFeatureEnabled()
        => WeaponCosmeticFeatureEnabled() || _cosmeticKnivesEnabled;

    private void RefreshCosmeticAlignEnabled()
    {
        _cosmeticAlignEnabled = AnyCosmeticFeatureEnabled();
        if (!_cosmeticAlignEnabled)
            RestoreAllReplayMusicKits("cosmetics_disabled");
    }

    private void ApplyLeftHandDesiredMode(bool enabled, Action<string> reply)
    {
        _leftHandDesiredEnabled = enabled;
        BotControllerNative.WriteLeftHandDesired = enabled;
        if (!_leftHandDesiredEnabled)
            ClearReplayLeftHandDesiredLatches(forceNative: true);
        reply($"[DTR OK] align left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)}");
        if (!_leftHandDesiredEnabled)
            reply(LeftHandDesiredFidelityNotice);
    }

    private void SetWeaponAlignEnabled(bool enabled)
    {
        _weaponAlignEnabled = enabled;
        if (_weaponAlignEnabled)
            return;

        _pendingWeaponAlign.Clear();
        _pendingWeaponSlotReplacements.Clear();
        _rebuiltInventorySlots.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _activeWeaponCosmetics.Clear();
        foreach (var slot in _loadedSlots)
            BotControllerNative.UnlockWeaponSlot(slot);
    }

    private void SetProjectileAlignEnabled(bool enabled)
    {
        _projectileAlignEnabled = enabled;
        if (_projectileAlignEnabled)
            return;

        _projectileAlignNextBySlot.Clear();
        _pendingProjectileAlign.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
    }

    private void SetProjectileAlignTicks(int totalWrites)
    {
        _projectileAlignTotalWrites = totalWrites;
        foreach (var pending in _pendingProjectileAlign.Values)
        {
            if (!pending.Matched)
                continue;

            pending.TotalWritesTarget = totalWrites;
            pending.WritesRemaining = RemainingProjectileAlignWrites(totalWrites, pending.WritesApplied);
        }
    }

    private static int RemainingProjectileAlignWrites(int totalWrites, int writesApplied)
        => totalWrites == ProjectileAlignUntilDelete
            ? ProjectileAlignUntilDelete
            : Math.Max(0, totalWrites - writesApplied);

    private void SetCosmeticAlignEnabled(bool enabled)
    {
        if (enabled)
        {
            ApplyCosmeticPreset(CosmeticPreset.Basic);
            return;
        }

        ApplyCosmeticPreset(CosmeticPreset.Off);
        ResetCosmeticAlignState();
        ResetStickerAlignState();
        ResetCharmAlignState();
    }

    private void SetStickerAlignEnabled(bool enabled)
    {
        _stickerAlignEnabled = enabled;
        RefreshCosmeticAlignEnabled();
        if (!_stickerAlignEnabled)
            ResetStickerAlignState();
    }

    private void SetCharmAlignEnabled(bool enabled)
    {
        _charmAlignEnabled = enabled;
        RefreshCosmeticAlignEnabled();
        if (!_charmAlignEnabled)
            ResetCharmAlignState();
    }

    private void SetCrosshairAlignEnabled(bool enabled)
    {
        if (!enabled)
        {
            _crosshairAlignEnabled = false;
            ResetCrosshairAlignState();
            return;
        }

        _crosshairAlignEnabled = true;
        if (_loadedSlots.Count > 0)
            _ = RefreshReplayCrosshairPresentation();
    }

    private void SetScoreboardAlignEnabled(bool enabled)
    {
        _scoreboardAlignEnabled = enabled;
        if (!_scoreboardAlignEnabled)
            ResetScoreboardAlignState();
    }

    [ConsoleCommand("dtr_partial", "dtr_partial <0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PartialCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _partialReplayEnabled = ParseOnOff(command.GetArg(1), _partialReplayEnabled);

        command.ReplyToCommand($"dtr: partial_replay={_partialReplayEnabled}");
    }

    [ConsoleCommand("dtr_replay_identity", "dtr_replay_identity <off|name|steam|avatar|full|0|1>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ReplayIdentityCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            if (!TryParseReplayIdentityMode(command.GetArg(1), out var mode))
            {
                command.ReplyToCommand("usage: dtr_replay_identity <off|name|steam|avatar|full|0|1>");
                return;
            }
            _replayIdentityMode = mode;
            ApplyRuntimeConfigSideEffects();
        }

        command.ReplyToCommand($"dtr: replay_identity={ReplayIdentityModeName()}");
    }

    [ConsoleCommand("dtr_set", "dtr_set <identity|align|handoff|allow_partial> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void SetCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_set identity <off|name|steam|avatar|full>");
            command.ReplyToCommand("usage: dtr_set align <weapons|loadout|active_weapon|slot_lock|projectiles|cosmetics|stickers|charms|crosshair|left_hand|scoreboard> <off|on>");
            command.ReplyToCommand("usage: dtr_set handoff <off|death|contact|death_or_contact|death_contact_c4> [slot|all]");
            command.ReplyToCommand("usage: dtr_set allow_partial <off|on>");
            return;
        }

        switch (command.GetArg(1).ToLowerInvariant())
        {
            case "identity":
                SetIdentityMode(command);
                return;
            case "align":
                SetAlignMode(command);
                return;
            case "handoff":
                SetHandoffMode(command, argOffset: 2);
                return;
            case "allow_partial":
            case "partial":
                if (command.ArgCount < 3)
                {
                    command.ReplyToCommand("usage: dtr_set allow_partial <off|on>");
                    return;
                }
                _partialReplayEnabled = ParseOnOff(command.GetArg(2), _partialReplayEnabled);
                command.ReplyToCommand($"[DTR OK] allow_partial={FormatOnOff(_partialReplayEnabled)}");
                return;
            default:
                command.ReplyToCommand("[DTR ERR] unknown setting namespace. Use identity, align, handoff, or allow_partial.");
                return;
        }
    }

    [ConsoleCommand("dtr_bots", "dtr_bots")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void BotsCommand(CCSPlayerController? player, CommandInfo command)
    {
        var players = FindTeamPlayers();
        var strictBots = players.Count(candidate => candidate.IsBot);
        var managedBots = players.Count(candidate => _botHiderBridge.IsManagedBot(candidate.Slot));
        var candidates = players.Count(IsReplayTargetBot);
        command.ReplyToCommand(
            $"dtr: strict IsBot={strictBots}, BotHider managed={managedBots}, safe replay candidates={candidates}");
        foreach (var bot in players)
        {
            var managed = _botHiderBridge.IsManagedBot(bot.Slot);
            var controllingBot = TryGetControllingBotState(bot, out var isControllingBot)
                ? (isControllingBot ? "1" : "0")
                : "unknown";
            var userId = bot.UserId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            var kickHint = bot.UserId.HasValue
                ? $" kick_hint='dtr_kick slot {bot.Slot}'"
                : "";
            if (_loadedReplays.TryGetValue(bot.Slot, out var replay) &&
                !string.IsNullOrWhiteSpace(replay.PlayerName))
            {
                kickHint += $" kick_name='dtr_kick \"{EscapeConsoleString(replay.PlayerName)}\"'";
            }
            command.ReplyToCommand(
                $"slot={bot.Slot} userid={userId} team={bot.Team} isBot={bot.IsBot} managed={managed} controllingBot={controllingBot} candidate={IsReplayTargetBot(bot)} name=\"{EscapeConsoleString(bot.PlayerName)}\"{kickHint}");
        }
    }

    [ConsoleCommand("dtr_status", "dtr_status [slot <slot>|<slot>]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            TryReadFreezeTimeConVar(out var freezeTime, out var freezeReason);
            var plan = _sequenceActive
                ? _sequenceIndex < _sequenceRounds.Length
                    ? $"sequence from_source_round={_sequenceRounds[_sequenceIndex]} prepared={_sequencePrepared}:{_sequencePreparedRound}"
                    : "sequence complete"
                : HasPlayoffSchedulingState()
                    ? $"playoff {FormatPlayoffPlanStatus()}"
                : _armed
                    ? $"single source_round={_armedSourceRound} prepared={_armedPrepared}"
                    : "none";
            command.ReplyToCommand(
                $"[DTR OK] status plan={plan} loaded_slots={_loadedSlots.Count} settings identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} allow_partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)}:{FormatPlayoffPlanStatus()} {FormatVoiceAutoStatusInline()} {FormatChatAutoStatusInline()} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatViewmodelStatusCounts()} {FormatScoreboardStatusCounts()}");
            return;
        }

        var slotArg = command.GetArg(1).Equals("slot", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot))
            return;
        var state = BotControllerNative.GetReplayState(slot);
        var sequence = _sequenceActive && _sequenceIndex < _sequenceRounds.Length
            ? $" sequence_next={_sequenceRounds[_sequenceIndex]}"
            : string.Empty;
        var playoff = _playoffEnabled
            ? $" playoff={FormatPlayoffPlanStatus()}"
            : string.Empty;
        command.ReplyToCommand(
            $"dtr: abi={BotControllerNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} handoff_360={_handoffThreat360Enabled}:{_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled}:{_rayTraceLosProbe.ProbeStatus} partial={_partialReplayEnabled} identity={ReplayIdentityModeName()} projectile_align={_projectileAlignEnabled} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} cosmetic_align={_cosmeticAlignEnabled} agent_align={_cosmeticAgentsEnabled} sticker_align={_stickerAlignEnabled} charm_align={_charmAlignEnabled} preserve_native={_preserveNativeBotCosmetics} crosshair_align={_crosshairAlignEnabled} left_hand_desired={_leftHandDesiredEnabled} scoreboard_align={_scoreboardAlignEnabled} {FormatVoiceAutoStatusInline()} {FormatChatAutoStatusInline()}{sequence}{playoff}");
    }

    [ConsoleCommand("dtr_runtime", "dtr_runtime")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RuntimeCommand(CCSPlayerController? player, CommandInfo command)
    {
        var birth = BotControllerNative.ProjectileBirthAlignStatus;
        command.ReplyToCommand(
            $"[DTR OK] DemoTracer {BotControllerNative.RuntimeSummary}");
        command.ReplyToCommand(
            $"[DTR OK] projectile_birth_align configured={birth.Configured} pending={birth.Pending} queued={birth.Queued} applied={birth.Applied} expired={birth.Expired} failed={birth.Failed} initial_position=0x{birth.InitialPositionOffset:X} initial_velocity=0x{birth.InitialVelocityOffset:X}");
    }

    [ConsoleCommand("dtr_doctor", "dtr_doctor [manifest.json]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void DoctorCommand(CCSPlayerController? player, CommandInfo command)
    {
        TryReadFreezeTimeConVar(out var freezeTime, out var freezeReason);
        var players = FindTeamPlayers();
        var tPlayers = players.Count(candidate => candidate.Team == CsTeam.Terrorist);
        var ctPlayers = players.Count(candidate => candidate.Team == CsTeam.CounterTerrorist);
        var strictBots = players.Count(candidate => candidate.IsBot);
        var managedBots = players.Count(candidate => _botHiderBridge.IsManagedBot(candidate.Slot));
        var replayTargets = FindReplayTargets();
        var loadedPlaying = _loadedSlots.Count(slot => BotControllerNative.GetReplayState(slot).Playing);

        command.ReplyToCommand(
            $"[DTR DOCTOR] runtime {BotControllerNative.RuntimeSummary}");
        command.ReplyToCommand(
            $"[DTR DOCTOR] server map={CurrentMapName()} time={Server.CurrentTime.ToString("F2", CultureInfo.InvariantCulture)} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)}");
        command.ReplyToCommand(
            $"[DTR DOCTOR] bots players T={tPlayers}/CT={ctPlayers} strict_bots={strictBots} bot_hider_managed={managedBots} safe_replay_targets={replayTargets.Count}");
        var botHiderProvider = _botHiderBridge.GetProviderInfo();
        var botHiderDiagnostics = _botHiderBridge.GetDiagnostics();
        command.ReplyToCommand(
            botHiderProvider == null || botHiderDiagnostics == null
                ? "[DTR DOCTOR] bot_hider provider=unavailable"
                : $"[DTR DOCTOR] bot_hider api={botHiderProvider.ApiVersion} connected={botHiderProvider.Connected} draining={botHiderProvider.Draining} map_epoch={botHiderProvider.MapEpoch} leases={botHiderDiagnostics.ActiveLeases}/{botHiderDiagnostics.LeasedSlots} writes={botHiderDiagnostics.PublishedWrites} controller_repairs={botHiderDiagnostics.ControllerRepairs}");
        command.ReplyToCommand(
            $"[DTR DOCTOR] replay loaded={_loadedSlots.Count} playing={loadedPlaying} identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)}:{FormatPlayoffPlanStatus()} raytrace={_rayTraceLosProbe.ProbeStatus} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatViewmodelStatusCounts()} {FormatScoreboardStatusCounts()}");

        if (command.ArgCount >= 2)
            ReplyDoctorManifest(command, command.GetArg(1));
    }

    private static void ReplyDoctorManifest(CommandInfo command, string manifestPath)
    {
        if (TryReadManifest(manifestPath, out var manifest, out var readError))
        {
            var rounds = manifest.Files
                .Select(file => file.Round)
                .Distinct()
                .Order()
                .ToArray();
            command.ReplyToCommand(
                $"[DTR DOCTOR] manifest type=round path=\"{manifestPath}\" map={manifest.Map} abi={manifest.Abi} dtr_format={manifest.EffectiveDtrFormatVersion} files={manifest.Files.Count} avatar_overrides={manifest.AvatarOverrides.Count} rounds={FormatRoundList(rounds)}");
            return;
        }

        command.ReplyToCommand(
            $"[DTR DOCTOR] manifest path=\"{manifestPath}\" read_failed=\"{readError}\"");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetDtrRoundBannerForRound();
        BeginBotHiderPresentationTransition();
        try
        {
            if (StopReplayStateForRoundBoundary("round_start"))
                Server.PrintToConsole("[DTR WARN] round_start stopped stale DTR replay state");

            if ((_sequenceActive || _armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
            {
                Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
                StopAllState("warmup_block");
                return HookResult.Continue;
            }

            if (_sequenceActive)
            {
                if (PrepareNextSequenceRound("round_start"))
                    ScheduleFreezePrerollStart($"sequence round {_sequencePreparedRound}");
            }
            else if (IsPlayoffPlanReady())
            {
                if (PrepareNextPlayoffRound("round_start"))
                    ScheduleFreezePrerollStart($"playoff extra round {_playoffRoundIndex + 1}");
            }
            else if (_armed)
            {
                if (PrepareArmedRound("round_start"))
                    ScheduleFreezePrerollStart(_armedLabel);
            }
            Server.NextFrame(ScheduleLoadedReplayMusicKitRepairs);

            return HookResult.Continue;
        }
        finally
        {
            EndBotHiderPresentationTransition();
        }
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        InvalidateFreezePreroll();

        if ((_sequenceActive || _armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            StopAllState("warmup_block");
            return HookResult.Continue;
        }

        var resumeLoop = !_sequenceActive &&
                         !HasPlayoffSchedulingState() &&
                         _armed &&
                         _armedLoop;
        ResumeFreezePrerollReplays(resumeLoop);

        var missingFreezePrerollSlots = MissingFreezePrerollResumeSlots();
        if (missingFreezePrerollSlots.Length > 0)
        {
            Server.PrintToConsole(
                "[DTR WARN] freeze pre-roll ownership was incomplete; " +
                $"retrying affected slots from their live replay index slots={string.Join(",", missingFreezePrerollSlots)}");
        }

        if (_sequenceActive)
        {
            Server.NextFrame(StartPreparedSequenceRound);
            return HookResult.Continue;
        }

        if (HasPlayoffSchedulingState())
        {
            Server.NextFrame(StartPreparedPlayoffRound);
            return HookResult.Continue;
        }

        if (!_armed)
            return HookResult.Continue;
        if (!_armedPrepared)
        {
            Server.PrintToConsole($"[DTR WARN] armed round is waiting for the next full round_start: {_armedLabel}");
            return HookResult.Continue;
        }

        var loop = _armedLoop;
        var label = _armedLabel;
        _armed = false;
        _armedPrepared = false;
        Server.NextFrame(() =>
        {
            var message = StartLoaded(loop, ReplayStartAnchor.Live, null);
            Server.PrintToConsole($"dtr: auto-start {label}: {message}");
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            var spawnedSlot = player.Slot;
            var spawnedUserId = player.UserId;
            if (_retainedReplayViewmodelSlots.Contains(player.Slot) &&
                !_lastPlayingSlots.Contains(player.Slot))
            {
                RestoreReplayBotViewmodel(player.Slot);
            }
            ScheduleCachedCosmeticRepairForSlot(player.Slot);
            if (_loadedSlots.Count > 0)
            {
                if (_loadedReplays.ContainsKey(player.Slot))
                {
                    // Buy plans are slot-scoped, but the engine creates a new
                    // pawn each round. Reassert the skip edge at spawn and redo
                    // loadout preparation once the new pawn is fully usable.
                    _ = BotControllerNative.SetBuySkip(spawnedSlot);
                    var spawnToken = _initialSpawnAssignmentToken;
                    Server.NextFrame(() =>
                    {
                        if (spawnToken != _initialSpawnAssignmentToken ||
                            spawnedUserId is not int expectedUserId ||
                            Utilities.GetPlayerFromSlot(spawnedSlot) is not { IsValid: true } currentPlayer ||
                            currentPlayer.UserId != expectedUserId ||
                            !_loadedReplays.TryGetValue(spawnedSlot, out var replay))
                        {
                            return;
                        }

                        ApplyReplayLoadoutForSlot(spawnedSlot, replay);
                        PreloadReplayWeaponsForSlot(spawnedSlot, replay);
                    });
                }
                ScheduleReplayMusicKitRepairForSlot(spawnedSlot);
                ScheduleInitialRoundSpawnAssignment();
                Server.NextFrame(() => SyncBotHiderPresentationLease(announce: false));
                AddTimer(
                    0.05f,
                    () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true),
                    TimerFlags.STOP_ON_MAPCHANGE);
                AddTimer(
                    0.20f,
                    () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true),
                    TimerFlags.STOP_ON_MAPCHANGE);
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } victim)
        {
            if (IsReplaySlotPlaying(victim.Slot))
            {
                BotControllerNative.StopReplay(victim.Slot);
                ReleaseReplaySlot(victim.Slot, "replay_target_death");
            }
            else if (_retainedReplayViewmodelSlots.Contains(victim.Slot))
            {
                RestoreReplayBotViewmodel(victim.Slot);
            }
        }

        if (HandoffIncludesDeath(_handoffMode) && HasActiveReplaySlots())
        {
            var triggerSlot = GetDeathHandoffSlot(@event);
            if (triggerSlot >= 0)
                HandoffActiveReplays($"player_death_slot{triggerSlot}", triggerSlot);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (!HandoffIncludesC4(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        var triggerSlot = @event.Userid is { IsValid: true } planter && IsReplaySlotPlaying(planter.Slot)
            ? planter.Slot
            : -1;
        HandoffActiveReplays(
            triggerSlot >= 0 ? $"bomb_planted_slot{triggerSlot}" : "bomb_planted",
            triggerSlot,
            forceAll: true);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnGrenadeThrown(EventGrenadeThrown @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled)
            TraceGrenadeThrown(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled)
            TraceSmokeDetonate(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        if (_utilityTraceEnabled)
            TraceSmokeExpired(@event);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBulletDamage(EventBulletDamage @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Victim, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_pendingBulletDamages.TryGetValue(victimSlot, out var damage) &&
            damage.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(damage.Time))
        {
            _pendingBulletDamages.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage.Damage);
        }
        else
        {
            _pendingBulletHits[victimSlot] = new PendingBulletHit(attackerSlot, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Userid, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        var damage = Math.Max(0, @event.DmgHealth) + Math.Max(0, @event.DmgArmor);
        if (damage < BulletHandoffMinDamage)
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_pendingBulletHits.TryGetValue(victimSlot, out var hit) &&
            hit.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(hit.Time))
        {
            _pendingBulletHits.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage);
        }
        else
        {
            _pendingBulletDamages[victimSlot] = new PendingBulletDamage(attackerSlot, damage, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        TickRuntimeHealthHeartbeat();

        if (!_mapActive || _lifecycleResetInProgress)
            return;

        _botHiderBridge.BeginTickQueryScope();
        try
        {
            ProcessReplayTick();
        }
        finally
        {
            _botHiderBridge.EndTickQueryScope();
        }
    }

    private void ProcessReplayTick()
    {
        ProcessDtrRoundBanner();
        ProcessVoiceTestPlayback();
        ProcessChatPlayback();
        ProcessPendingProjectileAlign();

        if (_utilityTraceEnabled)
            TraceUtilityTick();

        if (_loadedSlots.Count == 0)
        {
            SetReplayPovMask(0);
            ClearReplayCrosshairPresentation();
            RestoreAllReplayBotViewmodels();
            return;
        }

        if (_lastPlayingSlots.Count == 0)
        {
            SetReplayPovMask(0);
            UpdateReplayCrosshairPresentation();
            RestoreNonRetainedReplayBotViewmodels();
            return;
        }

        Span<int> activeSlots = stackalloc int[MaxPlayerSlots];
        Span<ReplayState> activeStates = stackalloc ReplayState[MaxPlayerSlots];
        var trackedSlotCount = 0;
        foreach (var slot in _lastPlayingSlots)
        {
            if (slot is >= 0 and < MaxPlayerSlots)
                activeSlots[trackedSlotCount++] = slot;
        }

        var activeSlotCount = 0;
        for (var trackedIndex = 0; trackedIndex < trackedSlotCount; trackedIndex++)
        {
            var slot = activeSlots[trackedIndex];
            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing)
            {
                ReleaseReplaySlot(slot, "replay_finished", ReplayReleaseKind.Finished);
                continue;
            }

            activeSlots[activeSlotCount] = slot;
            activeStates[activeSlotCount] = state;
            activeSlotCount++;
        }

        if (activeSlotCount == 0)
        {
            SetReplayPovMask(0);
            UpdateReplayCrosshairPresentation();
            RestoreNonRetainedReplayBotViewmodels();
            return;
        }

        var playerSnapshot = BuildTickPlayerSnapshot();
        UpdateReplayPovMask(playerSnapshot);
        UpdateReplayCrosshairPresentation();
        UpdateReplayBotViewmodels(playerSnapshot);

        for (var activeIndex = 0; activeIndex < activeSlotCount; activeIndex++)
        {
            var slot = activeSlots[activeIndex];
            if (!_lastPlayingSlots.Contains(slot))
                continue;
            var state = activeStates[activeIndex];

            if (!IsReplaySlotStillSafe(slot, playerSnapshot))
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "unsafe_replay_target");
                continue;
            }
            if (playerSnapshot.TryGetSlot(slot, out var replayPlayer) &&
                replayPlayer is { IsValid: true, PawnIsAlive: false })
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "dead_replay_target");
                continue;
            }

            var hasLoadedReplay = _loadedReplays.TryGetValue(slot, out var replay);
            if (hasLoadedReplay)
                ProcessReplayHifiEvents(slot, replay, state.Cursor);

            if (HandoffIncludesContact(_handoffMode) &&
                ReplayBotHasContact(slot, playerSnapshot, out var contactReason, out _))
            {
                HandoffActiveReplays($"enemy_contact_{contactReason}_slot{slot}", slot);
                continue;
            }

            if (!_weaponAlignEnabled)
                continue;

            var weaponDefIndex = NormalizeWeaponDefIndex(state.WeaponDefIndex);
            if (weaponDefIndex < 0)
            {
                _lastReplayWeaponDef.Remove(slot);
                continue;
            }
            ApplyActiveReplayWeaponCosmeticForSlot(
                slot,
                weaponDefIndex,
                force: false,
                scheduleNextFrame: true,
                playerSnapshot: playerSnapshot);
            if (_lastReplayWeaponDef.TryGetValue(slot, out var lastDef) &&
                lastDef == weaponDefIndex)
                continue;

            ApplyReplayWeaponPreset(slot, weaponDefIndex, allowSlotReplacement: false, force: false);
        }
    }

    private void ProcessReplayHifiEvents(int slot, LoadedReplay replay, int cursor)
    {
        if (cursor < 0 || replay.HifiEvents.Length == 0)
            return;

        var next = _replayHifiEventNextBySlot.GetValueOrDefault(slot);
        while (next < replay.HifiEvents.Length && replay.HifiEvents[next].TickIndex <= (uint)cursor)
        {
            ExecuteReplayHifiEvent(slot, replay, replay.HifiEvents[next]);
            next++;
        }
        _replayHifiEventNextBySlot[slot] = next;
    }

    private void ExecuteReplayHifiEvent(int slot, LoadedReplay replay, ReplayHifiEvent replayEvent)
    {
        var kind = replayEvent.Kind.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "item_drop":
                // Live replay ticks must not mutate inventory/entities. Keep item events
                // as metadata until replay-safe transfer machinery exists.
                break;

            case "bomb_drop":
                // C4 is a unique objective entity. Mid-replay DropActiveWeapon on C4 can
                // leave CS2 in an invalid bomb state, so runtime C4 transfer stays record-only.
                break;

            case "item_pickup":
            case "item_transfer":
                if (ShouldQueueReplayUtilityGrant(replayEvent, replay.SteamId))
                    QueueReplayUtilityGrant(slot, replayEvent);
                break;

            case "bomb_pickup":
                // Safe C4 ownership is aligned before replay start. Do not clone or move C4
                // during live replay ticks.
                break;

            case "bomb_planted":
                // Actual server bomb_planted drives C4 handoff. Demo metadata stays
                // record-only so a failed or delayed live plant cannot hand off early.
                break;
        }
    }

    private static bool ReplayEventBelongsToSlot(ulong? eventSteamId, ulong replaySteamId)
        => !eventSteamId.HasValue || replaySteamId == 0 || eventSteamId.Value == replaySteamId;

    internal static bool ShouldQueueReplayUtilityGrant(
        ReplayHifiEvent replayEvent,
        ulong replaySteamId)
    {
        var kind = replayEvent.Kind.Trim().ToLowerInvariant();
        return kind is "item_pickup" or "item_transfer" &&
               ReplayEventBelongsToSlot(replayEvent.TargetSteamId, replaySteamId) &&
               IsUtilityWeaponDefIndex(ReplayEventWeaponDefIndex(replayEvent));
    }

    private static bool ShouldExecuteReplayEquipmentEvent(int weaponDefIndex, bool isBomb)
        => isBomb || IsUtilityWeaponDefIndex(weaponDefIndex);

    private void QueueReplayUtilityGrant(int slot, ReplayHifiEvent replayEvent)
    {
        var weaponDefIndex = ReplayEventWeaponDefIndex(replayEvent);
        if (!IsUtilityWeaponDefIndex(weaponDefIndex) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.UserId is not int userId)
        {
            return;
        }
        var generation = CurrentReplayMutationGeneration(slot);

        var targetCount = Math.Max(1, replayEvent.TargetCountAfter ?? 1);
        var sourceTick = replayEvent.Tick;
        Server.NextFrame(() => EnsureReplayUtilityGrant(
            slot,
            userId,
            generation,
            className,
            targetCount,
            sourceTick));
    }

    private void EnsureReplayUtilityGrant(
        int slot,
        int userId,
        long generation,
        string className,
        int targetCount,
        int sourceTick)
    {
        if (!IsReplaySlotStillSafe(slot) ||
            !IsReplaySlotPlaying(slot) ||
            !IsReplayMutationGenerationCurrent(slot, generation))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != userId)
            return;

        var currentCount = CountCurrentReplayItems(player, className);
        if (currentCount >= targetCount)
            return;

        var missing = targetCount - currentCount;
        for (var i = 0; i < missing; i++)
        {
            if (!TryGiveNamedItem(player, className))
            {
                Server.PrintToConsole(
                    $"dtr: hifi utility grant failed slot={slot} item={className} tick={sourceTick}");
                return;
            }
        }

        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
    }

    private void DropReplayItemToWorld(int slot, ReplayHifiEvent replayEvent, bool isBomb)
    {
        var weaponDefIndex = isBomb ? 49 : ReplayEventWeaponDefIndex(replayEvent);
        if (weaponDefIndex < 0 ||
            !ShouldExecuteReplayEquipmentEvent(weaponDefIndex, isBomb) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        var pawn = player.PlayerPawn.Value;
        var weapon = GetReplayWeaponsByClass(pawn, className).FirstOrDefault();
        if (weapon == null)
        {
            Server.PrintToConsole($"dtr: hifi drop skipped slot={slot} item={className} tick={replayEvent.Tick}");
            return;
        }
        if (!TrySelectWeapon(player, pawn, weapon))
            return;

        try
        {
            player.DropActiveWeapon();
            _trackedDroppedReplayItems.Add(new TrackedDroppedReplayItem(slot, weaponDefIndex, weapon.Handle));
            _lastEnsuredWeaponDef.Remove(slot);
            _lastReplayWeaponDef.Remove(slot);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: hifi drop failed slot={slot} item={className} tick={replayEvent.Tick}: {ex.Message}");
        }
    }

    private void EnsureReplayEventItem(int slot, ReplayHifiEvent replayEvent, bool isBomb)
    {
        var weaponDefIndex = isBomb ? 49 : ReplayEventWeaponDefIndex(replayEvent);
        if (weaponDefIndex < 0 ||
            !ShouldExecuteReplayEquipmentEvent(weaponDefIndex, isBomb) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var targetCount = Math.Max(1, replayEvent.TargetCountAfter ?? 1);
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var currentCount = CountCurrentReplayItems(player, className);
        if (currentCount >= targetCount)
            return;

        if (isBomb)
        {
            Server.PrintToConsole(
                $"dtr: hifi bomb pickup skipped slot={slot} tick={replayEvent.Tick}: refusing to clone C4");
            return;
        }

        var missing = targetCount - currentCount;
        for (var i = 0; i < missing; i++)
        {
            if (!TryGiveNamedItem(player, className))
            {
                Server.PrintToConsole($"dtr: hifi pickup fallback failed slot={slot} item={className} tick={replayEvent.Tick}");
                return;
            }
            KillOneTrackedDroppedReplayItem(weaponDefIndex);
        }

        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
    }

    private static int ReplayEventWeaponDefIndex(ReplayHifiEvent replayEvent)
    {
        if (replayEvent.WeaponDefIndex.HasValue)
            return NormalizeWeaponDefIndex(replayEvent.WeaponDefIndex.Value);
        if (string.IsNullOrWhiteSpace(replayEvent.ItemName))
            return -1;

        var itemName = NormalizeReplayEventItemName(replayEvent.ItemName);
        return NormalizeWeaponDefIndex(WeaponDefIndex(itemName));
    }

    private static string NormalizeReplayEventItemName(string itemName)
    {
        var normalized = itemName.Trim().ToLowerInvariant() switch
        {
            "decoy_grenade" or "weapon_decoy_grenade" => "weapon_decoy",
            "c4" or "weapon_c4_explosive" => "weapon_c4",
            var value => value
        };
        return normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"weapon_{normalized}";
    }

    private static int CountCurrentReplayItems(CCSPlayerController player, string className)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
            return 0;

        var count = 0;
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
                count++;
        }
        return count;
    }

    private static IEnumerable<CBasePlayerWeapon> GetReplayWeaponsByClass(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
                yield return weapon;
        }
    }

    private void KillOneTrackedDroppedReplayItem(int weaponDefIndex)
    {
        var index = _trackedDroppedReplayItems.FindIndex(item => item.WeaponDefIndex == weaponDefIndex);
        if (index < 0)
            return;

        var item = _trackedDroppedReplayItems[index];
        _trackedDroppedReplayItems.RemoveAt(index);
        KillTrackedDroppedReplayItem(item, "hifi_pickup_fallback");
    }

    private void KillTrackedReplayDropsForSlot(int slot, string reason)
    {
        for (var i = _trackedDroppedReplayItems.Count - 1; i >= 0; i--)
        {
            var item = _trackedDroppedReplayItems[i];
            if (item.SourceSlot != slot)
                continue;
            _trackedDroppedReplayItems.RemoveAt(i);
            KillTrackedDroppedReplayItem(item, reason);
        }
    }

    private static void KillTrackedDroppedReplayItem(TrackedDroppedReplayItem item, string reason)
    {
        try
        {
            var weapon = new CBasePlayerWeapon(item.Handle);
            if (weapon.IsValid)
                weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to kill tracked hifi drop slot={item.SourceSlot} def={item.WeaponDefIndex} reason={reason}: {ex.Message}");
        }
    }

    private void UpdateReplayPovMask(TickPlayerSnapshot playerSnapshot)
    {
        SetReplayPovMask(BuildReplayPovMask(playerSnapshot));
    }

    private ulong BuildReplayPovMask(TickPlayerSnapshot playerSnapshot)
    {
        if (_loadedSlots.Count == 0 || _lastPlayingSlots.Count == 0)
            return 0;

        Span<uint> replayPawnIndices = stackalloc uint[MaxPlayerSlots];
        Span<int> replaySlots = stackalloc int[MaxPlayerSlots];
        var replayPawnCount = 0;
        foreach (var slot in _lastPlayingSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots)
                continue;

            if (!playerSnapshot.TryGetSlot(slot, out var replayController) ||
                replayController is not { IsValid: true })
                continue;
            if (replayController.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
                continue;

            replayPawnIndices[replayPawnCount] = replayPawn.Value.Index;
            replaySlots[replayPawnCount] = slot;
            replayPawnCount++;
        }

        if (replayPawnCount == 0)
            return 0;

        ulong mask = 0;
        foreach (var controller in playerSnapshot.Controllers)
        {
            if (controller is not { IsValid: true })
                continue;
            if (controller.IsBot || _botHiderBridge.IsManagedBot(controller.Slot))
                continue;
            if (!TryGetInEyeObserverTargetIndex(controller, out var targetIndex))
                continue;
            for (var replayIndex = 0; replayIndex < replayPawnCount; replayIndex++)
            {
                if (replayPawnIndices[replayIndex] != targetIndex)
                    continue;
                mask |= 1UL << replaySlots[replayIndex];
                break;
            }
        }

        return mask;
    }

    private static bool TryGetInEyeObserverTargetIndex(CCSPlayerController controller, out uint targetIndex)
    {
        targetIndex = 0;
        try
        {
            CPlayer_ObserverServices? observerServices = null;
            if (controller.ObserverPawn is { IsValid: true, Value.IsValid: true } observerPawn)
                observerServices = observerPawn.Value.ObserverServices;
            else if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } playerPawn)
                observerServices = playerPawn.Value.ObserverServices;

            if (observerServices == null ||
                observerServices.ObserverMode != (byte)ObserverMode_t.OBS_MODE_IN_EYE)
                return false;
            if (observerServices.ObserverTarget is not { IsValid: true, Value.IsValid: true } target)
                return false;

            targetIndex = target.Value.Index;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetReplayPovMask(ulong mask)
    {
        if (mask == _lastReplayPovMask)
            return;

        _ = BotControllerNative.SetReplayPovMask(mask);
        _lastReplayPovMask = mask;
    }

    private void ClearReplayPovSlot(int slot)
    {
        if (slot is < 0 or >= MaxPlayerSlots || _lastReplayPovMask == ulong.MaxValue)
            return;

        SetReplayPovMask(_lastReplayPovMask & ~(1UL << slot));
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!_mapActive || _lifecycleResetInProgress)
            return;

        TryApplySpawnedReplayWeaponCosmetic(entity);

        if (!TryGetProjectileKind(entity, out var kind, out var weaponDefIndex))
            return;

        try
        {
            var projectile = new CBaseCSGrenadeProjectile(entity.Handle);
            if (!projectile.IsValid)
                return;
            TrackProjectileAlignCandidate(projectile, kind, weaponDefIndex);
            if (_utilityTraceEnabled)
            {
                _utilityTraceProjectiles[projectile.Index] =
                    new UtilityProjectileTrace(projectile.Index, entity.Handle, projectile.DesignerName);
                TraceProjectileEvent("projectile_spawned", projectile, null);
            }
        }
        catch (Exception ex)
        {
            if (_utilityTraceEnabled)
                TraceUtilityMessage("projectile_spawn_failed", ex.Message);
        }
    }

    private void TrackProjectileAlignCandidate(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        if (!_projectileAlignEnabled)
            return;

        var pending = new PendingProjectileAlign(projectile.Index, projectile.Handle, kind, weaponDefIndex)
        {
            MatchAttemptsRemaining = ProjectileAlignMatchAttempts,
            WritesRemaining = 0
        };
        _pendingProjectileAlign[projectile.Index] = pending;
        RememberProjectileAlignEvent(
            "projectile_align_candidate",
            $"projectile={projectile.Index} kind={kind} weapon={weaponDefIndex} ticks={FormatProjectileAlignTicks()}");

        TryResolveAndApplyProjectileAlign(projectile, pending);
    }

    private void ProcessPendingProjectileAlign()
    {
        if (_pendingProjectileAlign.Count == 0)
            return;

        _pendingProjectileAlignTickScratch.Clear();
        foreach (var entry in _pendingProjectileAlign)
            _pendingProjectileAlignTickScratch.Add(entry);
        _pendingProjectileAlignTickScratch.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        foreach (var entry in _pendingProjectileAlignTickScratch)
        {
            var pending = entry.Value;
            try
            {
                var projectile = new CBaseCSGrenadeProjectile(pending.Handle);
                if (!projectile.IsValid)
                {
                    FinishProjectileAlign(entry.Key, pending, "entity_invalid");
                    continue;
                }

                if (!pending.Matched)
                {
                    if (TryResolveAndApplyProjectileAlign(projectile, pending))
                        continue;

                    pending.MatchAttemptsRemaining--;
                    if (pending.MatchAttemptsRemaining <= 0)
                    {
                        RememberProjectileAlignEvent(
                            "projectile_align_expired",
                            $"projectile={pending.Index} kind={pending.Kind} weapon={pending.WeaponDefIndex} reason=no_match");
                        _pendingProjectileAlign.Remove(entry.Key);
                    }
                    else
                        _pendingProjectileAlign[entry.Key] = pending;
                    continue;
                }

                if (pending.WritesRemaining == 0)
                {
                    if (TryProcessMolotovPointAlign(projectile, pending))
                    {
                        if (pending.MolotovPointAlignApplied)
                            FinishProjectileAlign(entry.Key, pending, "molotov_point_align");
                        else
                            _pendingProjectileAlign[entry.Key] = pending;
                        continue;
                    }

                    FinishProjectileAlign(entry.Key, pending, "write_budget");
                    continue;
                }

                ApplyTrackedProjectileAlign(projectile, pending);
                if (pending.WritesRemaining != ProjectileAlignUntilDelete)
                    pending.WritesRemaining--;
                if (pending.WritesRemaining == 0)
                {
                    if (TryProcessMolotovPointAlign(projectile, pending))
                    {
                        if (pending.MolotovPointAlignApplied)
                            FinishProjectileAlign(entry.Key, pending, "molotov_point_align");
                        else
                            _pendingProjectileAlign[entry.Key] = pending;
                        continue;
                    }

                    FinishProjectileAlign(entry.Key, pending, "write_budget");
                }
                else
                    _pendingProjectileAlign[entry.Key] = pending;
            }
            catch (Exception ex)
            {
                _pendingProjectileAlign.Remove(entry.Key);
                RememberProjectileAlignEvent(
                    "projectile_align_failed",
                    $"projectile={entry.Key} kind={pending.Kind} error=\"{EscapeConsoleString(ex.Message)}\"");
                if (_utilityTraceEnabled)
                    TraceUtilityMessage("projectile_align_failed", $"index={entry.Key} {ex.Message}");
            }
        }
        _pendingProjectileAlignTickScratch.Clear();
    }

    private bool TryResolveAndApplyProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending)
    {
        if (!_projectileAlignEnabled ||
            !TryResolveProjectileAlign(
                projectile,
                pending.Kind,
                pending.WeaponDefIndex,
                out var slot,
                out var eventIndex,
                out var align))
            return false;

        var decision = EvaluateProjectileAlign(projectile, align, out var skipReason);
        if (decision == ProjectileAlignDecision.Retry)
        {
            if (pending.MatchAttemptsRemaining > 1)
                return false;

            skipReason = $"{skipReason}_expired";
            decision = ProjectileAlignDecision.Skip;
        }

        _projectileAlignNextBySlot[slot] = eventIndex + 1;
        if (decision == ProjectileAlignDecision.Skip)
        {
            _pendingProjectileAlign.Remove(pending.Index);
            var message =
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} reason={skipReason}";
            RememberProjectileAlignEvent("projectile_align_skipped", message);
            if (_utilityTraceEnabled)
            {
                TraceUtilityMessage(
                    "projectile_align_skipped",
                    message);
            }
            return true;
        }

        pending.Matched = true;
        pending.Slot = slot;
        pending.EventIndex = eventIndex;
        pending.Align = align;
        pending.TotalWritesTarget = _projectileAlignTotalWrites;
        ArmMolotovPointAlign(pending, align);
        ApplyTrackedProjectileAlign(projectile, pending);
        pending.WritesRemaining = RemainingProjectileAlignWrites(pending.TotalWritesTarget, pending.WritesApplied);
        var writeBudgetExhausted = pending.WritesRemaining == 0;
        if (!writeBudgetExhausted || pending.MolotovPointAlignArmed)
            _pendingProjectileAlign[pending.Index] = pending;

        if (_utilityTraceEnabled)
        {
            var message =
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} ticks={FormatProjectileAlignTicks()} native_birth_rc={pending.LastNativeBirthRc} molotov_point={FormatPendingMolotovPointAlign(pending)} init_vel=({align.InitialVelocity.X:F3},{align.InitialVelocity.Y:F3},{align.InitialVelocity.Z:F3}) effect={align.EffectSource}:{align.EffectConfidence:F2}";
            RememberProjectileAlignEvent("projectile_align", message);
            TraceUtilityMessage(
                "projectile_align",
                message);
        }
        else
        {
            RememberProjectileAlignEvent(
                "projectile_align",
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} ticks={FormatProjectileAlignTicks()} native_birth_rc={pending.LastNativeBirthRc} molotov_point={FormatPendingMolotovPointAlign(pending)} init_vel=({align.InitialVelocity.X:F3},{align.InitialVelocity.Y:F3},{align.InitialVelocity.Z:F3}) effect={align.EffectSource}:{align.EffectConfidence:F2}");
        }
        if (writeBudgetExhausted && !pending.MolotovPointAlignArmed)
            FinishProjectileAlign(pending.Index, pending, "write_budget");
        return true;
    }

    private bool TryResolveProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex,
        out int slot,
        out int eventIndex,
        out ReplayProjectileEvent align)
    {
        slot = -1;
        eventIndex = -1;
        align = default;

        if (!TryGetProjectileThrowerSlot(projectile, out slot))
            return false;
        if (!_loadedReplays.TryGetValue(slot, out var replay) || replay.Projectiles.Length == 0)
            return false;

        var state = BotControllerNative.GetReplayState(slot);
        if (!state.Playing)
            return false;

        var next = _projectileAlignNextBySlot.TryGetValue(slot, out var value) ? value : 0;
        eventIndex = FindProjectileAlignEvent(replay.Projectiles, next, state.Cursor, kind, weaponDefIndex);
        if (eventIndex < 0)
            return false;

        align = replay.Projectiles[eventIndex];
        return true;
    }

    private static void ApplyProjectileAlign(CBaseCSGrenadeProjectile projectile, ReplayProjectileEvent align)
    {
        SetVector(projectile.InitialPosition, align.InitialPosition);
        SetVector(projectile.InitialVelocity, align.InitialVelocity);
        SetVector(projectile.AbsOrigin, align.InitialPosition);
        SetVector(projectile.AbsVelocity, align.InitialVelocity);
    }

    private void ApplyTrackedProjectileAlign(CBaseCSGrenadeProjectile projectile, PendingProjectileAlign pending)
    {
        pending.LastNativeBirthRc = QueueNativeProjectileBirthAlign(projectile, pending.Align);
        if (pending.LastNativeBirthRc != 0)
            ApplyProjectileAlign(projectile, pending.Align);
        var now = Server.CurrentTime;
        if (pending.WritesApplied == 0)
            pending.FirstWriteTime = now;
        pending.LastWriteTime = now;
        pending.WritesApplied++;
    }

    private int QueueNativeProjectileBirthAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileEvent align)
    {
        var entityPtr = unchecked((ulong)projectile.Handle);
        var rc = BotControllerNative.QueueProjectileBirthAlign(
            entityPtr,
            align.InitialPosition,
            align.InitialVelocity);
        if (rc == 0)
            return 0;

        RememberProjectileAlignEvent(
            "projectile_birth_align_fallback",
            $"projectile={projectile.Index} kind={align.Kind} native_birth_rc={rc}");
        return rc;
    }

    private void ArmMolotovPointAlign(PendingProjectileAlign pending, ReplayProjectileEvent align)
    {
        pending.MolotovPointAlignArmed = false;
        pending.MolotovPointAlignApplied = false;
        pending.MolotovPointAlignMode = MolotovPointAlignMode.Off;
        pending.MolotovPointAlignTargetTickIndex = -1;

        if (_molotovPointAlignMode == MolotovPointAlignMode.Off ||
            align.Kind != ReplayProjectileKind.Molotov ||
            !HasReliableFireProjectileMetadata(align))
        {
            return;
        }

        pending.MolotovPointAlignArmed = true;
        pending.MolotovPointAlignMode = _molotovPointAlignMode;
        pending.MolotovPointAlignTargetTickIndex = Math.Max(0, align.EffectTickIndex - _molotovPointAlignLeadTicks);
    }

    private bool TryProcessMolotovPointAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending)
    {
        if (!pending.MolotovPointAlignArmed || pending.MolotovPointAlignApplied)
            return false;

        var state = BotControllerNative.GetReplayState(pending.Slot);
        if (!state.Playing)
            return true;

        var targetTick = pending.MolotovPointAlignTargetTickIndex;
        if (targetTick < 0)
            return false;

        var cursor = state.Cursor;
        if (cursor > pending.Align.EffectTickIndex + 16)
        {
            RememberProjectileAlignEvent(
                "molotov_point_align_expired",
                $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} cursor={cursor} effect_tick={pending.Align.EffectTickIndex}");
            pending.MolotovPointAlignApplied = true;
            return true;
        }

        if (cursor < targetTick)
            return true;

        ApplyMolotovPointAlign(projectile, pending, cursor);
        pending.MolotovPointAlignApplied = true;
        return true;
    }

    private void ApplyMolotovPointAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending,
        int cursor)
    {
        var point = pending.Align.EffectPosition;
        var zero = new ReplayVector3(0.0f, 0.0f, 0.0f);

        SetVector(projectile.AbsOrigin, point);
        SetVector(projectile.ExplodeEffectOrigin, point);
        SetVector(projectile.AbsVelocity, zero);
        SetVector(projectile.BaseVelocity, zero);
        SetVector(projectile.InitialVelocity, zero);
        projectile.TicksAtZeroVelocity = Math.Max(projectile.TicksAtZeroVelocity, 8);

        var rc = 0;
        try
        {
            var molotov = new CMolotovProjectile(projectile.Handle);
            if (molotov.IsValid)
            {
                SetVector(molotov.AbsOrigin, point);
                SetVector(molotov.ExplodeEffectOrigin, point);
                SetVector(molotov.AbsVelocity, zero);
                SetVector(molotov.BaseVelocity, zero);
                molotov.TicksAtZeroVelocity = Math.Max(molotov.TicksAtZeroVelocity, 8);
                if (pending.MolotovPointAlignMode == MolotovPointAlignMode.Detonate)
                {
                    molotov.DetonateTime = Server.CurrentTime;
                    molotov.DetonationRecorded = false;
                    molotov.IsLive = true;
                }
            }
            else
            {
                rc = -2;
            }
        }
        catch
        {
            rc = -8;
        }

        RememberProjectileAlignEvent(
            "molotov_point_align",
            $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} mode={FormatMolotovPointAlignMode(pending.MolotovPointAlignMode)} cursor={cursor} target_tick={pending.MolotovPointAlignTargetTickIndex} effect_tick={pending.Align.EffectTickIndex} rc={rc} point=({point.X:F3},{point.Y:F3},{point.Z:F3})");
    }

    private void FinishProjectileAlign(uint index, PendingProjectileAlign pending, string reason)
    {
        _pendingProjectileAlign.Remove(index);
        if (!pending.Matched)
            return;

        var duration = Math.Max(0.0f, pending.LastWriteTime - pending.FirstWriteTime);
        var message =
            $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} kind={pending.Kind} reason={reason} writes={pending.WritesApplied} target={FormatProjectileAlignTicks()} native_birth_rc={pending.LastNativeBirthRc} molotov_point={FormatPendingMolotovPointAlign(pending)} duration={duration.ToString("F3", CultureInfo.InvariantCulture)}";
        RememberProjectileAlignEvent("projectile_align_finished", message);
        if (!_utilityTraceEnabled)
            return;

        TraceUtilityMessage(
            "projectile_align_finished",
            message);
    }

    private void RememberProjectileAlignEvent(string kind, string message)
    {
        var line =
            $"{Server.CurrentTime.ToString("F3", CultureInfo.InvariantCulture)} {kind} {message}";
        _projectileAlignLog.Enqueue(line);
        while (_projectileAlignLog.Count > ProjectileAlignLogMaxEntries)
            _projectileAlignLog.Dequeue();
    }

    private static ProjectileAlignDecision EvaluateProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileEvent align,
        out string skipReason)
    {
        skipReason = string.Empty;
        if (align.Kind != ReplayProjectileKind.Molotov)
            return ProjectileAlignDecision.Apply;

        if (!HasReliableFireProjectileMetadata(align))
        {
            skipReason = "unreliable_fire_metadata";
            return ProjectileAlignDecision.Skip;
        }
        if (!ReplayVectorIsMeaningful(align.InitialPosition) ||
            !ReplayVectorIsMeaningful(align.InitialVelocity))
        {
            skipReason = "invalid_fire_initial_vector";
            return ProjectileAlignDecision.Skip;
        }

        if (!VectorIsMeaningful(projectile.InitialPosition))
        {
            skipReason = "fire_initial_position_pending";
            return ProjectileAlignDecision.Retry;
        }

        var initialDistance = VectorDistance(projectile.InitialPosition, align.InitialPosition);
        if (initialDistance > FireProjectileAlignMaxInitialPositionDistance)
        {
            skipReason = $"fire_initial_position_distance={initialDistance:F1}";
            return ProjectileAlignDecision.Skip;
        }

        return ProjectileAlignDecision.Apply;
    }

    private static bool HasReliableFireProjectileMetadata(ReplayProjectileEvent align)
    {
        if (align.EffectConfidence < 0.75f || align.EffectTickIndex < 0)
            return false;
        if (!ReplayVectorIsMeaningful(align.EffectPosition))
            return false;

        return align.EffectSource.Equals("inferno_start_burn_event", StringComparison.OrdinalIgnoreCase) ||
               align.EffectSource.Equals("molotov_detonation_event", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindProjectileAlignEvent(
        IReadOnlyList<ReplayProjectileEvent> events,
        int start,
        int cursor,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        const int MaxCursorDistance = 96;
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = Math.Max(start, 0); i < events.Count; i++)
        {
            var candidate = events[i];
            if (candidate.Kind != kind)
                continue;
            if (!ProjectileWeaponDefMatches(kind, weaponDefIndex, candidate.WeaponDefIndex))
                continue;

            var diff = Math.Abs((int)candidate.TickIndex - cursor);
            if (diff < bestDistance)
            {
                best = i;
                bestDistance = diff;
            }
            if ((int)candidate.TickIndex > cursor + MaxCursorDistance)
                break;
        }

        return bestDistance <= MaxCursorDistance ? best : -1;
    }

    private static bool ProjectileWeaponDefMatches(
        ReplayProjectileKind kind,
        int liveWeaponDefIndex,
        int replayWeaponDefIndex)
    {
        if (liveWeaponDefIndex <= 0 || replayWeaponDefIndex <= 0)
            return true;
        if (liveWeaponDefIndex == replayWeaponDefIndex)
            return true;

        // CS2 commonly exposes incendiary projectiles under the same molotov
        // projectile class. Treat 46/48 as the same projectile kind for align,
        // while still preparing the bot with the exact replay weapon def.
        return kind == ReplayProjectileKind.Molotov &&
               liveWeaponDefIndex is 46 or 48 &&
               replayWeaponDefIndex is 46 or 48;
    }

    private static bool TryGetProjectileThrowerSlot(CBaseCSGrenadeProjectile projectile, out int slot)
    {
        slot = -1;
        var thrower = projectile.Thrower.Value;
        if (thrower is not { IsValid: true })
            return false;

        foreach (var player in FindTeamPlayers())
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.Handle == thrower.Handle)
            {
                slot = player.Slot;
                return true;
            }
        }

        return false;
    }

    private static void SetVector(Vector? vector, ReplayVector3 value)
    {
        if (vector == null)
            return;
        vector.X = value.X;
        vector.Y = value.Y;
        vector.Z = value.Z;
    }

    private static float VectorDistance(Vector? vector, ReplayVector3 value)
    {
        if (vector == null)
            return float.PositiveInfinity;
        var dx = vector.X - value.X;
        var dy = vector.Y - value.Y;
        var dz = vector.Z - value.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool VectorIsMeaningful(Vector? value)
        => value != null &&
           float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           (MathF.Abs(value.X) > float.Epsilon ||
            MathF.Abs(value.Y) > float.Epsilon ||
            MathF.Abs(value.Z) > float.Epsilon);

    private static bool ReplayVectorIsMeaningful(ReplayVector3 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           (MathF.Abs(value.X) > float.Epsilon ||
            MathF.Abs(value.Y) > float.Epsilon ||
            MathF.Abs(value.Z) > float.Epsilon);

    private void OnEntityDeleted(CEntityInstance entity)
    {
        ForgetKnifeCosmeticBirthForEntity(entity);

        if (!_mapActive || _lifecycleResetInProgress)
            return;

        if (_pendingProjectileAlign.Remove(entity.Index, out var pending) &&
            pending.MolotovPointAlignArmed &&
            !pending.MolotovPointAlignApplied)
        {
            RememberProjectileAlignEvent(
                "molotov_point_align_deleted",
                $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} target_tick={pending.MolotovPointAlignTargetTickIndex} effect_tick={pending.Align.EffectTickIndex}");
        }

        if (!_utilityTraceEnabled)
            return;

        var index = entity.Index;
        if (!_utilityTraceProjectiles.TryGetValue(index, out var tracked))
            return;

        try
        {
            var projectile = new CBaseCSGrenadeProjectile(tracked.Handle);
            TraceProjectileEvent("projectile_deleted", projectile, tracked);
        }
        catch
        {
            TraceWrite(RowFields(
                ("kind", "projectile_deleted"),
                ("time", TimeField()),
                ("projectile_index", index),
                ("projectile_name", tracked.DesignerName)
            ));
        }
        _utilityTraceProjectiles.Remove(index);
    }

    private LoadRoundResult LoadRound(string manifestPath, int round)
        => LoadRoundSelection(manifestPath, round, round, steamIdMatch: false);

    private LoadRoundResult LoadPlayoffRound(string manifestPath, int tRound, int ctRound)
        => LoadRoundSelection(manifestPath, tRound, ctRound, steamIdMatch: true);

    private LoadRoundResult LoadRoundSelection(
        string manifestPath,
        int tRound,
        int ctRound,
        bool steamIdMatch)
    {
        var replayStateReplaced = false;
        var presentationTransitionStarted = false;
        var loadSucceeded = false;
        var selectionLabel = steamIdMatch
            ? $"playoff T=r{tRound}/CT=r{ctRound}"
            : $"round {tRound}";
        try
        {
            var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
            if (!TryGetPrefetchedManifest(resolvedManifestPath, out var manifest) &&
                !TryReadManifest(resolvedManifestPath, out manifest, out var readError))
                return LoadRoundResult.Fail($"dtr: failed to read manifest: {readError}");
            if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
            {
                return LoadRoundResult.Fail(
                    $"dtr: map mismatch, server=\"{currentMap}\" manifest=\"{manifest.Map}\" path=\"{resolvedManifestPath}\"");
            }

            var manifestDir = Path.GetDirectoryName(resolvedManifestPath) ?? ".";
            var avatarOverrides = BuildAvatarOverrideMap(manifest.AvatarOverrides);
            var allTFiles = tRound < 0
                ? []
                : SortReplayFilesForScoreboard(
                    manifest.Files.Where(file => file.Round == tRound),
                    "t");
            var allCtFiles = ctRound < 0
                ? []
                : SortReplayFilesForScoreboard(
                    manifest.Files.Where(file => file.Round == ctRound),
                    "ct");
            if (allTFiles.Count == 0 && allCtFiles.Count == 0)
                return LoadRoundResult.Fail($"dtr: manifest has no files for {selectionLabel}");

            ResolveActiveReplayRetentionPermutation(allTFiles, allCtFiles);

            var roundMetadata = !steamIdMatch && tRound == ctRound
                ? manifest.Rounds.FirstOrDefault(item => item.Round == tRound)
                : null;
            var roundScoreboard = roundMetadata?.Scoreboard;

            var targets = FindReplayTargets();
            var tBots = targets.Where(bot => bot.Team == CsTeam.Terrorist).ToList();
            var ctBots = targets.Where(bot => bot.Team == CsTeam.CounterTerrorist).ToList();

            if (!_partialReplayEnabled && (tBots.Count < allTFiles.Count || ctBots.Count < allCtFiles.Count))
            {
                return LoadRoundResult.Fail(
                    $"dtr: not enough bots, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            List<ReplayAssignment> tAssignments;
            List<ReplayAssignment> ctAssignments;
            if (steamIdMatch)
            {
                if (!TryBuildSteamMatchedReplayAssignments(allTFiles, tBots, out tAssignments, out var tMatchError))
                    return LoadRoundResult.Fail($"dtr: {selectionLabel} T SteamID match failed: {tMatchError}");
                if (!TryBuildSteamMatchedReplayAssignments(allCtFiles, ctBots, out ctAssignments, out var ctMatchError))
                    return LoadRoundResult.Fail($"dtr: {selectionLabel} CT SteamID match failed: {ctMatchError}");
            }
            else
            {
                tAssignments = BuildReplayAssignments(allTFiles, tBots);
                ctAssignments = BuildReplayAssignments(allCtFiles, ctBots);
            }
            if (tAssignments.Count == 0 && ctAssignments.Count == 0)
            {
                return LoadRoundResult.Fail(
                    $"dtr: no safe bot targets, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            var skippedT = allTFiles.Count - tAssignments.Count;
            var skippedCt = allCtFiles.Count - ctAssignments.Count;

            BeginBotHiderPresentationTransition();
            presentationTransitionStarted = true;
            StopAndUnloadLoaded(clearArmedPlan: true, releaseBuffers: false);
            replayStateReplaced = true;
            _loadedRoundScoreboard = roundScoreboard;
            var loaded = new List<string>();
            if (!LoadSide(
                    tAssignments,
                    manifestDir,
                    avatarOverrides,
                    includeScoreboardEvidence: !steamIdMatch,
                    loaded,
                    out var loadError))
                return FailLoadRoundAfterPartialLoad(selectionLabel, loadError);
            if (!LoadSide(
                    ctAssignments,
                    manifestDir,
                    avatarOverrides,
                    includeScoreboardEvidence: !steamIdMatch,
                    loaded,
                    out loadError))
                return FailLoadRoundAfterPartialLoad(selectionLabel, loadError);

            var voice = steamIdMatch
                ? string.Empty
                : ConfigureLoadedAutoVoiceClip(
                    resolvedManifestPath,
                    tRound,
                    roundMetadata,
                    manifest.TickRate);
            var chat = steamIdMatch
                ? string.Empty
                : ConfigureLoadedAutoChat(tRound, roundMetadata, manifest.TickRate);
            var partial = skippedT > 0 || skippedCt > 0
                ? $" partial replay skipped T={skippedT}/CT={skippedCt}"
                : string.Empty;
            var voiceStatus = string.IsNullOrWhiteSpace(voice)
                ? string.Empty
                : $" voice={voice}";
            var chatStatus = string.IsNullOrWhiteSpace(chat)
                ? string.Empty
                : $" chat={chat}";
            ReleaseUnusedWarmReplayBuffers();
            RetainLoadedBotHiderPresentation();
            loadSucceeded = true;
            return LoadRoundResult.Success($"dtr: loaded {loaded.Count} replays for {selectionLabel}{partial}{voiceStatus}{chatStatus}: {string.Join(", ", loaded)}");
        }
        catch (Exception ex)
        {
            if (replayStateReplaced)
                StopAndUnloadLoaded();
            return LoadRoundResult.Fail($"dtr: load round failed: {ex.Message}");
        }
        finally
        {
            FinishReplayPrefetchRound();
            if (!loadSucceeded)
                ReleaseUnusedWarmReplayBuffers();
            if (presentationTransitionStarted)
                EndBotHiderPresentationTransition();
        }
    }

    private LoadRoundResult FailLoadRoundAfterPartialLoad(string selectionLabel, string error)
    {
        StopAndUnloadLoaded();
        return LoadRoundResult.Fail($"dtr: failed while loading {selectionLabel}: {error}");
    }

    private bool LoadSide(
        IReadOnlyList<ReplayAssignment> assignments,
        string manifestDir,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides,
        bool includeScoreboardEvidence,
        List<string> loaded,
        out string error)
    {
        error = string.Empty;
        foreach (var assignment in assignments)
        {
            var file = assignment.File;
            var bot = assignment.Bot;
            var slot = bot.Slot;
            if (!IsReplaySlotStillSafe(slot))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} target is no longer a safe bot";
                return false;
            }

            if (!TryResolveChildPathUnderRoot(manifestDir, file.Path, out var recPath, out var pathError))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} {pathError}";
                return false;
            }

            ReplayFileMetadata replayMetadata;
            var prefetchStatus = TryTakePrefetchedReplay(recPath, out var prefetchedReplay);
            bool loadedReplay;
            switch (prefetchStatus)
            {
                case DtrReplayPrefetchTakeStatus.Success:
                    loadedReplay = BotControllerNative.LoadReplay(
                        slot,
                        prefetchedReplay,
                        out replayMetadata);
                    break;
                case DtrReplayPrefetchTakeStatus.Pending:
                    error = $"{file.Side}:slot{slot}:{file.PlayerName} replay prefetch is still pending";
                    return false;
                case DtrReplayPrefetchTakeStatus.Failed:
                    error = $"{file.Side}:slot{slot}:{file.PlayerName} replay prefetch failed validation";
                    return false;
                default:
                    // Explicit/manual load paths do not necessarily create a
                    // prefetch generation and retain their synchronous behavior.
                    loadedReplay = BotControllerNative.LoadReplayFromFile(
                        slot,
                        recPath,
                        out replayMetadata);
                    break;
            }
            if (!loadedReplay)
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} {recPath} ({BotControllerNative.LastLoadError})";
                return false;
            }

            _warmReplayBufferSlots.Remove(slot);
            RememberLoadedSlot(slot);
            TrackLoadedReplay(
                slot,
                recPath,
                file.PlayerName,
                file.SteamId,
                file.FirstWeaponDefIndex ?? -1,
                file.PreloadWeaponDefIndices,
                file.Loadout,
                NormalizeMusicKitId(file.MusicKitId),
                file.ScoreboardFlair,
                file.Cosmetics,
                file.View,
                includeScoreboardEvidence ? file.Scoreboard : null,
                manifestTeam: ReplayTeamFromManifestSide(file.Side),
                replayMetadata: replayMetadata,
                retentionRank: assignment.RetentionRank);
            if (!BotControllerNative.SetBuySkip(slot))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} failed to suppress native buying";
                return false;
            }
            TryApplyReplayIdentity(slot, file, manifestDir, avatarOverrides);
            loaded.Add($"{file.Side}:slot{slot}:{file.PlayerName}");
        }
        return true;
    }

    private List<ReplayAssignment> BuildReplayAssignments(
        IReadOnlyList<ManifestFile> files,
        IReadOnlyList<CCSPlayerController> bots)
    {
        var count = Math.Min(files.Count, bots.Count);
        var assignments = new List<ReplayAssignment>(count);
        var rankedFiles = files
            .Select((file, index) => new
            {
                File = file,
                Index = index,
                Rank = ResolveReplayRetentionRank(file.SteamId, index + 1),
            })
            .ToArray();
        var selectedFiles = ReplayRetentionPriorityParser
            .SelectPreferredIndices(rankedFiles.Select(item => item.Rank).ToArray(), count)
            .Select(index => rankedFiles[index])
            .ToArray();
        for (var i = 0; i < selectedFiles.Length; i++)
        {
            assignments.Add(new ReplayAssignment(
                selectedFiles[i].File,
                bots[i],
                selectedFiles[i].Rank));
        }
        return assignments;
    }

    private bool TryBuildSteamMatchedReplayAssignments(
        IReadOnlyList<ManifestFile> files,
        IReadOnlyList<CCSPlayerController> bots,
        out List<ReplayAssignment> assignments,
        out string error)
    {
        assignments = new List<ReplayAssignment>(bots.Count);
        error = string.Empty;
        var fileGroupsBySteamId = files
            .Where(file => file.SteamId != 0)
            .GroupBy(file => file.SteamId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var assignedSteamIds = new HashSet<ulong>();

        foreach (var bot in bots.OrderBy(candidate => candidate.Slot))
        {
            ulong steamId = 0;
            if (_loadedReplays.TryGetValue(bot.Slot, out var loaded))
                steamId = loaded.SteamId;
            else if (_retainedBotHiderPresentation.TryGetValue(bot.Slot, out var retained))
                steamId = retained.SteamId;

            if (steamId == 0)
            {
                error = $"slot={bot.Slot} has no retained DTR SteamID evidence";
                return false;
            }
            if (!assignedSteamIds.Add(steamId))
            {
                error = $"SteamID {steamId} is assigned to more than one replay target";
                return false;
            }
            if (!fileGroupsBySteamId.TryGetValue(steamId, out var matchingFiles))
            {
                error = $"slot={bot.Slot} SteamID={steamId} has no source-side replay";
                return false;
            }
            if (matchingFiles.Length != 1)
            {
                error = $"slot={bot.Slot} SteamID={steamId} has {matchingFiles.Length} ambiguous source-side replays";
                return false;
            }

            var file = matchingFiles[0];
            var fallbackRank = files
                .Select((candidate, index) => (candidate, rank: index + 1))
                .First(item => ReferenceEquals(item.candidate, file))
                .rank;
            assignments.Add(new ReplayAssignment(
                file,
                bot,
                ResolveReplayRetentionRank(file.SteamId, fallbackRank)));
        }
        return true;
    }

    private static CsTeam? ReplayTeamFromManifestSide(string side)
        => side.Equals("t", StringComparison.OrdinalIgnoreCase)
            ? CsTeam.Terrorist
            : side.Equals("ct", StringComparison.OrdinalIgnoreCase)
                ? CsTeam.CounterTerrorist
                : null;

    private static List<ManifestFile> SortReplayFilesForScoreboard(
        IEnumerable<ManifestFile> files,
        string side)
    {
        return files
            .Where(file => file.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => ReplayPlayerColorSortOrder(file.Scoreboard?.PlayerColor))
            .ThenBy(file => file.Scoreboard?.PlayerUserId ?? int.MaxValue)
            .ThenBy(file => file.Scoreboard?.PlayerEntityId ?? int.MaxValue)
            .ThenBy(file => file.SteamId)
            .ToList();
    }

    private static Dictionary<ulong, ManifestAvatarOverride> BuildAvatarOverrideMap(
        IReadOnlyList<ManifestAvatarOverride> avatarOverrides)
    {
        var map = new Dictionary<ulong, ManifestAvatarOverride>();
        foreach (var avatar in avatarOverrides)
        {
            if (avatar.SteamId == 0)
                continue;

            map.TryAdd(avatar.SteamId, avatar);
        }
        return map;
    }

    private static int ReplayPlayerColorSortOrder(string? playerColor)
        => NormalizeReplayPlayerColor(playerColor) switch
        {
            "yellow" => 0,
            "blue" => 1,
            "purple" => 2,
            "green" => 3,
            "orange" => 4,
            _ => 100,
        };

    private static int ReplayPlayerColorSchemaIndex(string? playerColor)
        => NormalizeReplayPlayerColor(playerColor) switch
        {
            "blue" => 0,
            "green" => 1,
            "yellow" => 2,
            "orange" => 3,
            "purple" => 4,
            _ => -1,
        };

    private static string? NormalizeReplayPlayerColor(string? playerColor)
    {
        var normalized = playerColor?.Trim().ToLowerInvariant();
        return normalized is "blue" or "green" or "yellow" or "orange" or "purple"
            ? normalized
            : null;
    }

    private void TryApplyReplayIdentity(
        int slot,
        ManifestFile file,
        string manifestDir,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides)
    {
        if (_replayIdentityMode == ReplayIdentityMode.Off)
            return;

        ManifestAvatarOverride? avatarOverride = null;
        var hasAvatarOverride =
            file.SteamId != 0 &&
            avatarOverrides.TryGetValue(file.SteamId, out avatarOverride);
        var avatarCommandPath = string.Empty;
        var avatarOverrideReady = false;
        if (hasAvatarOverride && avatarOverride != null &&
            _replayIdentityMode == ReplayIdentityMode.Avatar)
        {
            avatarOverrideReady = TryPrepareReplayAvatarOverride(
                file.SteamId,
                manifestDir,
                avatarOverride,
                out avatarCommandPath,
                out var avatarError);
            if (!avatarOverrideReady)
            {
                Server.PrintToConsole(
                    $"dtr: replay avatar skipped slot={slot} player={file.PlayerName} sid={file.SteamId} fallback=steam: {avatarError}");
            }
        }

        var writeSteamId = _replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar;
        if (writeSteamId && file.SteamId == 0)
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: missing steam_id");
            return;
        }

        if (!_botHiderBridge.IsAvailable())
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: BotHider unavailable");
            return;
        }

        if (!_botHiderBridge.IsManagedBot(slot))
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: not a BotHider managed bot");
            return;
        }

        if (writeSteamId)
        {
            if (avatarOverrideReady && avatarOverride != null)
            {
                ScheduleReplayAvatarOverride(
                    slot,
                    file,
                    file.SteamId,
                    avatarOverride,
                    avatarCommandPath);
            }
        }
        if (writeSteamId && _replayIdentityMode == ReplayIdentityMode.Avatar)
        {
            Server.PrintToConsole(
                avatarOverrideReady
                    ? $"dtr: replay identity lease pending slot={slot} player={file.PlayerName} sid={file.SteamId} avatar=override"
                    : $"dtr: replay identity lease pending slot={slot} player={file.PlayerName} sid={file.SteamId} avatar=steam");
        }
        else
        {
            Server.PrintToConsole(
                writeSteamId
                    ? $"dtr: replay identity lease pending slot={slot} player={file.PlayerName} sid={file.SteamId}"
                    : $"dtr: replay identity lease pending slot={slot} player={file.PlayerName}");
        }
    }

    private bool ReplayIdentityShouldApplyScoreboardFlair()
        => ShouldApplyReplayScoreboardFlair(
            _scoreboardAlignEnabled,
            _replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar);

    internal static bool ShouldApplyReplayScoreboardFlair(
        bool scoreboardAlignEnabled,
        bool identitySupportsFlair)
        => scoreboardAlignEnabled && identitySupportsFlair;

    private void ScheduleReplayAvatarOverride(
        int slot,
        ManifestFile file,
        ulong avatarSteamId,
        ManifestAvatarOverride avatar,
        string commandPath)
    {
        if (file.SteamId == 0)
            return;

        var generation = CurrentReplayIdentityGeneration(slot);
        var steamId = file.SteamId;
        var playerName = file.PlayerName;
        Server.NextFrame(() =>
            TryApplyReplayAvatarOverride(
                slot,
                steamId,
                avatarSteamId,
                playerName,
                avatar,
                commandPath,
                generation));
    }

    private void TryApplyReplayAvatarOverride(
        int slot,
        ulong steamId,
        ulong avatarSteamId,
        string playerName,
        ManifestAvatarOverride avatar,
        string commandPath,
        long generation)
    {
        if (steamId == 0 ||
            !IsReplayIdentityGenerationCurrent(slot, generation))
        {
            return;
        }

        if (!_loadedReplays.TryGetValue(slot, out var replay) ||
            replay.SteamId != steamId ||
            !IsReplaySlotStillSafe(slot) ||
            !_botHiderBridge.IsManagedBot(slot))
        {
            return;
        }

        Server.ExecuteCommand(
            $"bc_avatar_override_probe {avatarSteamId} \"{EscapeConsoleString(commandPath)}\"");
        Server.PrintToConsole(
            $"dtr: replay avatar queued slot={slot} player={playerName} sid={steamId} avatar_sid={avatarSteamId} path={avatar.Path} cache={commandPath}");
    }

    private bool TryPrepareReplayAvatarOverride(
        ulong steamId,
        string manifestDir,
        ManifestAvatarOverride avatar,
        out string commandPath,
        out string error)
    {
        commandPath = string.Empty;
        error = string.Empty;

        var format = avatar.Format.Trim();
        var pngFormat =
            (format.Length == 0 && avatar.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ||
            format.Equals("png", StringComparison.OrdinalIgnoreCase);
        if (!pngFormat)
        {
            error = $"unsupported format={avatar.Format}";
            return false;
        }

        if (!TryResolveChildPathUnderRoot(manifestDir, avatar.Path, out var avatarPath, out error))
            return false;
        if (!File.Exists(avatarPath))
        {
            error = $"missing {avatar.Path}";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(avatarPath);
            if (bytes.Length == 0)
            {
                error = "avatar PNG is empty";
                return false;
            }
            if (bytes.Length > AvatarOverrideMaxBytes)
            {
                error = "avatar PNG must be 16 KiB or smaller";
                return false;
            }
            if (!bytes.AsSpan().StartsWith(AvatarPngSignature))
            {
                error = "avatar file is not a PNG";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"avatar PNG validation failed: {ex.Message}";
            return false;
        }

        return TryPrepareAvatarOverrideCommandPath(
            steamId,
            avatarPath,
            avatar,
            out commandPath,
            out error);
    }

    private bool TryPrepareAvatarOverrideCommandPath(
        ulong steamId,
        string sourcePath,
        ManifestAvatarOverride avatar,
        out string commandPath,
        out string error)
    {
        commandPath = string.Empty;
        error = string.Empty;

        try
        {
            var pluginDir = Path.GetDirectoryName(ModulePath);
            if (string.IsNullOrWhiteSpace(pluginDir))
                pluginDir = Path.GetDirectoryName(GetType().Assembly.Location);
            if (string.IsNullOrWhiteSpace(pluginDir))
                pluginDir = ".";

            var cacheDir = Path.Combine(pluginDir, AvatarOverrideCacheDirectoryName);
            Directory.CreateDirectory(cacheDir);

            var normalizedManifestPath = avatar.Path.Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(normalizedManifestPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                error = "avatar cache filename is empty";
                return false;
            }

            var contentHash = AvatarContentHashKey(sourcePath, avatar.Sha256);
            var pathHash = ShortSha256Hex($"{steamId}\n{avatar.Path}\n{contentHash}");
            var safeStem = SanitizeAvatarCacheStem(Path.GetFileNameWithoutExtension(fileName));
            var cachedName = $"{steamId}_{pathHash}_{safeStem}.png";
            var cachedPath = Path.Combine(cacheDir, cachedName);
            var sourceInfo = new FileInfo(sourcePath);
            var shouldCopy =
                !File.Exists(cachedPath) ||
                new FileInfo(cachedPath).Length != sourceInfo.Length;
            if (shouldCopy)
                File.Copy(sourcePath, cachedPath, overwrite: true);

            commandPath = cachedPath.Replace('\\', '/');
            return true;
        }
        catch (Exception ex)
        {
            error = $"avatar cache failed: {ex.Message}";
            return false;
        }
    }

    private static string AvatarContentHashKey(string sourcePath, string manifestSha256)
    {
        var normalized = NormalizeSha256(manifestSha256);
        if (normalized.Length >= 16)
            return normalized[..16];

        using var stream = File.OpenRead(sourcePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (Uri.IsHexDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static string ShortSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SanitizeAvatarCacheStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "avatar";

        var builder = new StringBuilder(Math.Min(value.Length, 48));
        foreach (var c in value)
        {
            if (builder.Length >= 48)
                break;
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return builder.Length == 0 ? "avatar" : builder.ToString();
    }

    private string PlayLoaded(bool loop)
    {
        PreloadLoadedReplays();
        return StartLoaded(loop);
    }

    private void PreloadLoadedReplays()
    {
        // Establish round-start positions before any later freeze-time replay
        // scheduling can leave partial-roster bots at native spawn points.
        ScheduleInitialRoundSpawnAssignment();
        ApplyLoadedReplayMusicKits();
        ScheduleLoadedReplayMusicKitRepairs();

        if (_weaponAlignEnabled)
        {
            foreach (var slot in _loadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_loadedReplays.TryGetValue(slot, out var replay))
                {
                    ApplyReplayLoadoutForSlot(slot, replay);
                    PreloadReplayWeaponsForSlot(slot, replay);
                }
            }
        }

        if (_cosmeticAlignEnabled && (_weaponAlignEnabled || _cosmeticAgentsEnabled))
        {
            foreach (var slot in _loadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_loadedReplays.TryGetValue(slot, out var replay) && !_cosmeticSyncedSlots.Contains(slot))
                    ApplyLoadedReplayCosmeticsForSlot(slot, replay);
            }
        }

        ApplyLoadedReplayScoreboards();
        AlignSafeC4OwnerForLoadedReplays();
        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void ApplyLoadedReplayMusicKits()
    {
        if (!_cosmeticAlignEnabled)
            return;

        foreach (var slot in _loadedSlots)
        {
            if (!IsReplaySlotStillSafe(slot) ||
                !_loadedReplays.TryGetValue(slot, out var replay) ||
                replay.MusicKitId <= 0)
            {
                continue;
            }

            _ = ApplyReplayMusicKitForSlot(slot, replay.MusicKitId);
        }
    }

    private bool ApplyReplayMusicKitForSlot(int slot, int musicKitId)
    {
        if (!ReplayMusicKitAlignmentAllowed(musicKitId) ||
            !IsReplaySlotStillSafe(slot))
            return false;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
            return false;

        try
        {
            if (ReplayMusicKitStateMatches(player, musicKitId))
                return true;
            return ApplyReplayMusicKit(player, musicKitId);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: music kit apply failed slot={slot} kit={musicKitId}: {ex.Message}");
            return false;
        }
    }

    private void ScheduleLoadedReplayMusicKitRepairs()
    {
        if (!_cosmeticAlignEnabled)
            return;

        foreach (var slot in _loadedSlots.ToArray())
            ScheduleReplayMusicKitRepairForSlot(slot);
    }

    private void ScheduleReplayMusicKitRepairForSlot(int slot)
    {
        if (!_loadedReplays.TryGetValue(slot, out var replay) ||
            !ReplayMusicKitAlignmentAllowed(replay.MusicKitId))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } || player.UserId is not int userId)
            return;

        var generation = CurrentReplayIdentityGeneration(slot);
        var expectedMusicKitId = replay.MusicKitId;
        var repairToken = ++_nextReplayMusicKitRepairToken;
        _replayMusicKitRepairTokens[slot] = repairToken;

        void ReconcileWhileCurrent(bool finalAttempt)
        {
            try
            {
                if (!_replayMusicKitRepairTokens.TryGetValue(slot, out var currentToken) ||
                    currentToken != repairToken ||
                    !IsReplayIdentityGenerationCurrent(slot, generation) ||
                    !_loadedReplays.TryGetValue(slot, out var current) ||
                    current.MusicKitId != expectedMusicKitId ||
                    Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } currentPlayer ||
                    currentPlayer.UserId != userId)
                {
                    return;
                }

                _ = ApplyReplayMusicKitForSlot(slot, expectedMusicKitId);
            }
            finally
            {
                if (finalAttempt &&
                    _replayMusicKitRepairTokens.TryGetValue(slot, out var currentToken) &&
                    currentToken == repairToken)
                {
                    _replayMusicKitRepairTokens.Remove(slot);
                }
            }
        }

        // BotRandomizer publishes its stable per-bot kit on its spawn NextFrame.
        // Re-resolve the same slot/user/generation and reconcile once afterwards;
        // this avoids both callback-order races and the old repeated write storm.
        Server.NextFrame(() => ReconcileWhileCurrent(finalAttempt: false));
        AddTimer(
            ReplayMusicKitReconcileDelaySeconds,
            () => ReconcileWhileCurrent(finalAttempt: true),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool ApplyReplayMusicKit(
        CCSPlayerController player,
        int musicKitId)
    {
        if (!ReplayMusicKitAlignmentAllowed(musicKitId) ||
            player is not { IsValid: true } ||
            musicKitId is > ushort.MaxValue)
            return false;

        var inventory = player.InventoryServices;
        if (inventory is null || !CaptureReplayMusicKitBaseline(player, inventory))
            return false;

        inventory.MusicID = (ushort)musicKitId;
        TrySetReplayMusicKitStateChanged(
            player,
            "CCSPlayerController",
            "m_pInventoryServices");

        player.MusicKitID = musicKitId;
        TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
        player.MusicKitMVPs = 0;
        TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
        player.MvpNoMusic = false;
        TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");

        return ReplayMusicKitStateMatches(player, musicKitId);
    }

    private bool CaptureReplayMusicKitBaseline(
        CCSPlayerController player,
        CCSPlayerController_InventoryServices inventory)
    {
        var slot = player.Slot;
        if (player.UserId is not int userId ||
            !_replayIdentityGenerationBySlot.TryGetValue(slot, out var generation))
            return false;

        if (_replayMusicKitBaselines.TryGetValue(slot, out var existing))
        {
            if (existing.Generation == generation && existing.UserId == userId)
                return true;
            _replayMusicKitBaselines.Remove(slot);
        }

        _replayMusicKitBaselines[slot] = new ReplayMusicKitBaseline(
            generation,
            userId,
            inventory.MusicID,
            player.MusicKitID,
            player.MusicKitMVPs,
            player.MvpNoMusic);
        return true;
    }

    private void RestoreReplayMusicKitForSlot(int slot, string reason)
    {
        InvalidateReplayMusicKitRepair(slot);
        if (!ManagedSchemaWritesAllowed())
        {
            _replayMusicKitBaselines.Remove(slot);
            return;
        }

        if (!_replayMusicKitBaselines.TryGetValue(slot, out var baseline))
            return;

        if (!IsReplayIdentityGenerationCurrent(slot, baseline.Generation) ||
            !IsReplaySlotStillSafe(slot))
        {
            _replayMusicKitBaselines.Remove(slot);
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.UserId != baseline.UserId)
        {
            _replayMusicKitBaselines.Remove(slot);
            return;
        }
        var inventory = player.InventoryServices;
        if (inventory is null)
        {
            _replayMusicKitBaselines.Remove(slot);
            return;
        }

        try
        {
            inventory.MusicID = baseline.InventoryMusicKitId;
            TrySetReplayMusicKitStateChanged(
                player,
                "CCSPlayerController",
                "m_pInventoryServices");

            player.MusicKitID = baseline.ControllerMusicKitId;
            TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
            player.MusicKitMVPs = baseline.ControllerMusicKitMvps;
            TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
            player.MvpNoMusic = baseline.MvpNoMusic;
            TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: music kit restore failed slot={slot} reason={reason}: {ex.Message}");
        }
        finally
        {
            _replayMusicKitBaselines.Remove(slot);
        }
    }

    private void RestoreAllReplayMusicKits(string reason)
    {
        foreach (var slot in _replayMusicKitBaselines.Keys.ToArray())
            RestoreReplayMusicKitForSlot(slot, reason);
        _replayMusicKitBaselines.Clear();
        _replayMusicKitRepairTokens.Clear();
    }

    private void InvalidateReplayMusicKitRepair(int slot)
        => _replayMusicKitRepairTokens.Remove(slot);

    private static bool ReplayMusicKitStateMatches(CCSPlayerController player, int expectedMusicKitId)
    {
        var inventory = player.InventoryServices;
        return ReplayMusicKitStateMatches(
            expectedMusicKitId,
            inventory is null ? null : inventory.MusicID,
            player.MusicKitID,
            player.MusicKitMVPs,
            player.MvpNoMusic);
    }

    internal static bool ReplayMusicKitStateMatches(
        int expectedMusicKitId,
        int? inventoryMusicKitId,
        int controllerMusicKitId,
        int controllerMusicKitMvps,
        bool mvpNoMusic)
        => expectedMusicKitId is > 0 and <= ushort.MaxValue &&
           inventoryMusicKitId == (ushort)expectedMusicKitId &&
           controllerMusicKitId == expectedMusicKitId &&
           controllerMusicKitMvps == 0 &&
           !mvpNoMusic;

    private bool ReplayMusicKitAlignmentAllowed(int musicKitId)
        => ShouldApplyReplayMusicKit(
            _cosmeticAlignEnabled,
            ManagedSchemaWritesAllowed(),
            musicKitId);

    internal static bool ShouldApplyReplayMusicKit(
        bool cosmeticAlignEnabled,
        bool runtimeSupported,
        int musicKitId)
        => cosmeticAlignEnabled &&
           runtimeSupported &&
           musicKitId is > 0 and <= ushort.MaxValue;

    private static bool ManagedSchemaWritesAllowed()
        => ManagedSchemaRuntime.Value.Allowed;

    private static (bool Allowed, string Patch) DetectManagedSchemaRuntime()
    {
        try
        {
            var steamInfPath = Path.Combine(Server.GameDirectory, "steam.inf");
            var patchLine = File.ReadLines(steamInfPath)
                .FirstOrDefault(line => line.StartsWith("PatchVersion=", StringComparison.OrdinalIgnoreCase));
            var patch = patchLine?["PatchVersion=".Length..].Trim() ?? "unknown";
            return IsManagedSchemaPatchSupported(patch)
                ? (true, patch)
                : (false, patch);
        }
        catch
        {
            return (false, "unknown");
        }
    }

    internal static bool IsManagedSchemaPatchSupported(string? patch)
        => Version.TryParse(patch, out var current) &&
           current.CompareTo(MaxVerifiedManagedSchemaPatch) <= 0;

    private static void TrySetReplayMusicKitStateChanged(
        CBaseEntity entity,
        string className,
        string fieldName)
    {
        try
        {
            // Current CSS exposes these controller values for direct server-side
            // reads/writes but does not network every field. Calling
            // SetStateChanged for a non-networked field only emits warnings and
            // duplicates BotRandomizer's presentation traffic.
            if (!Schema.IsSchemaFieldNetworked(className, fieldName))
                return;
            Utilities.SetStateChanged(entity, className, fieldName);
        }
        catch
        {
            // Presentation metadata is best-effort; the MVP event still carries
            // the demo-backed kit even when a field cannot be network-dirtied.
        }
    }

    private int NormalizeMusicKitId(uint? musicKitId)
        => musicKitId is > 0 and <= int.MaxValue && IsKnownMusicKitId((int)musicKitId.Value)
            ? (int)musicKitId.Value
            : 0;

    private int NormalizeMusicKitId(int musicKitId)
        => IsKnownMusicKitId(musicKitId) ? musicKitId : 0;

    private void AlignSafeC4OwnerForLoadedReplays(bool forceReconcile = false)
    {
        if (_safeC4Aligned && !forceReconcile)
            return;

        var plantedOwner = FindLoadedC4Owner(IsBombPlantedEvent);
        var initialOwner = FindLoadedC4Owner(IsBombInitialOwnerEvent);
        var targetOwner = plantedOwner ?? initialOwner;

        if (!targetOwner.HasValue)
            return;

        var targetSlot = targetOwner.Value.Slot;
        var targetSteamId = targetOwner.Value.SteamId;
        if (targetSlot < 0 || !IsReplaySlotStillSafe(targetSlot))
            return;
        var firstAlignment = !_safeC4Aligned;

        // CS2 may assign its native C4 to another live T, including a human who
        // joins during freeze time. Demo evidence makes this replay slot the
        // authoritative owner, so purge every other player rather than only bots.
        foreach (var candidate in FindTeamPlayers())
        {
            if (candidate.Slot == targetSlot)
                continue;
            RemoveC4FromPlayer(candidate, "safe_c4_owner_align");
        }

        var player = Utilities.GetPlayerFromSlot(targetSlot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;
        if (CountCurrentReplayItems(player, "weapon_c4") <= 0 &&
            !TryGiveNamedItem(player, "weapon_c4"))
        {
            Server.PrintToConsole(
                $"dtr: C4 safe owner align failed slot={targetSlot} steam_id={targetSteamId}");
            return;
        }

        _safeC4Aligned = true;
        if (!firstAlignment)
            return;

        if (plantedOwner.HasValue &&
            initialOwner.HasValue &&
            plantedOwner.Value.SteamId != initialOwner.Value.SteamId)
        {
            Server.PrintToConsole(
                "dtr: C4 safe owner collapsed to planter " +
                $"slot={targetSlot} steam_id={targetSteamId} initial_steam_id={initialOwner.Value.SteamId}");
            return;
        }

        var source = plantedOwner.HasValue ? "bomb_planted" : "bomb_initial_owner";
        Server.PrintToConsole(
            $"dtr: C4 safe owner aligned slot={targetSlot} steam_id={targetSteamId} source={source}");
    }

    private static bool IsBombInitialOwnerEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_initial_owner", StringComparison.OrdinalIgnoreCase);

    private static bool IsBombPlantedEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_planted", StringComparison.OrdinalIgnoreCase);

    private (int Slot, ulong SteamId)? FindLoadedC4Owner(Func<ReplayHifiEvent, bool> predicate)
    {
        foreach (var slot in _loadedSlots)
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay))
                continue;

            var replayEvent = replay.HifiEvents.FirstOrDefault(predicate);
            if (replayEvent is null)
                continue;

            var steamId = replayEvent.ActorSteamId.GetValueOrDefault(replay.SteamId);
            return (slot, steamId);
        }

        return null;
    }

    private static void RemoveC4FromPlayer(CCSPlayerController player, string reason)
    {
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        var pawn = player.PlayerPawn.Value;
        foreach (var weapon in GetReplayWeaponsByClass(pawn, "weapon_c4").ToArray())
            DropAndKillReplayWeapon(player, pawn, weapon, reason);
    }

    private string StartLoaded(bool loop)
        => StartLoaded(loop, ReplayStartAnchor.Live, null);

    private string StartLoaded(
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        var respawned = RespawnDeadLoadedReplayBots();
        if (respawned > 0)
        {
            Server.NextFrame(() =>
            {
                PreloadLoadedReplays();
                Server.PrintToConsole(
                    $"dtr: queued start after respawn: {StartLoadedReady(loop, anchor, freezeTimeSeconds)}");
            });
            return $"dtr: respawned {respawned} replay bot(s), start queued";
        }

        return StartLoadedReady(loop, anchor, freezeTimeSeconds);
    }

    private string StartLoadedReady(
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        if (IsWarmupPeriod())
            return "[DTR ERR] 热身阶段无法进行回放";

        if (!TryAssignInitialRoundSpawns(out var spawnReason))
            return $"[DTR ERR] initial spawn assignment is not ready: {spawnReason}";

        var ok = 0;
        foreach (var slot in _loadedSlots)
        {
            _lastEnsuredWeaponDef.Remove(slot);
            _lastReplayWeaponDef.Remove(slot);
            _lastLockedWeaponTarget.Remove(slot);

            if (!IsReplaySlotStillSafe(slot))
            {
                ReleaseReplaySlot(slot, "unsafe_start_target");
                continue;
            }

            if (_loadedReplays.TryGetValue(slot, out var replay))
            {
                if (!ResetReplayPawnRoundStartHealth(slot))
                {
                    ReleaseReplaySlot(slot, "dead_start_target");
                    continue;
                }
            }

            if (StartReplayForSlot(slot, loop, anchor, freezeTimeSeconds))
            {
                MarkReplayStarted(slot);
                ok++;
            }
        }
        var voice = TryStartLoadedAutoVoicePlayback(anchor, freezeTimeSeconds, ok);
        var chat = TryStartLoadedAutoChatPlayback(anchor, freezeTimeSeconds, ok);
        return $"dtr: started {ok}/{_loadedSlots.Count} loaded slots, loop={loop}{voice}{chat}";
    }

    private void ScheduleInitialRoundSpawnAssignment()
    {
        if (_initialSpawnAssignmentComplete ||
            _initialSpawnAssignmentScheduled ||
            _loadedSlots.Count == 0)
        {
            return;
        }

        var token = _initialSpawnAssignmentToken;
        var attempts = 0;
        _initialSpawnAssignmentScheduled = true;

        void TryAssign()
        {
            if (token != _initialSpawnAssignmentToken)
                return;

            attempts++;
            if (TryAssignInitialRoundSpawns(out var reason))
            {
                _initialSpawnAssignmentComplete = true;
                _initialSpawnAssignmentScheduled = false;
                return;
            }

            if (attempts >= InitialSpawnAssignmentMaxAttempts)
            {
                _initialSpawnAssignmentScheduled = false;
                Server.PrintToConsole(
                    $"[DTR WARN] initial spawn assignment was not ready after {attempts} tick(s): {reason}");
                return;
            }

            Server.NextFrame(TryAssign);
        }

        TryAssign();
    }

    private bool TryAssignInitialRoundSpawns(out string reason)
    {
        if (_initialSpawnAssignmentComplete)
        {
            reason = string.Empty;
            return true;
        }

        var players = FindTeamPlayers();
        var replayPlacements = new List<(
            CCSPlayerController Player,
            int Slot,
            CsTeam Team,
            ReplayVector3 Destination)>();
        var humanRelocations = new List<(
            CCSPlayerController Player,
            int VacatedBySlot,
            CsTeam Team,
            ReplayVector3 Origin)>();
        var summaries = new List<(CsTeam Team, int ReplaySlots, int SubstituteHumansMoved)>();

        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var plannedSlots = _loadedSlots
                .Where(slot => _loadedReplays.TryGetValue(slot, out var replay) && replay.ManifestTeam == team)
                .ToArray();
            if (plannedSlots.Length == 0 || plannedSlots.Length >= StandardTeamSize)
                continue;

            var replaySpawns = new List<(
                CCSPlayerController Player,
                int Slot,
                ReplayVector3 Destination,
                ReplayVector3 Current)>();
            foreach (var slot in plannedSlots)
            {
                if (!_loadedReplays.TryGetValue(slot, out var replay) ||
                    replay.RoundStartOrigin is not { } destination ||
                    !IsFiniteReplayPosition(destination))
                {
                    reason = $"slot={slot} team={team} has no valid recorded round-start origin";
                    return false;
                }

                var replayPlayer = players.FirstOrDefault(candidate => candidate.Slot == slot);
                if (replayPlayer is not { IsValid: true, PawnIsAlive: true } ||
                    replayPlayer.Team != team ||
                    !TryGetPawnOrigin(replayPlayer, out var current))
                {
                    reason = $"slot={slot} team={team} pawn is not spawned yet";
                    return false;
                }

                replaySpawns.Add((
                    replayPlayer,
                    slot,
                    destination,
                    new ReplayVector3(current.X, current.Y, current.Z)));
            }

            var humans = new List<(CCSPlayerController Player, ReplayVector3 Current)>();
            foreach (var candidate in players)
            {
                if (candidate.Team != team ||
                    !candidate.PawnIsAlive ||
                    candidate.IsBot ||
                    _botHiderBridge.IsManagedBot(candidate.Slot) ||
                    _loadedSlots.Contains(candidate.Slot) ||
                    (TryGetControllingBotState(candidate, out var controllingBot) && controllingBot) ||
                    !TryGetPawnOrigin(candidate, out var current))
                {
                    continue;
                }

                humans.Add((
                    candidate,
                    new ReplayVector3(current.X, current.Y, current.Z)));
            }

            var conflicts = humans
                .Where(human => replaySpawns.Any(
                    replay => PlayerHullsOverlap(human.Current, replay.Destination)))
                .ToList();
            var occupiedOrigins = humans
                .Where(human => !conflicts.Any(conflict => conflict.Player.Slot == human.Player.Slot))
                .Select(human => human.Current)
                .ToList();
            var relocationSpawns = new List<(int Slot, ReplayVector3 Origin)>();
            foreach (var replay in replaySpawns)
            {
                if (replaySpawns.Any(candidate =>
                        PlayerHullsOverlap(replay.Current, candidate.Destination)) ||
                    occupiedOrigins.Any(origin => PlayerHullsOverlap(replay.Current, origin)) ||
                    relocationSpawns.Any(candidate =>
                        PlayerHullsOverlap(replay.Current, candidate.Origin)))
                {
                    continue;
                }

                relocationSpawns.Add((replay.Slot, replay.Current));
            }

            var teamRelocations = new List<(
                CCSPlayerController Player,
                int VacatedBySlot,
                CsTeam Team,
                ReplayVector3 Origin)>();
            foreach (var conflict in conflicts)
            {
                if (relocationSpawns.Count == 0)
                {
                    reason =
                        $"slot={conflict.Player.Slot} team={team} has no safe DTR-native spawn to inherit";
                    return false;
                }

                var relocation = relocationSpawns
                    .OrderBy(candidate => PositionDistanceSquared(conflict.Current, candidate.Origin))
                    .First();

                relocationSpawns.Remove(relocation);
                occupiedOrigins.Add(relocation.Origin);
                teamRelocations.Add((conflict.Player, relocation.Slot, team, relocation.Origin));
            }

            humanRelocations.AddRange(teamRelocations);
            replayPlacements.AddRange(replaySpawns.Select(replay => (
                replay.Player,
                replay.Slot,
                team,
                replay.Destination)));
            summaries.Add((team, replaySpawns.Count, teamRelocations.Count));
        }

        try
        {
            // Preserve the game's native spawn allocation for substitute
            // humans before placing replay bots at demo-backed positions.
            foreach (var relocation in humanRelocations)
            {
                var pawn = relocation.Player.PlayerPawn.Value;
                if (pawn is not { IsValid: true })
                {
                    reason =
                        $"human slot={relocation.Player.Slot} team={relocation.Team} pawn became invalid";
                    return false;
                }

                pawn.Teleport(
                    new Vector(relocation.Origin.X, relocation.Origin.Y, relocation.Origin.Z),
                    null,
                    new Vector(0.0f, 0.0f, 0.0f));
            }

            foreach (var replay in replayPlacements)
            {
                var pawn = replay.Player.PlayerPawn.Value;
                if (pawn is not { IsValid: true })
                {
                    reason = $"replay slot={replay.Slot} team={replay.Team} pawn became invalid";
                    return false;
                }

                pawn.Teleport(
                    new Vector(replay.Destination.X, replay.Destination.Y, replay.Destination.Z),
                    null,
                    new Vector(0.0f, 0.0f, 0.0f));
            }

            foreach (var summary in summaries)
            {
                Server.PrintToConsole(
                    "dtr: initial partial-roster spawns assigned " +
                    $"team={summary.Team} dtr_slots={summary.ReplaySlots}/{StandardTeamSize} " +
                    $"substitute_humans_moved={summary.SubstituteHumansMoved}");
            }
        }
        catch (Exception ex)
        {
            reason = $"initial spawn teleport failed: {ex.Message}";
            return false;
        }

        _initialSpawnAssignmentComplete = true;
        reason = string.Empty;
        return true;
    }

    private void InvalidateInitialSpawnAssignment()
    {
        _initialSpawnAssignmentToken++;
        _initialSpawnAssignmentComplete = false;
        _initialSpawnAssignmentScheduled = false;
    }

    private static bool IsFiniteReplayPosition(ReplayVector3 position)
        => float.IsFinite(position.X) &&
           float.IsFinite(position.Y) &&
           float.IsFinite(position.Z);

    private static bool PlayerHullsOverlap(ReplayVector3 left, ReplayVector3 right)
        => MathF.Abs(left.X - right.X) < PlayerHullWidth &&
           MathF.Abs(left.Y - right.Y) < PlayerHullWidth &&
           MathF.Abs(left.Z - right.Z) < PlayerHullHeight;

    private static float PositionDistanceSquared(ReplayVector3 left, ReplayVector3 right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private bool StartReplayForSlot(int slot, bool loop)
        => StartReplayForSlot(slot, loop, ReplayStartAnchor.Live, null);

    private bool StartReplayForSlot(
        int slot,
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        if (IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

        var startIndex = 0u;
        if (_loadedReplays.TryGetValue(slot, out var replay))
        {
            if (anchor == ReplayStartAnchor.FreezePreroll)
            {
                if (replay.PlayStartTickIndex == 0)
                    return false;
                if (_freezePrerollSlots.Contains(slot) &&
                    BotControllerNative.GetReplayState(slot).Playing)
                {
                    return true;
                }

                startIndex = FreezePrerollStartIndex(replay, freezeTimeSeconds ?? 0.0f);
                var startedUntil = startIndex < replay.PlayStartTickIndex &&
                                   RegisterReplayPawnForSlot(slot) &&
                                   BotControllerNative.StartReplayUntil(
                                       slot,
                                       loop,
                                       startIndex,
                                       replay.PlayStartTickIndex);
                if (!startedUntil)
                    return false;

                // Keep native Update/Upkeep alive during pre-roll. Replay
                // hooks own movement/view/input, while the persistent buy skip
                // gates only vanilla purchase intent. Handoff depends on the
                // native bot continuing to perceive and warm its state here.
                _freezePrerollSlots.Add(slot);
                _resumedFreezePrerollSlots.Remove(slot);
                _demoTracerOwnedSlots.Add(slot);
                return true;
            }

            startIndex = anchor switch
            {
                ReplayStartAnchor.Live => replay.PlayStartTickIndex,
                _ => 0,
            };
        }
        _freezePrerollSlots.Remove(slot);
        if (anchor == ReplayStartAnchor.Live &&
            _resumedFreezePrerollSlots.Remove(slot) &&
            BotControllerNative.GetReplayState(slot).Playing)
        {
            _demoTracerOwnedSlots.Add(slot);
            return true;
        }
        var started = RegisterReplayPawnForSlot(slot) &&
                      BotControllerNative.StartReplayAt(slot, loop, startIndex);
        if (started)
            _demoTracerOwnedSlots.Add(slot);
        return started;
    }

    private static bool RegisterReplayPawnForSlot(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        // Best-effort on old native builds; the normal start path still does
        // the authoritative lock/replay validation.
        _ = BotControllerNative.SetReplayPawn(slot, player.PlayerPawn.Value.Handle);
        return true;
    }

    private void ScheduleFreezePrerollStart(string label)
    {
        if (!TryGetFreezePrerollSchedule(
                out var freezeTimeSeconds,
                out var delaySeconds,
                out _,
                out var reason))
        {
            Server.PrintToConsole($"dtr: freeze pre-roll skipped for {label}: {reason}");
            return;
        }

        var token = ++_freezePrerollToken;
        var attempts = 0;
        _freezePrerollStarted = false;
        void TryStart()
        {
            if (token != _freezePrerollToken || _freezePrerollStarted)
                return;

            attempts++;
            if (!TryGetFreezePrerollSchedule(
                    out var currentFreezeTimeSeconds,
                    out _,
                    out var playbackPrerollSeconds,
                    out var currentReason))
            {
                Server.PrintToConsole(
                    $"[DTR WARN] freeze pre-roll expired before {label} became ready: {currentReason}");
                return;
            }

            if (!LoadedReplayPawnsReadyForFreezePreroll(out var readinessReason) ||
                !ReassertLoadedReplayBuySkips(out readinessReason))
            {
                if (attempts == 1 || attempts % 64 == 0)
                {
                    Server.PrintToConsole(
                        $"[DTR WARN] freeze pre-roll waiting for replay pawns on {label}: {readinessReason}");
                }
                Server.NextFrame(TryStart);
                return;
            }

            PreloadLoadedReplays();
            var message = StartLoadedReady(
                loop: false,
                ReplayStartAnchor.FreezePreroll,
                playbackPrerollSeconds);
            var missingSlots = MissingFreezePrerollStartSlots();
            if (missingSlots.Length > 0)
            {
                if (attempts == 1 || attempts % 64 == 0)
                {
                    Server.PrintToConsole(
                        $"[DTR WARN] freeze pre-roll retry {label}: slots={string.Join(",", missingSlots)}; {message}");
                }
                Server.NextFrame(TryStart);
                return;
            }

            _freezePrerollStarted = true;
            Server.PrintToConsole(
                $"dtr: freeze pre-roll start {label}: mp_freezetime={currentFreezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}s playback={playbackPrerollSeconds.ToString("F2", CultureInfo.InvariantCulture)}s delay={delaySeconds.ToString("F2", CultureInfo.InvariantCulture)}s; {message}");
        }

        if (delaySeconds <= 0.01f)
        {
            Server.NextFrame(TryStart);
        }
        else
        {
            AddTimer(delaySeconds, TryStart);
            Server.PrintToConsole(
                $"dtr: freeze pre-roll scheduled {label}: mp_freezetime={freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}s delay={delaySeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        }
    }

    private int[] FreezePrerollExpectedSlots()
        => _loadedSlots
            .Where(slot => _loadedReplays.TryGetValue(slot, out var replay) &&
                           replay.PlayStartTickIndex > 0)
            .Distinct()
            .Order()
            .ToArray();

    private bool LoadedReplayPawnsReadyForFreezePreroll(out string reason)
    {
        foreach (var slot in FreezePrerollExpectedSlots())
        {
            if (!IsReplaySlotStillSafe(slot))
            {
                reason = $"slot={slot} is no longer a safe replay bot";
                return false;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, PawnIsAlive: true } ||
                player.PlayerPawn is not { IsValid: true, Value.IsValid: true } ||
                player.PlayerPawn.Value.WeaponServices == null)
            {
                reason = $"slot={slot} pawn or weapon services are not ready";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool ReassertLoadedReplayBuySkips(out string reason)
    {
        foreach (var slot in FreezePrerollExpectedSlots())
        {
            if (!BotControllerNative.SetBuySkip(slot))
            {
                reason = $"slot={slot} native buy suppression failed";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private int[] MissingFreezePrerollStartSlots()
        => FreezePrerollExpectedSlots()
            .Where(slot => !_freezePrerollSlots.Contains(slot))
            .ToArray();

    private int[] MissingFreezePrerollResumeSlots()
        => FreezePrerollExpectedSlots()
            .Where(slot => !_resumedFreezePrerollSlots.Contains(slot))
            .ToArray();

    private void InvalidateFreezePreroll()
    {
        _freezePrerollToken++;
        _freezePrerollStarted = false;
    }

    private void ResumeFreezePrerollReplays(bool loop)
    {
        foreach (var slot in _freezePrerollSlots.ToArray())
        {
            if (_loadedReplays.TryGetValue(slot, out var replay) &&
                replay.PlayStartTickIndex > 0 &&
                BotControllerNative.GetReplayState(slot).Playing &&
                BotControllerNative.StartReplayAt(
                    slot,
                    loop,
                    replay.PlayStartTickIndex))
            {
                _resumedFreezePrerollSlots.Add(slot);
                continue;
            }

            // Never leave a held freeze command active after the server enters
            // live play. The normal next-frame start path may retry this slot.
            _ = BotControllerNative.StopReplay(slot);
            _resumedFreezePrerollSlots.Remove(slot);
        }
        _freezePrerollSlots.Clear();
    }

    private void ClearFreezePrerollReplayState()
    {
        _freezePrerollSlots.Clear();
        _resumedFreezePrerollSlots.Clear();
    }

    private bool TryGetFreezePrerollSchedule(
        out float freezeTimeSeconds,
        out float delaySeconds,
        out float playbackPrerollSeconds,
        out string reason)
    {
        freezeTimeSeconds = 0.0f;
        delaySeconds = 0.0f;
        playbackPrerollSeconds = 0.0f;
        if (!TryReadFreezeTimeConVar(out freezeTimeSeconds, out reason))
            return false;
        if (freezeTimeSeconds <= 0.0f)
        {
            reason = $"{FreezeTimeConVarName} is {freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}";
            return false;
        }

        var maxRecordedPrerollSeconds = 0.0f;
        foreach (var replay in _loadedReplays.Values)
        {
            if (replay.PlayStartTickIndex == 0 || replay.TickRate <= 0.0f)
                continue;
            maxRecordedPrerollSeconds = Math.Max(
                maxRecordedPrerollSeconds,
                replay.PlayStartTickIndex / replay.TickRate);
        }

        if (maxRecordedPrerollSeconds <= 0.0f)
        {
            reason = "loaded replays have no recorded freeze pre-roll";
            return false;
        }

        var scheduleWindowSeconds = freezeTimeSeconds;
        if (TryReadFreezePhaseRemaining(out var phaseRemainingSeconds, out _) &&
            phaseRemainingSeconds > 0.0f)
        {
            scheduleWindowSeconds = phaseRemainingSeconds;
        }

        var timing = ComputeFreezePrerollTiming(
            freezeTimeSeconds,
            scheduleWindowSeconds,
            maxRecordedPrerollSeconds);
        delaySeconds = timing.DelaySeconds;
        playbackPrerollSeconds = timing.PlaybackSeconds;
        reason = string.Empty;
        return true;
    }

    internal static FreezePrerollTiming ComputeFreezePrerollTiming(
        float freezeTimeSeconds,
        float phaseRemainingSeconds,
        float maxRecordedPrerollSeconds)
    {
        var safeFreezeSeconds = Math.Max(0.0f, freezeTimeSeconds);
        var safeRemainingSeconds = Math.Clamp(
            phaseRemainingSeconds,
            0.0f,
            safeFreezeSeconds);
        var safeRecordedSeconds = Math.Max(0.0f, maxRecordedPrerollSeconds);
        var playbackSeconds = Math.Min(safeRemainingSeconds, safeRecordedSeconds);
        return new FreezePrerollTiming(
            Math.Max(0.0f, safeRemainingSeconds - playbackSeconds),
            playbackSeconds);
    }

    private static bool TryReadFreezePhaseRemaining(out float seconds, out string reason)
    {
        seconds = 0.0f;
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            if (proxy is not { IsValid: true })
            {
                reason = "cs_gamerules entity was not found";
                return false;
            }

            var rules = proxy.GameRules;
            if (rules == null)
            {
                reason = "cs_gamerules has no rules object";
                return false;
            }

            if (!rules.FreezePeriod)
            {
                reason = "game rules are not in freeze period";
                return false;
            }

            var phaseTime = rules.TimeUntilNextPhaseStarts;
            if (!float.IsFinite(phaseTime))
            {
                reason = "game rules phase end time is invalid";
                return false;
            }

            seconds = phaseTime > Server.CurrentTime
                ? phaseTime - Server.CurrentTime
                : phaseTime;
            if (seconds > 0.0f && float.IsFinite(seconds))
            {
                reason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = $"failed to read game rules freeze phase: {ex.Message}";
            return false;
        }

        reason = "game rules freeze phase has no remaining time";
        return false;
    }

    private static bool IsWarmupPeriod()
    {
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            return proxy is { IsValid: true } &&
                   proxy.GameRules != null &&
                   proxy.GameRules.WarmupPeriod;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadFreezeTimeConVar(out float seconds, out string reason)
    {
        seconds = 0.0f;
        var conVar = ConVar.Find(FreezeTimeConVarName);
        if (conVar == null)
        {
            reason = $"server ConVar {FreezeTimeConVarName} was not found";
            return false;
        }

        try
        {
            seconds = conVar.GetPrimitiveValue<float>();
        }
        catch
        {
            try
            {
                seconds = conVar.GetPrimitiveValue<int>();
            }
            catch (Exception ex)
            {
                reason = $"failed to read {FreezeTimeConVarName}: {ex.Message}";
                return false;
            }
        }

        if (float.IsFinite(seconds) && seconds >= 0.0f)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{FreezeTimeConVarName} has invalid value {seconds.ToString(CultureInfo.InvariantCulture)}";
        return false;
    }

    private static uint FreezePrerollStartIndex(LoadedReplay replay, float freezeTimeSeconds)
    {
        if (replay.PlayStartTickIndex == 0 || replay.TickRate <= 0.0f || freezeTimeSeconds <= 0.0f)
            return replay.PlayStartTickIndex;

        var serverFreezeTicks = (uint)Math.Round(freezeTimeSeconds * replay.TickRate);
        return serverFreezeTicks >= replay.PlayStartTickIndex
            ? 0
            : replay.PlayStartTickIndex - serverFreezeTicks;
    }

    private int RespawnDeadLoadedReplayBots()
    {
        var respawned = 0;
        foreach (var slot in _loadedSlots)
        {
            if (!_loadedReplays.TryGetValue(slot, out var replay))
                continue;

            if (!IsReplaySlotStillSafe(slot))
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || player.PawnIsAlive)
                continue;

            try
            {
                player.Respawn();
                _loadoutSyncedSlots.Remove(slot);
                _rebuiltInventorySlots.Remove(slot);
                _lastEnsuredWeaponDef.Remove(slot);
                _lastReplayWeaponDef.Remove(slot);
                _lastLockedWeaponTarget.Remove(slot);
                _cosmeticSyncedSlots.Remove(slot);
                _cosmeticHeartbeatTokens.Remove(slot);
                _activeWeaponCosmetics.Remove(slot);
                _appliedGloveCosmetics.Remove(slot);
                _gloveCosmeticTokens.Remove(slot);
                respawned++;
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"dtr: failed to respawn replay bot slot={slot}: {ex.Message}");
            }
        }

        return respawned;
    }

    private void StopAndUnloadLoaded()
        => StopAndUnloadLoaded(clearArmedPlan: true, releaseBuffers: true);

    private void StopAndUnloadLoaded(bool clearArmedPlan)
        => StopAndUnloadLoaded(clearArmedPlan, releaseBuffers: true);

    private void StopAndUnloadLoaded(bool clearArmedPlan, bool releaseBuffers)
    {
        CancelDtrRoundBanner(resetRound: false);
        InvalidateInitialSpawnAssignment();
        var trackedSlots = _loadedSlots.ToHashSet();
        StopVoiceTestPlayback("unload_all", printSummary: false);
        ClearLoadedAutoVoiceClip();
        ClearLoadedAutoChat();
        RestoreAllReplayMusicKits("unload_all");
        foreach (var slot in _loadedSlots.ToArray())
        {
            if (releaseBuffers)
            {
                BotControllerNative.UnloadReplay(slot);
                _warmReplayBufferSlots.Remove(slot);
            }
            else
            {
                BotControllerNative.StopReplay(slot);
                _warmReplayBufferSlots.Add(slot);
            }
            ReleaseReplaySlot(slot, "unload_all");
        }
        if (releaseBuffers)
            ReleaseUnusedWarmReplayBuffers();
        StopUntrackedNativeReplaySlots(trackedSlots, "unload_all");
        _loadedSlots.Clear();
        _demoTracerOwnedSlots.Clear();
        _loadedReplays.Clear();
        ResetCosmeticEvidenceCache();
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _pendingWeaponAlign.Clear();
        _pendingWeaponSlotReplacements.Clear();
        _activeWeaponCosmetics.Clear();
        _projectileAlignNextBySlot.Clear();
        _replayIdentityGenerationBySlot.Clear();
        _replayMutationGenerationBySlot.Clear();
        _pendingProjectileAlign.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
        _rebuiltInventorySlots.Clear();
        _loadoutSyncedSlots.Clear();
        ResetCosmeticAlignState(resetCounters: true);
        ResetStickerAlignState(resetCounters: true);
        ResetCharmAlignState(resetCounters: true);
        ResetCrosshairAlignState(resetCounters: true);
        ResetViewmodelAlignState(resetCounters: true);
        ResetScoreboardAlignState(resetCounters: true);
        _loadedRoundScoreboard = null;
        _lastPlayingSlots.Clear();
        _replayStartedAt.Clear();
        _replayPerceptionBaselineSerial.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _pendingThreat360.Clear();
        _safeC4Aligned = false;
        if (clearArmedPlan)
        {
            _armed = false;
            _armedPrepared = false;
            _armedManifestPath = string.Empty;
            _armedSourceRound = -1;
        }
        else
        {
            _armedPrepared = false;
        }
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        SetReplayPovMask(0);
    }

    private void ClearReplayStateForLifecycle(string reason)
    {
        if (_lifecycleResetInProgress)
            return;

        _lifecycleResetInProgress = true;
        try
        {
            CancelReplayPrefetch();
            InvalidateInitialSpawnAssignment();
            ClearFreezePrerollReplayState();
            ClearReplayLeftHandDesiredLatches(forceNative: true);
            var hadReplayState = _loadedSlots.Count > 0 ||
                                 _demoTracerOwnedSlots.Count > 0 ||
                                 _loadedReplays.Count > 0 ||
                                 _lastPlayingSlots.Count > 0 ||
                                 _retainedReplayViewmodelSlots.Count > 0 ||
                                 _pendingProjectileAlign.Count > 0 ||
                                 _roundBannerPlayback != null ||
                                 _voiceTestPlayback != null ||
                                 _chatPlayback != null ||
                                 _armed ||
                                 _sequenceActive ||
                                 HasPlayoffSchedulingState();

            StopVoiceTestPlayback(reason, printSummary: false);
            CancelDtrRoundBanner(resetRound: true);
            InvalidateFreezePreroll();
            if (reason.StartsWith("map_start:", StringComparison.OrdinalIgnoreCase))
            {
                _replayMusicKitBaselines.Clear();
                _replayMusicKitRepairTokens.Clear();
            }
            else
            {
                RestoreAllReplayMusicKits(reason);
            }

            if (BotControllerNative.IsCompatible)
            {
                foreach (var slot in NativeReplaySlots())
                    ClearNativeSlotForLifecycle(slot);
                _ = BotControllerNative.ClearAllBuyPlans();
                _ = BotControllerNative.SetReplayPovMask(0);
            }
            _lastReplayPovMask = 0;
            ClearLoadedAutoVoiceClip();
            ClearVoiceClipCache();
            ClearLoadedAutoChat();

            _loadedSlots.Clear();
            _warmReplayBufferSlots.Clear();
            _demoTracerOwnedSlots.Clear();
            _loadedReplays.Clear();
            ClearReplayRetentionPriority(clearPending: true);
            ClearRetainedBotHiderPresentation();
            _lastEnsuredWeaponDef.Clear();
            _lastReplayWeaponDef.Clear();
            _lastLockedWeaponTarget.Clear();
            _pendingWeaponAlign.Clear();
            _pendingWeaponSlotReplacements.Clear();
            _projectileAlignNextBySlot.Clear();
            _replayHifiEventNextBySlot.Clear();
            _replayIdentityGenerationBySlot.Clear();
            _replayMutationGenerationBySlot.Clear();
            _pendingProjectileAlign.Clear();
            BotControllerNative.ClearProjectileBirthAlign();
            _trackedDroppedReplayItems.Clear();
            _rebuiltInventorySlots.Clear();
            _loadoutSyncedSlots.Clear();
            ResetCosmeticAlignState(resetCounters: true);
            ResetCosmeticEvidenceCache();
            ResetStickerAlignState(resetCounters: true);
            ResetCharmAlignState(resetCounters: true);
            ResetCrosshairAlignState(resetCounters: true);
            ResetViewmodelAlignState(resetCounters: true);
            ResetScoreboardAlignState(resetCounters: true);
            _loadedRoundScoreboard = null;
            _lastPlayingSlots.Clear();
            _replayStartedAt.Clear();
            _replayPerceptionBaselineSerial.Clear();
            _pendingBulletHits.Clear();
            _pendingBulletDamages.Clear();
            _pendingThreat360.Clear();
            _utilityTraceProjectiles.Clear();
            _safeC4Aligned = false;

            _armed = false;
            _armedLoop = false;
            _armedPrepared = false;
            _armedLabel = string.Empty;
            _armedManifestPath = string.Empty;
            _armedSourceRound = -1;
            StopSequenceState();

            if (hadReplayState)
                Server.PrintToConsole($"dtr: cleared replay lifecycle state reason={reason}");
        }
        finally
        {
            _lifecycleResetInProgress = false;
        }
    }

    private bool HasReplayLifecycleState(bool includeNative = false)
    {
        if (_loadedSlots.Count > 0 ||
            _demoTracerOwnedSlots.Count > 0 ||
            _loadedReplays.Count > 0 ||
            _lastPlayingSlots.Count > 0 ||
            _retainedReplayViewmodelSlots.Count > 0 ||
            _pendingProjectileAlign.Count > 0 ||
            _armed ||
            _sequenceActive ||
            HasPlayoffSchedulingState())
        {
            return true;
        }

        return includeNative && BotControllerNative.IsCompatible && HasAnyNativeActiveReplaySlot();
    }

    private static void ClearNativeSlotForLifecycle(int slot)
    {
        try
        {
            BotControllerNative.UnloadReplay(slot);
            BotControllerNative.ClearBuyPlan(slot);
            BotControllerNative.UnlockReplayControl(slot);
            BotControllerNative.UnlockWeaponSlot(slot);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: lifecycle native clear failed slot={slot}: {ex.Message}");
        }
    }

    private void StopLoadedReplaySlots(string reason)
    {
        CancelDtrRoundBanner(resetRound: false);
        StopVoiceTestPlayback(reason, printSummary: false);
        StopChatPlayback(reason);
        foreach (var slot in _loadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason);
        }
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _pendingWeaponAlign.Clear();
        _pendingWeaponSlotReplacements.Clear();
        _activeWeaponCosmetics.Clear();
        _projectileAlignNextBySlot.Clear();
        _pendingProjectileAlign.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
        _rebuiltInventorySlots.Clear();
        _cosmeticSyncedSlots.Clear();
        _cosmeticHeartbeatTokens.Clear();
        ClearReplayCrosshairPresentation();
        RestoreAllReplayBotViewmodels();
        _lastPlayingSlots.Clear();
        _replayStartedAt.Clear();
        _replayPerceptionBaselineSerial.Clear();
        _pendingBulletHits.Clear();
        _pendingBulletDamages.Clear();
        _pendingThreat360.Clear();
        _safeC4Aligned = false;
        SetReplayPovMask(0);
    }

    private void ReleaseUnusedWarmReplayBuffers()
    {
        foreach (var slot in _warmReplayBufferSlots)
            BotControllerNative.UnloadReplay(slot);
        _warmReplayBufferSlots.Clear();
    }

    private void StopAllState(string reason)
    {
        CancelReplayPrefetch();
        StopLoadedReplaySlots(reason);
        ClearReplayRetentionPriority(clearPending: true);
        ClearLoadedAutoVoiceClip();
        ClearLoadedAutoChat();
        _armed = false;
        _armedPrepared = false;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        StopSequenceState();
        ReleaseUnusedWarmReplayBuffers();
    }

    private bool StopReplayStateForRoundBoundary(string reason)
    {
        if (_loadedSlots.Count == 0 &&
            _lastPlayingSlots.Count == 0 &&
            _retainedReplayViewmodelSlots.Count == 0 &&
            !HasAnyNativeActiveReplaySlot())
            return false;

        var keepWarmBuffers = _sequenceActive || _armed;
        StopAndUnloadLoaded(
            clearArmedPlan: false,
            releaseBuffers: !keepWarmBuffers);
        return true;
    }

    private static IEnumerable<int> NativeReplaySlots()
    {
        for (var slot = 0; slot < MaxPlayerSlots; slot++)
            yield return slot;
    }

    private void StopUntrackedNativeReplaySlots(IReadOnlySet<int> trackedSlots, string reason)
    {
        foreach (var slot in NativeReplaySlots())
        {
            if (trackedSlots.Contains(slot) || _warmReplayBufferSlots.Contains(slot))
                continue;

            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing && state.Total <= 0)
                continue;

            BotControllerNative.UnloadReplay(slot);
            BotControllerNative.ClearBuyPlan(slot);
            BotControllerNative.UnlockWeaponSlot(slot);
            ClearReplayPovSlot(slot);
            Server.PrintToConsole($"dtr: stopped native replay slot={slot} reason={reason}");
        }
    }

    private void StopOneSlot(CommandInfo command, int slot, string reason)
    {
        StopVoiceTestPlayback(reason, printSummary: false);
        var ok = BotControllerNative.StopReplay(slot);
        ReleaseReplaySlot(slot, reason);
        command.ReplyToCommand(ok
            ? $"[DTR OK] stopped slot {slot}"
            : $"[DTR ERR] failed to stop slot {slot}");
    }

    private static void IssueRestartIfRequested(CommandInfo command, bool restart)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        command.ReplyToCommand("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_start.");
    }

    private static void IssueRestartIfRequested(bool restart, Action<string> reply)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        reply("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_start.");
    }

    private void MarkReplayStarted(int slot)
    {
        _retainedReplayViewmodelSlots.Remove(slot);
        _lastPlayingSlots.Add(slot);
        _replayStartedAt[slot] = Server.CurrentTime;
        _replayPerceptionBaselineSerial[slot] =
            BotControllerNative.TryGetNativePerceptionState(slot, out var perception)
                ? perception.UpdateSerial
                : 0u;
        _projectileAlignNextBySlot[slot] = 0;
        _replayHifiEventNextBySlot[slot] = 0;
    }

    private void ReleaseReplaySlot(
        int slot,
        string reason,
        ReplayReleaseKind releaseKind = ReplayReleaseKind.Immediate)
    {
        InvalidateReplayMusicKitRepair(slot);
        InvalidateReplayMutationGeneration(slot);
        _freezePrerollSlots.Remove(slot);
        _resumedFreezePrerollSlots.Remove(slot);
        var retainedViewmodel = (releaseKind is ReplayReleaseKind.Handoff or ReplayReleaseKind.Finished) &&
                                RetainReplayBotViewmodelForRound(slot);
        if (!retainedViewmodel)
            RestoreReplayBotViewmodel(slot);
        _lastPlayingSlots.Remove(slot);
        _replayStartedAt.Remove(slot);
        _replayPerceptionBaselineSerial.Remove(slot);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _pendingWeaponAlign.Remove(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        _activeWeaponCosmetics.Remove(slot);
        _projectileAlignNextBySlot.Remove(slot);
        _replayHifiEventNextBySlot.Remove(slot);
        _demoTracerOwnedSlots.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
        _pendingBulletHits.Remove(slot);
        _pendingBulletDamages.Remove(slot);
        _pendingThreat360.Remove(slot);
        _cosmeticSyncedSlots.Remove(slot);
        _cosmeticHeartbeatTokens.Remove(slot);
        BotControllerNative.ClearBuyPlan(slot);
        BotControllerNative.UnlockReplayControl(slot);
        BotControllerNative.UnlockWeaponSlot(slot);
        KillTrackedReplayDropsForSlot(slot, reason);
        ClearReplayPovSlot(slot);
        ScheduleCachedCosmeticRepairForSlot(slot);
        if (releaseKind == ReplayReleaseKind.Handoff &&
            IsReplaySlotStillSafe(slot) &&
            HasLivePawn(Utilities.GetPlayerFromSlot(slot)) &&
            !BotControllerNative.RequestEquipBestWeapon(slot))
        {
            Server.PrintToConsole(
                $"dtr: handoff best-weapon request unavailable slot={slot}");
        }
        Server.PrintToConsole(
            $"dtr: released slot={slot} reason={reason} viewmodel={(retainedViewmodel ? "retained_round" : "released")}");
    }

    private bool HasActiveReplaySlots()
    {
        foreach (var slot in _loadedSlots)
        {
            if (BotControllerNative.GetReplayState(slot).Playing)
                return true;
        }
        return false;
    }

    private bool HasAnyNativeActiveReplaySlot()
    {
        if (HasActiveReplaySlots())
            return true;

        foreach (var slot in NativeReplaySlots())
        {
            var state = BotControllerNative.GetReplayState(slot);
            if (state.Playing || state.Total > 0)
                return true;
        }
        return false;
    }

    private bool CheckReplayStartGates(Action<string> reply, bool stopCurrentForOverride)
    {
        if (IsWarmupPeriod())
        {
            reply("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

        if (!stopCurrentForOverride || !HasAnyNativeActiveReplaySlot())
            return true;

        reply("[DTR WARN] 会STOP当前所有DTR并override");
        StopAndUnloadLoaded();
        StopSequenceState();
        return true;
    }

    private bool IsReplaySlotBusy(int slot)
    {
        if (slot < 0)
            return false;
        if (_loadedSlots.Contains(slot) ||
            _loadedReplays.ContainsKey(slot))
        {
            return true;
        }

        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing || state.Total > 0;
    }

    private bool IsDemoTracerBot(int slot)
    {
        if (slot < 0)
            return false;

        if (_demoTracerOwnedSlots.Contains(slot))
        {
            return true;
        }

        if (_armed || _armedPrepared || _sequenceActive)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true } && IsReplayTargetBot(player))
                return true;
        }

        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing;
    }

    private bool TryGetBotCosmeticState(int slot, out DemoTracerBotCosmeticState state)
    {
        state = new DemoTracerBotCosmeticState();
        if (slot < 0)
            return false;

        state.IsDemoTracerBot = IsDemoTracerBot(slot);
        state.IsSlotBusy = IsReplaySlotBusy(slot);
        state.CosmeticWriterEnabled = AnyCosmeticFeatureEnabled();
        state.HasCosmeticEvidence =
            _loadedReplays.TryGetValue(slot, out var replay) &&
            HasCosmeticEvidence(replay.Cosmetics) &&
            IsReplaySlotStillSafe(slot);
        state.ShouldDeferInventoryWrites =
            state.IsDemoTracerBot &&
            state.HasCosmeticEvidence &&
            state.CosmeticWriterEnabled;
        return state.IsDemoTracerBot || state.IsSlotBusy || state.HasCosmeticEvidence;
    }

    private void RememberLoadedSlot(int slot)
    {
        if (!_loadedSlots.Contains(slot))
            _loadedSlots.Add(slot);
        _demoTracerOwnedSlots.Add(slot);
    }

    private long BeginReplayIdentityGeneration(int slot)
    {
        var generation = ++_nextReplayIdentityGeneration;
        _replayIdentityGenerationBySlot[slot] = generation;
        return generation;
    }

    private long CurrentReplayIdentityGeneration(int slot)
    {
        if (_replayIdentityGenerationBySlot.TryGetValue(slot, out var generation))
            return generation;

        return BeginReplayIdentityGeneration(slot);
    }

    private bool IsReplayIdentityGenerationCurrent(int slot, long generation)
        => _replayIdentityGenerationBySlot.TryGetValue(slot, out var current) &&
           current == generation;

    private void InvalidateReplayIdentityGeneration(int slot)
        => _replayIdentityGenerationBySlot.Remove(slot);

    private long CurrentReplayMutationGeneration(int slot)
    {
        if (_replayMutationGenerationBySlot.TryGetValue(slot, out var generation))
            return generation;

        generation = ++_nextReplayMutationGeneration;
        _replayMutationGenerationBySlot[slot] = generation;
        return generation;
    }

    private bool IsReplayMutationGenerationCurrent(int slot, long generation)
        => _replayMutationGenerationBySlot.TryGetValue(slot, out var current) &&
           current == generation;

    private void InvalidateReplayMutationGeneration(int slot)
    {
        _replayMutationGenerationBySlot.Remove(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
    }

    private void ForgetLoadedReplayMetadata(int slot)
    {
        InvalidateInitialSpawnAssignment();
        RestoreReplayMusicKitForSlot(slot, "forget_replay");
        InvalidateReplayIdentityGeneration(slot);
        InvalidateReplayMutationGeneration(slot);
        _loadedReplays.Remove(slot);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _pendingWeaponAlign.Remove(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        _replayHifiEventNextBySlot.Remove(slot);
        _rebuiltInventorySlots.Remove(slot);
        InvalidateReplayMusicKitRepair(slot);
        _cosmeticSyncedSlots.Remove(slot);
        _cosmeticHeartbeatTokens.Remove(slot);
        _activeWeaponCosmetics.Remove(slot);
        _appliedGloveCosmetics.Remove(slot);
        _gloveCosmeticTokens.Remove(slot);
        _pendingKnifeEntityRefreshes.Remove(slot);
        _slotCosmeticEvidenceKeys.Remove(slot);
        _scoreboardSyncedSlots.Remove(slot);
        KillTrackedReplayDropsForSlot(slot, "forget_replay");
        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void TrackLoadedReplay(
        int slot,
        string path,
        string playerName,
        ulong steamId = 0,
        int manifestFirstWeaponDefIndex = -1,
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices = null,
        ReplayLoadoutSnapshot? loadout = null,
        int musicKitId = 0,
        ReplayScoreboardFlair? scoreboardFlair = null,
        ReplayCosmetics? cosmetics = null,
        ReplayView? view = null,
        ReplayPlayerScoreboard? scoreboard = null,
        CsTeam? manifestTeam = null,
        ReplayFileMetadata? replayMetadata = null,
        int retentionRank = ReplayRetentionPriorityParser.MaxPlayersPerTeam)
    {
        InvalidateInitialSpawnAssignment();
        RestoreReplayBotViewmodel(slot);
        var hadPreviousGeneration = _replayIdentityGenerationBySlot.TryGetValue(
            slot,
            out var previousGeneration);
        var metadata = replayMetadata ?? ReadReplayMetadataOrEmpty(path);
        TryBuildWeaponPlan(metadata.WeaponDefIndices ?? [], out var scannedFirstDef, out var scannedPreloadDefs);
        var firstDef = NormalizeWeaponDefIndex(manifestFirstWeaponDefIndex);
        if (!IsKnownWeaponDefIndex(firstDef))
            firstDef = scannedFirstDef;

        var hasLoadout = loadout != null;
        var normalizedLoadout = NormalizeReplayLoadout(loadout ?? new ReplayLoadoutSnapshot());
        var preloadDefs = BuildReplayPreloadWeaponDefs(
            manifestPreloadWeaponDefIndices,
            scannedPreloadDefs,
            normalizedLoadout,
            hasLoadout);
        var hifiEvents = (metadata.HighFidelity?.Events ?? [])
            .OrderBy(replayEvent => replayEvent.TickIndex)
            .ThenBy(replayEvent => replayEvent.Tick)
            .ToArray();
        var inventorySnapshots = (metadata.HighFidelity?.InventorySnapshots ?? [])
            .OrderBy(snapshot => snapshot.TickIndex)
            .ThenBy(snapshot => snapshot.Tick)
            .ToArray();
        var normalizedCosmetics = NormalizeReplayCosmetics(cosmetics);
        var normalizedView = NormalizeReplayView(view);
        var normalizedScoreboard = NormalizeReplayScoreboard(scoreboard);
        var normalizedMusicKitId = NormalizeMusicKitId(musicKitId);
        _loadedReplays[slot] = new LoadedReplay(
            path,
            playerName,
            steamId,
            manifestTeam,
            firstDef,
            preloadDefs,
            hasLoadout,
            normalizedLoadout,
            normalizedMusicKitId,
            NormalizeReplayScoreboardFlair(scoreboardFlair),
            normalizedCosmetics,
            normalizedView,
            normalizedScoreboard,
            metadata.Projectiles ?? [],
            hifiEvents,
            inventorySnapshots,
            metadata.TickCount,
            metadata.TickRate,
            metadata.PlayStartTickIndex,
            metadata.RoundStartOrigin,
            retentionRank);
        _pendingKnifeEntityRefreshes.Remove(slot);
        RememberReplayCosmeticEvidence(slot, _loadedReplays[slot]);
        InvalidateReplayMusicKitRepair(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        InvalidateReplayMutationGeneration(slot);
        var generation = BeginReplayIdentityGeneration(slot);
        if (_replayMusicKitBaselines.TryGetValue(slot, out var musicKitBaseline))
        {
            if (hadPreviousGeneration && musicKitBaseline.Generation == previousGeneration)
                _replayMusicKitBaselines[slot] = musicKitBaseline with { Generation = generation };
            else
                _replayMusicKitBaselines.Remove(slot);
        }
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _activeWeaponCosmetics.Remove(slot);
        _projectileAlignNextBySlot[slot] = 0;
        _replayHifiEventNextBySlot[slot] = 0;
        _rebuiltInventorySlots.Remove(slot);
        _loadoutSyncedSlots.Remove(slot);
        _cosmeticSyncedSlots.Remove(slot);
        _cosmeticHeartbeatTokens.Remove(slot);
        _scoreboardSyncedSlots.Remove(slot);
        _safeC4Aligned = false;
        if (normalizedMusicKitId <= 0)
            RestoreReplayMusicKitForSlot(slot, "manifest_without_music_kit");
    }

    private static ReplayFileMetadata ReadReplayMetadataOrEmpty(string path)
        => BotControllerNative.TryReadReplayMetadata(path, out var metadata)
            ? metadata
            : ReplayFileMetadata.Empty;

    private void ApplyReplayLoadoutForSlot(int slot, LoadedReplay replay)
    {
        if (!_weaponAlignEnabled || !replay.HasLoadout || _loadoutSyncedSlots.Contains(slot))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            pawn.WeaponServices == null ||
            player.UserId is not int playerUserId)
            return;
        var replayMutationGeneration = CurrentReplayMutationGeneration(slot);

        ApplyReplayArmorAndKit(player, pawn, replay.Loadout);

        var targetItems = BuildLoadoutItemCounts(replay.Loadout);
        var deferredWeaponSync = false;
        deferredWeaponSync |= SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Primary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Primary,
            playerUserId,
            replayMutationGeneration);
        deferredWeaponSync |= SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Secondary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Secondary,
            playerUserId,
            replayMutationGeneration);
        GiveMissingLoadoutItems(
            player,
            targetItems,
            itemName => GetReplayWeaponSlot(itemName) is not ReplayWeaponSlot.Primary
                and not ReplayWeaponSlot.Secondary
                and not ReplayWeaponSlot.Knife
                and not ReplayWeaponSlot.C4);

        if (deferredWeaponSync)
        {
            Server.NextFrame(() => Server.NextFrame(() =>
                ApplyReplayWeaponPresetIfCurrent(slot, playerUserId, replayMutationGeneration)));
        }
        else
        {
            ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(replay), true, true);
        }

        if (!_pendingWeaponSlotReplacements.Keys.Any(key => key.PlayerSlot == slot))
            _loadoutSyncedSlots.Add(slot);
    }

    private void ApplyReplayWeaponPresetIfCurrent(
        int slot,
        int playerUserId,
        long replayMutationGeneration)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (!IsReplayMutationGenerationCurrent(slot, replayMutationGeneration) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != playerUserId ||
            !_loadedReplays.TryGetValue(slot, out var currentReplay))
        {
            return;
        }

        ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(currentReplay), true, true);
    }

    private static void ApplyReplayArmorAndKit(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayLoadoutSnapshot loadout)
    {
        if (!ManagedSchemaWritesAllowed())
            return;

        pawn.ArmorValue = (int)loadout.ArmorValue;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");

        if (pawn.ItemServices == null || pawn.ItemServices.Handle == IntPtr.Zero)
            return;

        var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
        itemServices.HasHelmet = loadout.HasHelmet;
        itemServices.HasDefuser = player.Team == CsTeam.CounterTerrorist && loadout.HasDefuser;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_pItemServices");
    }

    private static bool ResetReplayPawnRoundStartHealth(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        if (!ManagedSchemaWritesAllowed())
            return true;

        var pawn = player.PlayerPawn.Value;
        pawn.Health = ReplayStartHealth;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        return true;
    }

    private bool SyncTargetWeaponSlot(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        ReplayWeaponSlot slot,
        Func<string, bool> predicate,
        int playerUserId,
        long replayMutationGeneration)
    {
        var targetItem = BestTargetSlotItem(targetItems, predicate);
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            if (targetItem != null)
                TryGiveNamedItem(player, targetItem);
            return false;
        }

        if (targetItem != null && HasReplayWeapon(pawn, targetItem))
            return false;

        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, slot).ToList();
        if (targetItem == null)
        {
            var extraWeapon = currentSlotWeapons.FirstOrDefault();
            return extraWeapon != null &&
                   DropAndKillReplayWeapon(player, pawn, extraWeapon, "extra_loadout_slot");
        }

        if (currentSlotWeapons.Count == 0)
        {
            TryGiveNamedItem(player, targetItem);
            return false;
        }

        var fallbackItem = currentSlotWeapons
            .Select(weapon => NormalizeWeaponClassName(weapon.DesignerName))
            .FirstOrDefault(itemName => !WeaponClassMatches(itemName, targetItem));
        var weaponToDrop = currentSlotWeapons
            .FirstOrDefault(weapon => !WeaponClassMatches(
                NormalizeWeaponClassName(weapon.DesignerName),
                targetItem));
        if (fallbackItem == null || weaponToDrop == null)
            return false;
        if (player.UserId != playerUserId ||
            !IsReplayMutationGenerationCurrent(player.Slot, replayMutationGeneration))
            return false;

        return BeginWeaponSlotReplacement(
            player,
            pawn,
            weaponToDrop,
            targetItem,
            fallbackItem,
            slot,
            playerUserId,
            replayMutationGeneration,
            "replace_loadout_slot");
    }

    private bool BeginWeaponSlotReplacement(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weaponToDrop,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot weaponSlot,
        int playerUserId,
        long replayMutationGeneration,
        string reason)
    {
        var key = (player.Slot, weaponSlot);
        if (_pendingWeaponSlotReplacements.TryGetValue(key, out var existing) &&
            existing.PlayerUserId == playerUserId &&
            existing.ReplayMutationGeneration == replayMutationGeneration &&
            existing.TargetItem.Equals(targetItem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!DropAndKillReplayWeapon(player, pawn, weaponToDrop, reason))
            return false;

        var pending = new PendingWeaponSlotReplacement(
            player.Slot,
            playerUserId,
            replayMutationGeneration,
            targetItem,
            fallbackItem,
            weaponSlot);
        _pendingWeaponSlotReplacements[key] = pending;
        _lastEnsuredWeaponDef.Remove(player.Slot);
        _lastReplayWeaponDef.Remove(player.Slot);
        Server.NextFrame(() => CompleteWeaponSlotReplacement(
            pending,
            WeaponSlotReplacementClearWaitFrames));
        return true;
    }

    private void CompleteWeaponSlotReplacement(
        PendingWeaponSlotReplacement pending,
        int clearWaitFramesRemaining)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        var targetPresent = HasReplayWeapon(pawn, pending.TargetItem);
        var anySlotWeapon = GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any();
        switch (DecideWeaponSlotReplacement(
                    targetPresent,
                    anySlotWeapon,
                    clearWaitFramesRemaining))
        {
            case WeaponSlotReplacementAction.TargetReady:
                FinishWeaponSlotReplacement(pending, success: true, "target_ready");
                return;

            case WeaponSlotReplacementAction.WaitForClear:
                Server.NextFrame(() => CompleteWeaponSlotReplacement(
                    pending,
                    clearWaitFramesRemaining - 1));
                return;

            case WeaponSlotReplacementAction.PreserveExisting:
                Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                    pending,
                    WeaponSlotReplacementFallbackWaitFrames,
                    fallbackGrantIssued: false,
                    "slot_clear_timeout"));
                return;

            case WeaponSlotReplacementAction.GrantTarget:
                var targetGrantIssued = TryGiveNamedItem(player, pending.TargetItem);
                Server.NextFrame(() => VerifyTargetWeaponReplacement(
                    pending,
                    WeaponSlotReplacementGrantWaitFrames,
                    targetGrantIssued));
                return;
        }
    }

    private void VerifyTargetWeaponReplacement(
        PendingWeaponSlotReplacement pending,
        int grantWaitFramesRemaining,
        bool targetGrantIssued)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        if (HasReplayWeapon(pawn, pending.TargetItem))
        {
            FinishWeaponSlotReplacement(pending, success: true, "target_granted");
            return;
        }

        if (GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any())
        {
            FinishWeaponSlotReplacement(pending, success: false, "target_grant_conflict");
            return;
        }

        if (grantWaitFramesRemaining > 0)
        {
            if (!targetGrantIssued)
                targetGrantIssued = TryGiveNamedItem(player, pending.TargetItem);
            Server.NextFrame(() => VerifyTargetWeaponReplacement(
                pending,
                grantWaitFramesRemaining - 1,
                targetGrantIssued));
            return;
        }

        var fallbackGrantIssued = TryGiveNamedItem(player, pending.FallbackItem);
        Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
            pending,
            WeaponSlotReplacementFallbackWaitFrames,
            fallbackGrantIssued,
            "target_grant_timeout"));
    }

    private void VerifyFallbackWeaponIfNeeded(
        PendingWeaponSlotReplacement pending,
        int fallbackWaitFramesRemaining,
        bool fallbackGrantIssued,
        string failureReason)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        if (HasReplayWeapon(pawn, pending.TargetItem))
        {
            FinishWeaponSlotReplacement(pending, success: true, "target_granted_late");
            return;
        }

        if (GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any())
        {
            if (fallbackWaitFramesRemaining > 0)
            {
                Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                    pending,
                    fallbackWaitFramesRemaining - 1,
                    fallbackGrantIssued,
                    failureReason));
                return;
            }

            FinishWeaponSlotReplacement(pending, success: false, $"{failureReason}_weapon_preserved");
            return;
        }

        if (!fallbackGrantIssued)
            fallbackGrantIssued = TryGiveNamedItem(player, pending.FallbackItem);
        if (fallbackWaitFramesRemaining > 0)
        {
            Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                pending,
                fallbackWaitFramesRemaining - 1,
                fallbackGrantIssued,
                failureReason));
            return;
        }

        FinishWeaponSlotReplacement(pending, success: false, $"{failureReason}_fallback_failed");
    }

    private bool TryGetPendingWeaponSlotReplacementPawn(
        PendingWeaponSlotReplacement pending,
        out CCSPlayerController player,
        out CCSPlayerPawn pawn)
    {
        var key = (pending.PlayerSlot, pending.WeaponSlot);
        var currentPlayer = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        var currentPawn = currentPlayer?.PlayerPawn.Value;
        if (!_pendingWeaponSlotReplacements.TryGetValue(key, out var current) ||
            current != pending ||
            !IsReplayMutationGenerationCurrent(
                pending.PlayerSlot,
                pending.ReplayMutationGeneration) ||
            currentPlayer is not { IsValid: true, PawnIsAlive: true } ||
            currentPlayer.UserId != pending.PlayerUserId ||
            currentPawn is not { IsValid: true } ||
            currentPawn.WeaponServices == null)
        {
            if (_pendingWeaponSlotReplacements.TryGetValue(key, out current) && current == pending)
                _pendingWeaponSlotReplacements.Remove(key);
            player = null!;
            pawn = null!;
            return false;
        }

        player = currentPlayer;
        pawn = currentPawn;
        return true;
    }

    private void FinishWeaponSlotReplacement(
        PendingWeaponSlotReplacement pending,
        bool success,
        string reason)
    {
        var key = (pending.PlayerSlot, pending.WeaponSlot);
        if (_pendingWeaponSlotReplacements.TryGetValue(key, out var current) && current == pending)
            _pendingWeaponSlotReplacements.Remove(key);

        _lastEnsuredWeaponDef.Remove(pending.PlayerSlot);
        _lastReplayWeaponDef.Remove(pending.PlayerSlot);
        if (success)
        {
            Server.PrintToConsole(
                $"dtr: replaced slot={pending.PlayerSlot} item={pending.TargetItem} reason={reason}");
            if (!_pendingWeaponSlotReplacements.Keys.Any(
                    key => key.PlayerSlot == pending.PlayerSlot))
            {
                Server.NextFrame(() => FinalizeReplayLoadoutSyncIfCurrent(pending));
            }
            return;
        }

        _rebuiltInventorySlots.Remove(pending.PlayerSlot);
        _loadoutSyncedSlots.Remove(pending.PlayerSlot);
        Server.PrintToConsole(
            $"[DTR WARN] weapon slot replacement incomplete slot={pending.PlayerSlot} " +
            $"target={pending.TargetItem} fallback={pending.FallbackItem} reason={reason}");
    }

    private void FinalizeReplayLoadoutSyncIfCurrent(PendingWeaponSlotReplacement pending)
    {
        var player = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        if (!IsReplayMutationGenerationCurrent(
                pending.PlayerSlot,
                pending.ReplayMutationGeneration) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != pending.PlayerUserId ||
            _pendingWeaponSlotReplacements.Keys.Any(
                key => key.PlayerSlot == pending.PlayerSlot) ||
            !_loadedReplays.TryGetValue(pending.PlayerSlot, out var replay))
        {
            return;
        }

        _loadoutSyncedSlots.Remove(pending.PlayerSlot);
        ApplyReplayLoadoutForSlot(pending.PlayerSlot, replay);
    }

    private void ClearPendingWeaponSlotReplacementsForSlot(int slot)
    {
        foreach (var key in _pendingWeaponSlotReplacements.Keys
                     .Where(key => key.PlayerSlot == slot)
                     .ToArray())
        {
            _pendingWeaponSlotReplacements.Remove(key);
        }
    }

    private static void GiveMissingLoadoutItems(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        var currentItems = CountCurrentLoadoutItems(player);
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            var missingCount = Math.Max(0, targetCount - currentItems.GetValueOrDefault(itemName));
            for (var i = 0; i < missingCount; i++)
                TryGiveNamedItem(player, itemName);
        }
    }

    private static Dictionary<string, int> CountCurrentLoadoutItems(CCSPlayerController player)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
            return counts;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            var itemName = NormalizeWeaponClassName(weapon.DesignerName);
            if (GetReplayWeaponSlot(itemName) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Other)
                continue;
            counts[itemName] = counts.GetValueOrDefault(itemName) + 1;
        }
        return counts;
    }

    private static bool DropAndKillReplayWeapon(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string reason)
    {
        var weaponName = weapon.DesignerName;
        var weaponEntityHandle = weapon.EntityHandle.Raw;
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex)
            return false;
        if (!TrySelectWeapon(player, pawn, weapon))
            return false;

        try
        {
            player.DropActiveWeapon();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to drop slot={player.Slot} item={weaponName}: {ex.Message}");
            return false;
        }

        ScheduleDroppedWeaponKill(player.Slot, weaponEntityHandle, weaponName, reason);
        return true;
    }

    private static void KillDroppedWeapon(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        string reason)
    {
        try
        {
            var weapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
            if (weapon is not { IsValid: true } ||
                weapon.EntityHandle.Raw != weaponEntityHandle ||
                !WeaponClassMatches(weapon.DesignerName, weaponName))
            {
                return;
            }

            var currentlyOwned = Utilities.GetPlayers().Any(candidate =>
            {
                if (candidate is not { IsValid: true } ||
                    candidate.PlayerPawn is not { IsValid: true } pawnHandle)
                    return false;
                var candidatePawn = pawnHandle.Value;
                return candidatePawn is { IsValid: true } &&
                       PawnOwnsWeapon(candidatePawn, weapon);
            });
            if (!currentlyOwned)
                weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to kill dropped weapon slot={slot} item={weaponName} reason={reason}: {ex.Message}");
        }
    }

    private static void ScheduleDroppedWeaponKill(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        string reason)
    {
        Server.NextFrame(() => Server.NextFrame(() =>
            KillDroppedWeapon(slot, weaponEntityHandle, weaponName, reason)));
    }

    private static bool TryGiveNamedItem(CCSPlayerController player, string itemName)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
            return false;

        try
        {
            return player.GiveNamedItem(itemName) != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to give slot={player.Slot} item={itemName}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySelectWeapon(CCSPlayerController player, CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        var defIndex = WeaponDefIndex(weapon.DesignerName);
        if (player.Slot >= 0 && defIndex >= 0)
            BotControllerNative.SwitchBotWeapon(player.Slot, defIndex);

        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null)
            return false;

        if (ManagedSchemaWritesAllowed())
        {
            weaponServices.ActiveWeapon.Raw = weapon.EntityHandle.Raw;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");
        }

        if (player.UserId != null)
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {weapon.DesignerName}");

        return true;
    }

    private static IEnumerable<CBasePlayerWeapon> GetWeaponsInReplaySlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            if (GetReplayWeaponSlot(NormalizeWeaponClassName(weapon.DesignerName)) == slot)
                yield return weapon;
        }
    }

    private void PreloadReplayWeaponsForSlot(int slot, LoadedReplay replay)
    {
        if (!_rebuiltInventorySlots.Contains(slot))
        {
            var rebuilt = true;
            foreach (var def in replay.PreloadWeaponDefIndices)
                rebuilt &= EnsureReplayWeaponForSlot(
                    slot,
                    def,
                    forceSwitch: false,
                    allowGive: true,
                    replaceConflictingSlot: false);
            if (rebuilt)
                _rebuiltInventorySlots.Add(slot);
        }

        ApplyReplayWeaponPreset(
            slot,
            ChooseStartWeaponDef(replay),
            allowSlotReplacement: true,
            force: true);
    }

    private void ApplyReplayWeaponPreset(
        int slot,
        int weaponDefIndex,
        bool allowSlotReplacement,
        bool force)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsKnownWeaponDefIndex(normalized))
            return;

        if (!force &&
            _lastReplayWeaponDef.TryGetValue(slot, out var lastDef) &&
            lastDef == normalized)
            return;

        var target = GetReplayLockTarget(normalized);
        if (target <= 0)
        {
            if (_lastLockedWeaponTarget.Remove(slot))
                BotControllerNative.UnlockWeaponSlot(slot);
        }
        else if (force ||
                 !_lastLockedWeaponTarget.TryGetValue(slot, out var lastTarget) ||
                 lastTarget != target)
        {
            if (BotControllerNative.LockWeaponSlot(slot, target))
                _lastLockedWeaponTarget[slot] = target;
        }

        if (allowSlotReplacement && IsSlotReplaceableWeaponDef(normalized))
        {
            var ensured = EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: true);
            if (!ensured)
            {
                _lastReplayWeaponDef.Remove(slot);
                return;
            }
        }

        if (BotControllerNative.SwitchBotWeapon(slot, normalized))
        {
            _lastReplayWeaponDef[slot] = normalized;
            ApplyReplayWeaponCosmeticForSlot(slot, normalized);
            ScheduleActiveReplayWeaponCosmeticNextFrame(slot, normalized);
        }
        else if (!allowSlotReplacement)
        {
            _lastReplayWeaponDef[slot] = normalized;
        }
        else
        {
            _lastReplayWeaponDef.Remove(slot);
        }
    }

    private static int ChooseStartWeaponDef(LoadedReplay replay)
    {
        var first = NormalizeWeaponDefIndex(replay.FirstWeaponDefIndex);
        if (IsKnownWeaponDefIndex(first) && GetReplayLockTarget(first) != 5)
            return first;

        foreach (var def in replay.PreloadWeaponDefIndices)
        {
            var normalized = NormalizeWeaponDefIndex(def);
            if (IsKnownWeaponDefIndex(normalized))
                return normalized;
        }

        return first;
    }

    private void QueueReplayWeaponAlign(int slot, int weaponDefIndex, bool forceSwitch)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
            return;
        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last) && last == normalized)
            return;

        _pendingWeaponAlign[slot] = new PendingWeaponAlign(normalized, forceSwitch);
        if (_weaponAlignFrameQueued)
            return;

        _weaponAlignFrameQueued = true;
        Server.NextFrame(ProcessPendingWeaponAlign);
    }

    private void ProcessPendingWeaponAlign()
    {
        _weaponAlignFrameQueued = false;
        if (!_weaponAlignEnabled || _pendingWeaponAlign.Count == 0)
        {
            _pendingWeaponAlign.Clear();
            return;
        }

        var pending = _pendingWeaponAlign.ToArray();
        _pendingWeaponAlign.Clear();
        foreach (var (slot, request) in pending)
            _ = EnsureReplayWeaponForSlot(
                slot,
                request.WeaponDefIndex,
                request.ForceSwitch,
                allowGive: false,
                replaceConflictingSlot: false);
    }

    private bool EnsureReplayWeaponForSlot(
        int slot,
        int weaponDefIndex,
        bool forceSwitch,
        bool allowGive,
        bool replaceConflictingSlot)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
            return false;
        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last) && last == normalized && !forceSwitch)
            return true;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        if (allowGive &&
            replaceConflictingSlot &&
            player.UserId is int playerUserId &&
            TryGetWeaponClassByDefIndex(normalized, out var replacementClassName))
        {
            var pawn = player.PlayerPawn.Value;
            var weaponSlot = GetReplayWeaponSlot(replacementClassName);
            var conflictingWeapon = GetWeaponsInReplaySlot(pawn, weaponSlot)
                .FirstOrDefault(weapon => !WeaponClassMatches(
                    weapon.DesignerName,
                    replacementClassName));
            if (conflictingWeapon != null)
            {
                var fallbackItem = NormalizeWeaponClassName(conflictingWeapon.DesignerName);
                _ = BeginWeaponSlotReplacement(
                    player,
                    pawn,
                    conflictingWeapon,
                    replacementClassName,
                    fallbackItem,
                    weaponSlot,
                    playerUserId,
                    CurrentReplayMutationGeneration(slot),
                    "replace_replay_slot");
                return false;
            }
        }

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                out var className))
            return false;

        _lastEnsuredWeaponDef[slot] = normalized;
        ApplyReplayWeaponCosmeticForSlot(slot, normalized);
        if (forceSwitch)
        {
            if (!BotControllerNative.SwitchBotWeapon(slot, normalized))
            {
                _lastEnsuredWeaponDef.Remove(slot);
                return false;
            }
            ScheduleActiveReplayWeaponCosmeticNextFrame(slot, normalized);
        }

        Server.PrintToConsole($"dtr: aligned slot={slot} def={normalized} item={className}");
        return true;
    }

    private static bool TryEnsureReplayWeapon(
        CCSPlayerController player,
        int weaponDefIndex,
        bool allowGive,
        out string className)
    {
        className = string.Empty;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out className))
            return false;

        var pawn = player.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true })
            return false;

        if (HasReplayWeapon(pawn, className))
            return true;

        var slot = GetReplayWeaponSlot(className);
        if (!allowGive)
            return false;
        if (slot is ReplayWeaponSlot.Other or ReplayWeaponSlot.Knife or
            ReplayWeaponSlot.C4 or ReplayWeaponSlot.Taser)
            return false;

        if (HasConflictingWeaponInSlot(pawn, slot, className))
            return false;

        if (HasReplayWeapon(pawn, className))
            return true;

        if (!TryGiveNamedItem(player, className))
            return false;

        return HasReplayWeapon(pawn, className) || slot == ReplayWeaponSlot.Utility;
    }

    private static bool HasReplayWeapon(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            return false;

        var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
        if (activeWeapon != null &&
            activeWeapon.IsValid &&
            WeaponClassMatches(activeWeapon.DesignerName, className))
        {
            return true;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
                return true;
        }
        return false;
    }

    private static bool HasConflictingWeaponInSlot(
        CCSPlayerPawn pawn,
        ReplayWeaponSlot slot,
        string expectedClassName)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary))
            return false;
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, expectedClassName))
                continue;
            if (GetReplayWeaponSlot(weapon.DesignerName) == slot)
                return true;
        }

        return false;
    }

    internal readonly record struct FreezePrerollTiming(
        float DelaySeconds,
        float PlaybackSeconds);

    private enum ReplayStartAnchor
    {
        Live,
        FreezePreroll,
    }

    private enum ReplayIdentityMode
    {
        Off,
        Name,
        Steam,
        Avatar,
    }

    private sealed class TickPlayerSnapshot
    {
        private readonly CCSPlayerController?[] _bySlot = new CCSPlayerController?[MaxPlayerSlots];

        public TickPlayerSnapshot(
            IReadOnlyList<CCSPlayerController> controllers,
            IReadOnlyList<CCSPlayerController> teamPlayers)
        {
            Controllers = controllers;
            TeamPlayers = teamPlayers;

            foreach (var controller in controllers)
            {
                if (controller is not { IsValid: true } || controller.Slot is < 0 or >= MaxPlayerSlots)
                    continue;
                _bySlot[controller.Slot] ??= controller;
            }
        }

        public IReadOnlyList<CCSPlayerController> Controllers { get; }
        public IReadOnlyList<CCSPlayerController> TeamPlayers { get; }

        public bool TryGetSlot(int slot, out CCSPlayerController player)
        {
            if (slot is >= 0 and < MaxPlayerSlots && _bySlot[slot] is { } value)
            {
                player = value;
                return true;
            }

            player = null!;
            return false;
        }
    }

    private readonly record struct LoadRoundResult(bool Ok, string Message)
    {
        public static LoadRoundResult Success(string message) => new(true, message);
        public static LoadRoundResult Fail(string message) => new(false, message);
    }

    private readonly record struct LoadedReplay(
        string Path,
        string PlayerName,
        ulong SteamId,
        CsTeam? ManifestTeam,
        int FirstWeaponDefIndex,
        int[] PreloadWeaponDefIndices,
        bool HasLoadout,
        ReplayLoadoutSnapshot Loadout,
        int MusicKitId,
        ReplayScoreboardFlair? ScoreboardFlair,
        ReplayCosmetics Cosmetics,
        ReplayView View,
        ReplayPlayerScoreboard Scoreboard,
        ReplayProjectileEvent[] Projectiles,
        ReplayHifiEvent[] HifiEvents,
        ReplayInventorySnapshot[] InventorySnapshots,
        int TickCount,
        float TickRate,
        uint PlayStartTickIndex,
        ReplayVector3? RoundStartOrigin,
        int RetentionRank);

    private readonly record struct ReplayMusicKitBaseline(
        long Generation,
        int UserId,
        ushort InventoryMusicKitId,
        int ControllerMusicKitId,
        int ControllerMusicKitMvps,
        bool MvpNoMusic);

    private readonly record struct ReplayAssignment(
        ManifestFile File,
        CCSPlayerController Bot,
        int RetentionRank);

    private readonly record struct DtrKickCandidate(
        int Slot,
        int? UserId,
        CsTeam Team,
        string LiveName,
        string LoadedName,
        ulong SteamId,
        int RetentionRank);

    private readonly record struct PendingWeaponAlign(int WeaponDefIndex, bool ForceSwitch);

    private readonly record struct PendingWeaponSlotReplacement(
        int PlayerSlot,
        int PlayerUserId,
        long ReplayMutationGeneration,
        string TargetItem,
        string FallbackItem,
        ReplayWeaponSlot WeaponSlot);

    private readonly record struct AppliedActiveWeaponCosmetic(int WeaponDefIndex, nint WeaponHandle);

    private readonly record struct PendingBulletHit(int AttackerSlot, float Time);

    private readonly record struct PendingBulletDamage(int AttackerSlot, int Damage, float Time);

    private readonly record struct PendingThreat360(int EnemySlot, float FirstSeenAt);

    private readonly record struct TrackedDroppedReplayItem(int SourceSlot, int WeaponDefIndex, IntPtr Handle);

    private enum ProjectileAlignDecision
    {
        Apply,
        Retry,
        Skip
    }

    private enum MolotovPointAlignMode
    {
        Off,
        Teleport,
        Detonate
    }

    private sealed class PendingProjectileAlign(
        uint index,
        IntPtr handle,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        public uint Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public ReplayProjectileKind Kind { get; } = kind;
        public int WeaponDefIndex { get; } = weaponDefIndex;
        public ReplayProjectileEvent Align { get; set; }
        public int Slot { get; set; } = -1;
        public int EventIndex { get; set; } = -1;
        public int MatchAttemptsRemaining { get; set; }
        public int WritesRemaining { get; set; }
        public int TotalWritesTarget { get; set; } = ProjectileAlignDefaultTotalWrites;
        public int WritesApplied { get; set; }
        public int LastNativeBirthRc { get; set; } = -99;
        public float FirstWriteTime { get; set; }
        public float LastWriteTime { get; set; }
        public bool Matched { get; set; }
        public bool MolotovPointAlignArmed { get; set; }
        public bool MolotovPointAlignApplied { get; set; }
        public MolotovPointAlignMode MolotovPointAlignMode { get; set; } = MolotovPointAlignMode.Off;
        public int MolotovPointAlignTargetTickIndex { get; set; } = -1;
    }

    private readonly record struct TraceVector(float? X, float? Y, float? Z)
    {
        public static TraceVector Empty => new(null, null, null);
    }

    private sealed class UtilityProjectileTrace(uint index, IntPtr handle, string designerName)
    {
        private bool _hasLastPosition;
        private TraceVector _lastPosition = TraceVector.Empty;
        private float _lastTime;

        public uint Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public string DesignerName { get; } = designerName;

        public TraceVector EstimateVelocity(TraceVector position, float time)
        {
            if (!_hasLastPosition ||
                !position.X.HasValue ||
                !position.Y.HasValue ||
                !position.Z.HasValue ||
                !_lastPosition.X.HasValue ||
                !_lastPosition.Y.HasValue ||
                !_lastPosition.Z.HasValue)
            {
                return TraceVector.Empty;
            }

            var dt = time - _lastTime;
            if (dt <= 0.0f)
                return TraceVector.Empty;

            return new TraceVector(
                (position.X.Value - _lastPosition.X.Value) / dt,
                (position.Y.Value - _lastPosition.Y.Value) / dt,
                (position.Z.Value - _lastPosition.Z.Value) / dt);
        }

        public void Update(TraceVector position, float time)
        {
            _lastPosition = position;
            _lastTime = time;
            _hasLastPosition = position.X.HasValue && position.Y.HasValue && position.Z.HasValue;
        }
    }

    private enum ReplayWeaponSlot
    {
        Other,
        Primary,
        Secondary,
        Utility,
        C4,
        Taser,
        Knife
    }

    private enum HandoffMode
    {
        Off,
        Death,
        Contact,
        DeathOrContact,
        DeathContactC4
    }

    private enum ReplayReleaseKind
    {
        Immediate,
        Handoff,
        Finished
    }

    private static string EscapeConsoleString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool ParseOnOff(string value, bool fallback)
        => value.ToLowerInvariant() switch
        {
            "1" or "on" or "true" or "yes" or "full" or "name" => true,
            "0" or "off" or "false" or "no" => false,
            _ => fallback,
        };

    private static string FormatOnOff(bool value)
        => value ? "on" : "off";

    private static bool TryParseProjectileAlignTicks(string value, out int ticks)
    {
        var normalized = value.Trim().ToLowerInvariant();
        ticks = normalized switch
        {
            "status" => int.MinValue,
            "default" or "normal" => ProjectileAlignDefaultTotalWrites,
            "once" or "single" => 1,
            "until_delete" or "until-delete" or "per_tick" or "per-tick" or "tick" or "every_tick" or "every-tick" => ProjectileAlignUntilDelete,
            _ => 0
        };
        if (ticks != 0)
            return true;

        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            return false;
        if (ticks < 1 || ticks > ProjectileAlignMaxTotalWrites)
            return false;
        return true;
    }

    private string FormatProjectileAlignTicks()
        => _projectileAlignTotalWrites == ProjectileAlignUntilDelete
            ? "until_delete"
            : _projectileAlignTotalWrites.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseMolotovPointAlignMode(string value, out MolotovPointAlignMode? mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "status" => null,
            "off" or "0" or "false" or "none" => MolotovPointAlignMode.Off,
            "teleport" or "tp" => MolotovPointAlignMode.Teleport,
            "detonate" or "effect" or "point" or "1" or "true" => MolotovPointAlignMode.Detonate,
            _ => null
        };

        return mode.HasValue || value.Equals("status", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMolotovPointAlignMode(MolotovPointAlignMode mode)
        => mode switch
        {
            MolotovPointAlignMode.Teleport => "teleport",
            MolotovPointAlignMode.Detonate => "detonate",
            _ => "off"
        };

    private static string FormatPendingMolotovPointAlign(PendingProjectileAlign pending)
        => pending.MolotovPointAlignArmed
            ? $"{FormatMolotovPointAlignMode(pending.MolotovPointAlignMode)}:{pending.MolotovPointAlignTargetTickIndex}"
            : "off";

    private static string CurrentMapName()
    {
        try
        {
            return Server.MapName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool CheckManifestMap(CommandInfo command, string manifestMap, string manifestPath)
    {
        if (CurrentMapMatchesManifest(manifestMap, out var currentMap))
            return true;

        command.ReplyToCommand(
            $"[DTR ERR] map mismatch: server=\"{currentMap}\" manifest=\"{manifestMap}\" path=\"{manifestPath}\"");
        return false;
    }

    private static bool CurrentMapMatchesManifest(string manifestMap, out string currentMap)
    {
        currentMap = CurrentMapName();
        if (string.IsNullOrWhiteSpace(manifestMap) ||
            string.IsNullOrWhiteSpace(currentMap) ||
            currentMap.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NormalizeMapName(currentMap).Equals(NormalizeMapName(manifestMap), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapName(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("de_", StringComparison.Ordinal)
            ? normalized[3..]
            : normalized;
    }

    private static string FormatRoundList(IReadOnlyList<int> rounds)
    {
        if (rounds.Count == 0)
            return "none";
        if (rounds.Count <= 16)
            return string.Join(",", rounds);
        return $"{string.Join(",", rounds.Take(16))},... ({rounds.Count})";
    }

    private static bool CheckAbi(CommandInfo command)
    {
        if (BotControllerNative.IsCompatible)
            return true;

        command.ReplyToCommand(
            $"dtr: ABI mismatch; {BotControllerNative.RuntimeSummary}");
        return false;
    }

    private static bool TryParseRoundArgs(
        CommandInfo command,
        string commandName,
        out string manifestPath,
        out int round,
        int argOffset = 1)
    {
        manifestPath = string.Empty;
        round = 0;
        if (command.ArgCount <= argOffset + 1)
        {
            command.ReplyToCommand($"usage: {commandName} <manifest.json> <source_round>");
            return false;
        }

        manifestPath = command.GetArg(argOffset);
        if (int.TryParse(command.GetArg(argOffset + 1), out round) && round >= 0)
            return true;

        command.ReplyToCommand("dtr: source_round must be a non-negative integer");
        return false;
    }

    private static bool TryParseSlot(CommandInfo command, out int slot)
        => TryParseSlotAt(command, 1, out slot);

    private static bool TryParseSlotAt(CommandInfo command, int argIndex, out int slot)
    {
        slot = 0;
        if (command.ArgCount > argIndex &&
            int.TryParse(command.GetArg(argIndex), out slot) &&
            slot is >= 0 and < MaxPlayerSlots)
            return true;

        command.ReplyToCommand($"dtr: slot must be an integer from 0 to {MaxPlayerSlots - 1}");
        return false;
    }

    private static bool TryParseHandoffMode(string value, out HandoffMode mode)
    {
        mode = value.ToLowerInvariant() switch
        {
            "0" or "off" or "none" => HandoffMode.Off,
            "death" or "kill" => HandoffMode.Death,
            "contact" or "see" or "sight" => HandoffMode.Contact,
            "death_or_contact" or "contact_or_death" => HandoffMode.DeathOrContact,
            "1" or "auto" or "default" or
            "death_contact_c4" or "death_contact_c4planted" or "death_contact_c4_planted" or
            "death_or_contact_or_c4" or "death_or_contact_or_bomb" or "death_contact_bomb" => HandoffMode.DeathContactC4,
            _ => HandoffMode.Off
        };
        return value.ToLowerInvariant() is "0" or "off" or "none" or
            "death" or "kill" or
            "contact" or "see" or "sight" or
            "death_or_contact" or "contact_or_death" or
            "1" or "auto" or "default" or
            "death_contact_c4" or "death_contact_c4planted" or "death_contact_c4_planted" or
            "death_or_contact_or_c4" or "death_or_contact_or_bomb" or "death_contact_bomb";
    }

    private static bool HandoffIncludesDeath(HandoffMode mode)
        => mode is HandoffMode.Death or HandoffMode.DeathOrContact or HandoffMode.DeathContactC4;

    private static bool HandoffIncludesContact(HandoffMode mode)
        => mode is HandoffMode.Contact or HandoffMode.DeathOrContact or HandoffMode.DeathContactC4;

    private static bool HandoffIncludesC4(HandoffMode mode)
        => mode is HandoffMode.DeathContactC4;

    private static string FormatHandoffMode(HandoffMode mode)
        => mode switch
        {
            HandoffMode.Off => "off",
            HandoffMode.Death => "death",
            HandoffMode.Contact => "contact",
            HandoffMode.DeathOrContact => "death_or_contact",
            HandoffMode.DeathContactC4 => "death_contact_c4",
            _ => "off"
        };

    private string ReplayIdentityModeName()
        => _replayIdentityMode switch
        {
            ReplayIdentityMode.Name => "name",
            ReplayIdentityMode.Steam => "steam",
            ReplayIdentityMode.Avatar => "avatar",
            _ => "off",
        };

}
