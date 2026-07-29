using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const string RuntimeConfigFileName = "demotracer.config.json";
    private bool _runtimeConfigHadLegacyAlign;
    private bool _runtimeConfigHadNewSections;

    private static readonly JsonSerializerOptions RuntimeConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [ConsoleCommand("dtr_config_reload", "dtr_config_reload")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ConfigReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        LoadRuntimeConfig(command.ReplyToCommand, announceMissing: true);
    }

    [ConsoleCommand("dtr_config_status", "dtr_config_status")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ConfigStatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        var path = RuntimeConfigPath();
        command.ReplyToCommand(
            $"[DTR OK] config path=\"{path}\" exists={File.Exists(path)}");
        ReplyRuntimeSettings(command.ReplyToCommand, "[DTR OK] config effective");
    }

    private string RuntimeConfigPath()
    {
        var directory = Path.GetDirectoryName(GetType().Assembly.Location);
        return Path.Combine(string.IsNullOrWhiteSpace(directory) ? "." : directory, RuntimeConfigFileName);
    }

    private void LoadRuntimeConfig(Action<string> reply, bool announceMissing)
    {
        var path = RuntimeConfigPath();
        if (!File.Exists(path))
        {
            if (announceMissing)
                reply($"[DTR OK] config not found; using built-in defaults. path=\"{path}\"");
            _runtimeConfigHadLegacyAlign = false;
            _runtimeConfigHadNewSections = false;
            ResetRuntimeConfigDefaults();
            ApplyRuntimeConfigSideEffects();
            return;
        }

        DemoTracerRuntimeConfig? config;
        try
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<DemoTracerRuntimeConfig>(json, RuntimeConfigJsonOptions);
        }
        catch (Exception ex)
        {
            reply($"[DTR ERR] failed to read config path=\"{path}\": {ex.Message}");
            ApplyRuntimeConfigSideEffects();
            return;
        }

        if (config == null)
        {
            reply($"[DTR ERR] config path=\"{path}\" was empty or invalid JSON");
            ApplyRuntimeConfigSideEffects();
            return;
        }

        ApplyRuntimeConfig(config, reply);
        reply($"[DTR OK] loaded config path=\"{path}\"");
    }

    private void ApplyRuntimeConfig(DemoTracerRuntimeConfig config, Action<string> reply)
    {
        ResetRuntimeConfigDefaults();
        _runtimeConfigHadLegacyAlign = config.Align != null;
        _runtimeConfigHadNewSections = config.Fidelity != null || config.Match != null || config.Cosmetics != null;
        if (_runtimeConfigHadLegacyAlign && _runtimeConfigHadNewSections)
            reply("[DTR WARN] config contains legacy align and new fidelity/match/cosmetics sections; new sections override matching legacy fields.");

        if (!string.IsNullOrWhiteSpace(config.Identity))
        {
            if (TryParseReplayIdentityMode(config.Identity, out var identityMode))
                _replayIdentityMode = identityMode;
            else
                reply($"[DTR WARN] ignored config identity=\"{config.Identity}\"; expected off, name, steam, avatar, or full");
        }

        if (config.AllowPartial.HasValue)
            _partialReplayEnabled = config.AllowPartial.Value;
        if (config.Playoff.HasValue)
            _playoffEnabled = config.Playoff.Value;
        if (config.ChatAuto.HasValue)
            _chatAutoEnabled = config.ChatAuto.Value;
        if (config.RoundBanner.HasValue)
            _roundBannerEnabled = config.RoundBanner.Value;

        ApplyRuntimeAlignConfig(config.Align, reply);
        ApplyRuntimeFidelityConfig(config.Fidelity, reply);
        ApplyRuntimeMatchConfig(config.Match, reply);
        ApplyRuntimeCosmeticsConfig(config.Cosmetics, reply);
        ApplyRuntimeHandoffConfig(config.Handoff, reply);
        ApplyRuntimeConfigSideEffects();
    }

    private void ResetRuntimeConfigDefaults()
    {
        _replayIdentityMode = ReplayIdentityMode.Steam;
        _partialReplayEnabled = true;
        _playoffEnabled = false;
        _handoffMode = HandoffMode.DeathContactC4;
        _handoffAllSlots = false;
        _handoffThreat360Enabled = true;
        _handoffThreat360Range = HandoffThreat360DefaultRange;
        _handoffThreat360LosEnabled = true;
        _viewmodelContinuityMode = ViewmodelContinuityMode.Round;
        _chatAutoEnabled = true;
        _roundBannerEnabled = true;

        SetWeaponAlignEnabled(true);
        SetProjectileAlignEnabled(true);
        SetCrosshairAlignEnabled(true);
        _leftHandDesiredEnabled = true;
        ApplyCosmeticPreset(CosmeticPreset.Off);
        _preserveNativeBotCosmetics = false;
        SetScoreboardAlignEnabled(false);
    }

    private void ApplyRuntimeAlignConfig(DemoTracerAlignConfig? align, Action<string> reply)
    {
        if (align == null)
            return;

        if (align.Weapons.HasValue)
            SetWeaponAlignEnabled(align.Weapons.Value);
        if (align.Projectiles.HasValue)
            SetProjectileAlignEnabled(align.Projectiles.Value);
        if (align.Cosmetics.HasValue)
            SetCosmeticAlignEnabled(align.Cosmetics.Value);
        if (align.Stickers.HasValue)
            SetStickerAlignEnabled(align.Stickers.Value);
        if (align.Charms.HasValue)
            SetCharmAlignEnabled(align.Charms.Value);
        if (align.Crosshair.HasValue)
            SetCrosshairAlignEnabled(align.Crosshair.Value);
        if (align.LeftHandDesired.HasValue)
        {
            _leftHandDesiredEnabled = align.LeftHandDesired.Value;
            if (!_leftHandDesiredEnabled)
                reply(LeftHandDesiredFidelityNotice);
        }
        if (align.Scoreboard.HasValue)
            SetScoreboardAlignEnabled(align.Scoreboard.Value);
    }

    private void ApplyRuntimeFidelityConfig(DemoTracerFidelityConfig? fidelity, Action<string> reply)
    {
        if (fidelity == null)
            return;

        if (!string.IsNullOrWhiteSpace(fidelity.Preset))
        {
            switch (fidelity.Preset.Trim().ToLowerInvariant())
            {
                case "default":
                    SetWeaponAlignEnabled(true);
                    SetProjectileAlignEnabled(true);
                    SetCrosshairAlignEnabled(true);
                    _leftHandDesiredEnabled = true;
                    break;
                case "full":
                    SetWeaponAlignEnabled(true);
                    SetProjectileAlignEnabled(true);
                    SetCrosshairAlignEnabled(true);
                    _leftHandDesiredEnabled = true;
                    break;
                case "handoff_safe":
                case "handoff-safe":
                case "handoff":
                    SetWeaponAlignEnabled(true);
                    SetProjectileAlignEnabled(true);
                    SetCrosshairAlignEnabled(true);
                    _leftHandDesiredEnabled = false;
                    reply(LeftHandDesiredFidelityNotice);
                    break;
                case "off":
                case "none":
                    SetWeaponAlignEnabled(false);
                    SetProjectileAlignEnabled(false);
                    SetCrosshairAlignEnabled(false);
                    _leftHandDesiredEnabled = false;
                    reply(LeftHandDesiredFidelityNotice);
                    break;
                default:
                    reply($"[DTR WARN] ignored config fidelity.preset=\"{fidelity.Preset}\"");
                    break;
            }
        }

        if (fidelity.Weapons.HasValue)
            SetWeaponAlignEnabled(fidelity.Weapons.Value);
        if (fidelity.Projectiles.HasValue)
            SetProjectileAlignEnabled(fidelity.Projectiles.Value);
        if (fidelity.Crosshair.HasValue)
            SetCrosshairAlignEnabled(fidelity.Crosshair.Value);
        if (fidelity.LeftHandDesired.HasValue)
        {
            _leftHandDesiredEnabled = fidelity.LeftHandDesired.Value;
            if (!_leftHandDesiredEnabled)
                reply(LeftHandDesiredFidelityNotice);
        }
    }

    private void ApplyRuntimeMatchConfig(DemoTracerMatchConfig? match, Action<string> reply)
    {
        if (match == null)
            return;

        if (!string.IsNullOrWhiteSpace(match.Preset))
        {
            switch (match.Preset.Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                    SetScoreboardAlignEnabled(false);
                    break;
                case "scoreboard":
                case "full":
                case "all":
                    SetScoreboardAlignEnabled(true);
                    break;
                default:
                    reply($"[DTR WARN] ignored config match.preset=\"{match.Preset}\"");
                    break;
            }
        }

        if (match.Scoreboard.HasValue)
            SetScoreboardAlignEnabled(match.Scoreboard.Value);
    }

    private void ApplyRuntimeCosmeticsConfig(DemoTracerCosmeticsConfig? cosmetics, Action<string> reply)
    {
        if (cosmetics == null)
            return;

        if (!string.IsNullOrWhiteSpace(cosmetics.Preset))
        {
            switch (cosmetics.Preset.Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                    ApplyCosmeticPreset(CosmeticPreset.Off);
                    break;
                case "weapons":
                case "weapon":
                    ApplyCosmeticPreset(CosmeticPreset.Weapons);
                    break;
                case "basic":
                    ApplyCosmeticPreset(CosmeticPreset.Basic);
                    break;
                case "full":
                case "all":
                    ApplyCosmeticPreset(CosmeticPreset.Full);
                    break;
                default:
                    reply($"[DTR WARN] ignored config cosmetics.preset=\"{cosmetics.Preset}\"");
                    break;
            }
        }

        if (cosmetics.Weapons.HasValue)
            _cosmeticWeaponsEnabled = cosmetics.Weapons.Value;
        if (cosmetics.Knives.HasValue)
            _cosmeticKnivesEnabled = cosmetics.Knives.Value;
        if (cosmetics.Gloves.HasValue)
            _cosmeticGlovesEnabled = cosmetics.Gloves.Value;
        if (cosmetics.Names.HasValue)
            _cosmeticNamesEnabled = cosmetics.Names.Value;
        if (cosmetics.Agents.HasValue)
            _cosmeticAgentsEnabled = cosmetics.Agents.Value;
        if (cosmetics.Stickers.HasValue)
            SetStickerAlignEnabled(cosmetics.Stickers.Value);
        if (cosmetics.Charms.HasValue)
            SetCharmAlignEnabled(cosmetics.Charms.Value);
        if (cosmetics.PreserveNative.HasValue)
            _preserveNativeBotCosmetics = cosmetics.PreserveNative.Value;

        RefreshCosmeticAlignEnabled();
        if (!_cosmeticAlignEnabled)
        {
            ResetCosmeticAlignState();
            ResetStickerAlignState();
            ResetCharmAlignState();
        }
    }

    private void ApplyRuntimeHandoffConfig(DemoTracerHandoffConfig? handoff, Action<string> reply)
    {
        if (handoff == null)
            return;

        if (!string.IsNullOrWhiteSpace(handoff.Mode))
        {
            if (TryParseHandoffMode(handoff.Mode, out var mode))
                _handoffMode = mode;
            else
                reply($"[DTR WARN] ignored config handoff.mode=\"{handoff.Mode}\"");
        }

        if (!string.IsNullOrWhiteSpace(handoff.Scope))
        {
            if (handoff.Scope.Equals("slot", StringComparison.OrdinalIgnoreCase))
                _handoffAllSlots = false;
            else if (handoff.Scope.Equals("all", StringComparison.OrdinalIgnoreCase))
                _handoffAllSlots = true;
            else
                reply($"[DTR WARN] ignored config handoff.scope=\"{handoff.Scope}\"; expected slot or all");
        }

        if (handoff.Threat360.HasValue)
        {
            _handoffThreat360Enabled = handoff.Threat360.Value;
            if (!_handoffThreat360Enabled)
                _pendingThreat360.Clear();
        }

        if (handoff.Threat360Range.HasValue)
        {
            _handoffThreat360Range = Math.Clamp(
                handoff.Threat360Range.Value,
                HandoffThreat360MinRange,
                HandoffThreat360MaxRange);
            _pendingThreat360.Clear();
        }

        if (handoff.Threat360Los.HasValue)
        {
            _handoffThreat360LosEnabled = handoff.Threat360Los.Value;
            _pendingThreat360.Clear();
        }

        if (!string.IsNullOrWhiteSpace(handoff.ViewmodelContinuity))
        {
            if (TryParseViewmodelContinuityMode(handoff.ViewmodelContinuity, out var continuityMode))
                _viewmodelContinuityMode = continuityMode;
            else
                reply($"[DTR WARN] ignored config handoff.viewmodel_continuity=\"{handoff.ViewmodelContinuity}\"; expected release or round");
        }
    }

    private void ApplyRuntimeConfigSideEffects()
    {
        if (!_playoffEnabled)
            CancelPlayoffPreparation(unloadPrepared: true);
        if (!_roundBannerEnabled)
            CancelDtrRoundBanner(resetRound: false);
        BotControllerNative.WriteLeftHandDesired = _leftHandDesiredEnabled;
        if (!_leftHandDesiredEnabled)
            ClearReplayLeftHandDesiredLatches(forceNative: true);
        if (_viewmodelContinuityMode == ViewmodelContinuityMode.Release)
            RestoreRetainedReplayBotViewmodels();
        BotControllerNative.SetReplayNativeFovOverride(_handoffThreat360Enabled);
        if (_replayIdentityMode != ReplayIdentityMode.Avatar)
            Server.ExecuteCommand("sv_reliableavatardata false");
        if (_loadedSlots.Count > 0 || _companionCrosshairOverrides.Count > 0)
            _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void ReplyRuntimeSettings(Action<string> reply, string prefix)
    {
        reply($"{prefix} schema=v2 legacy_align={FormatOnOff(_runtimeConfigHadLegacyAlign)} new_sections={FormatOnOff(_runtimeConfigHadNewSections)}");
        reply($"{prefix} playback identity={ReplayIdentityModeName()} allow_partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)} chat_auto={FormatOnOff(_chatAutoEnabled)} round_banner={FormatOnOff(_roundBannerEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} handoff_360={FormatOnOff(_handoffThreat360Enabled)} range={_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={FormatOnOff(_handoffThreat360LosEnabled)}");
        reply($"{prefix} fidelity preset={AlignPresetName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand={FormatOnOff(_leftHandDesiredEnabled)}");
        reply($"{prefix} match preset={(_scoreboardAlignEnabled ? "scoreboard" : "off")} scoreboard={FormatOnOff(_scoreboardAlignEnabled)}");
        reply($"{prefix} cosmetics preset={CosmeticPresetName()} risk={FormatOnOff(_cosmeticAlignEnabled)} weapons={FormatOnOff(_cosmeticWeaponsEnabled)} knives={FormatOnOff(_cosmeticKnivesEnabled)} gloves={FormatOnOff(_cosmeticGlovesEnabled)} names={FormatOnOff(_cosmeticNamesEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)}");
    }

    private static bool TryParseReplayIdentityMode(string value, out ReplayIdentityMode mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" => ReplayIdentityMode.Off,
            "name" => ReplayIdentityMode.Name,
            "steam" or "sid" or "steamid" or "1" or "on" or "true" => ReplayIdentityMode.Steam,
            "avatar" or "avatars" or "event_avatar" or "event-avatar" => ReplayIdentityMode.Avatar,
            "full" => ReplayIdentityMode.Avatar,
            _ => ReplayIdentityMode.Off,
        };
        return value.Trim().ToLowerInvariant() is
            "off" or "0" or "false" or
            "name" or
            "steam" or "sid" or "steamid" or "1" or "on" or "true" or
            "avatar" or "avatars" or "event_avatar" or "event-avatar" or
            "full";
    }

    public sealed class DemoTracerRuntimeConfig
    {
        [JsonPropertyName("identity")]
        public string? Identity { get; set; }

        [JsonPropertyName("allow_partial")]
        public bool? AllowPartial { get; set; }

        [JsonPropertyName("playoff")]
        public bool? Playoff { get; set; }

        [JsonPropertyName("chat_auto")]
        public bool? ChatAuto { get; set; }

        [JsonPropertyName("round_banner")]
        public bool? RoundBanner { get; set; }

        [JsonPropertyName("handoff")]
        public DemoTracerHandoffConfig? Handoff { get; set; }

        [JsonPropertyName("align")]
        public DemoTracerAlignConfig? Align { get; set; }

        [JsonPropertyName("fidelity")]
        public DemoTracerFidelityConfig? Fidelity { get; set; }

        [JsonPropertyName("match")]
        public DemoTracerMatchConfig? Match { get; set; }

        [JsonPropertyName("cosmetics")]
        public DemoTracerCosmeticsConfig? Cosmetics { get; set; }
    }

    public sealed class DemoTracerHandoffConfig
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("threat_360")]
        public bool? Threat360 { get; set; }

        [JsonPropertyName("threat_360_range")]
        public float? Threat360Range { get; set; }

        [JsonPropertyName("threat_360_los")]
        public bool? Threat360Los { get; set; }

        [JsonPropertyName("viewmodel_continuity")]
        public string? ViewmodelContinuity { get; set; }
    }

    public sealed class DemoTracerAlignConfig
    {
        [JsonPropertyName("weapons")]
        public bool? Weapons { get; set; }

        [JsonPropertyName("projectiles")]
        public bool? Projectiles { get; set; }

        [JsonPropertyName("crosshair")]
        public bool? Crosshair { get; set; }

        [JsonPropertyName("left_hand_desired")]
        public bool? LeftHandDesired { get; set; }

        [JsonPropertyName("cosmetics")]
        public bool? Cosmetics { get; set; }

        [JsonPropertyName("stickers")]
        public bool? Stickers { get; set; }

        [JsonPropertyName("charms")]
        public bool? Charms { get; set; }

        [JsonPropertyName("scoreboard")]
        public bool? Scoreboard { get; set; }
    }

    public sealed class DemoTracerFidelityConfig
    {
        [JsonPropertyName("preset")]
        public string? Preset { get; set; }

        [JsonPropertyName("weapons")]
        public bool? Weapons { get; set; }

        [JsonPropertyName("projectiles")]
        public bool? Projectiles { get; set; }

        [JsonPropertyName("crosshair")]
        public bool? Crosshair { get; set; }

        [JsonPropertyName("left_hand_desired")]
        public bool? LeftHandDesired { get; set; }
    }

    public sealed class DemoTracerMatchConfig
    {
        [JsonPropertyName("preset")]
        public string? Preset { get; set; }

        [JsonPropertyName("scoreboard")]
        public bool? Scoreboard { get; set; }
    }

    public sealed class DemoTracerCosmeticsConfig
    {
        [JsonPropertyName("preset")]
        public string? Preset { get; set; }

        [JsonPropertyName("weapons")]
        public bool? Weapons { get; set; }

        [JsonPropertyName("knives")]
        public bool? Knives { get; set; }

        [JsonPropertyName("gloves")]
        public bool? Gloves { get; set; }

        [JsonPropertyName("names")]
        public bool? Names { get; set; }

        [JsonPropertyName("agents")]
        public bool? Agents { get; set; }

        [JsonPropertyName("stickers")]
        public bool? Stickers { get; set; }

        [JsonPropertyName("charms")]
        public bool? Charms { get; set; }

        [JsonPropertyName("preserve_native")]
        public bool? PreserveNative { get; set; }
    }
}
