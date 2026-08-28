/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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
    public override string ModuleVersion => "1.2.0";
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
    private const int ProjectileAlignLogMaxEntries = 128;
    private const float ProjectileAlignMaxInitialPositionDistance = 128.0f;
    private const int MinManifestAbiVersion = 12;
    private const int MaxManifestAbiVersion = 17;
    private const int MaxPlayerSlots = BotControllerNative.MaxSlots;
    private const int ReplayStartHealth = 100;
    private const int StandardTeamSize = 5;
    private const int InitialSpawnAssignmentMaxAttempts = 8;
    private const float ReplayReadinessPollSeconds = 0.05f;
    private const int WeaponSlotReplacementClearWaitFrames = 8;
    private const int WeaponSlotReplacementGrantWaitFrames = 4;
    private const int WeaponSlotReplacementGrantRetryAttempts = 1;
    private const int WeaponSlotReplacementFallbackWaitFrames = 4;
    private const int WeaponSlotReplacementFallbackRetryAttempts = 1;
    private const int DetachedWeaponCleanupRetryFrames = 8;
    private const int ReplayLoadoutSlotRetryFrames = 8;
    private const float PlayerHullWidth = 32.0f;
    private const float PlayerHullHeight = 72.0f;
    private const string AvatarOverrideCacheDirectoryName = "avatar-cache";
    private const int AvatarOverrideMaxBytes = 16 * 1024;
    private static readonly byte[] AvatarPngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private const string FreezeTimeConVarName = "mp_freezetime";
    private const string MaxMoneyConVarName = "mp_maxmoney";
    private static readonly Lazy<string> Cs2PatchVersion = new(DetectCs2PatchVersion);
    private const string CosmeticRiskNotice = "[DTR WARN] cosmetic alignment consumes opt-in manifest cosmetics evidence and may carry Valve GSLT/server-guideline risk outside local/private replay validation.";
    private const string LeftHandDesiredFidelityNotice = "[DTR WARN] left_hand_desired=off 会降低保真度，但显著增高handoff流畅性。Reload loaded replays or plans for this setting to apply.";

    private readonly DemoTracerBotHiderBridge _botHiderBridge = new();
    private readonly RayTraceLosProbe _rayTraceLosProbe = new();
    private readonly DemoTracerApiFacade _apiFacade;
    private bool _weaponAlignEnabled = true;
    private bool _projectileAlignEnabled = true;
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
    private bool _balanceAlignEnabled;
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
    private bool _mapActive = true;
    private bool _lifecycleResetInProgress;

    public override void Load(bool hotReload)
    {
        RegisterControlCommandAuthorization();
        RegisterReplayRetentionJoinHook();
        RegisterReplayBuySuppressionHooks();
        LoadRuntimeConfig(message => Server.PrintToConsole(message), announceMissing: true);
        LoadCs2LibEconIndex();
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
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
        _ = SyncBotHiderPresentationLease(announce: _session.LoadedSlots.Count > 0);
        _botRandomizerBridge.Refresh();
        _ = _botRandomizerBridge.ReleaseOwner(BotRandomizerApi.BotRandomizerContract.DemoTracerOwner);
        _ = SyncBotRandomizerCosmeticLease(announce: _session.LoadedSlots.Count > 0);
        RefreshRuntimeHealthHeartbeat();
    }

    public override void Unload(bool hotReload)
    {
        StopRuntimeHealthHeartbeat();
        UnregisterReplayBuySuppressionHooks();
        UnregisterReplayRetentionJoinHook();
        ClearReplayStateForLifecycle(hotReload ? "plugin_reload" : "plugin_unload");
        BotControllerNative.ClearAllBuyPlans();
        _ = _botRandomizerBridge.ReleaseOwner(BotRandomizerApi.BotRandomizerContract.DemoTracerOwner);
        _botHiderBridge.Refresh();
        _botRandomizerBridge.Refresh();
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

        var disconnectsReplaySlot = HasReplayLifecycleState(includeNative: true) &&
                                    IsDisconnectingReplaySlot(playerSlot);
        ClearHumanTeamAvatarOverrideForSlot(playerSlot, "client_disconnect");
        if (disconnectsReplaySlot)
        {
            RemoveReplaySlot(
                playerSlot,
                $"client_disconnect:{playerSlot}",
                out _,
                out _);
            return;
        }

        ForgetRetainedBotHiderPresentation(playerSlot);
        ClearReplayCrosshairPresentationEntry(playerSlot);
    }

    private bool IsDisconnectingReplaySlot(int slot)
    {
        if (_session.ReplaySlots.IsLoaded(slot) ||
            _session.WarmReplayBufferSlots.Contains(slot) ||
            _session.LoadedReplays.ContainsKey(slot) ||
            _retainedReplayViewmodelSlots.Contains(slot))
        {
            return true;
        }

        if (!BotControllerNative.IsCompatible)
            return false;

        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing || state.Total > 0;
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

}
