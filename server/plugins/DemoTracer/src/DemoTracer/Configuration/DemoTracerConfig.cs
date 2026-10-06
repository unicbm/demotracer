/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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

    private static readonly JsonSerializerOptions RuntimeConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
        => Path.Combine(ModuleDirectory, RuntimeConfigFileName);

    private void LoadRuntimeConfig(Action<string> reply, bool announceMissing)
    {
        var path = RuntimeConfigPath();
        if (!File.Exists(path))
        {
            if (announceMissing)
                reply($"[DTR OK] config not found; using built-in defaults. path=\"{path}\"");
            ResetRuntimeConfigDefaults();
            ApplyRuntimeConfigSideEffects();
            return;
        }

        DemoTracerRuntimeConfig? config;
        try
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<DemoTracerRuntimeConfig>(json, RuntimeConfigJsonOptions);
            if (config?.UnsupportedAlign != null)
                throw new JsonException("The align config is no longer supported. Use fidelity and cosmetics.");
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

        ApplyRuntimeFidelityConfig(config.Fidelity, reply);
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
        _viewmodelContinuityMode = ViewmodelContinuityMode.Round;
        _chatAutoEnabled = true;
        _roundBannerEnabled = true;

        SetWeaponAlignEnabled(true);
        SetProjectileAlignEnabled(true);
        SetCrosshairAlignEnabled(true);
        _leftHandDesiredEnabled = true;
        _balanceAlignEnabled = false;
        ApplyCosmeticPreset(CosmeticPreset.Off);
        _preserveNativeBotCosmetics = false;
    }

    private void ApplyRuntimeFidelityConfig(DemoTracerFidelityConfig? fidelity, Action<string> reply)
    {
        if (fidelity == null)
            return;

        if (!string.IsNullOrWhiteSpace(fidelity.Preset))
        {
            if (ParseReplayFidelityPreset(fidelity.Preset) is { } preset)
            {
                ApplyReplayFidelityPreset(preset);
                if (!preset.LeftHandDesired)
                    reply(LeftHandDesiredFidelityNotice);
            }
            else
                reply($"[DTR WARN] ignored config fidelity.preset=\"{fidelity.Preset}\"");
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
        if (fidelity.Balance.HasValue)
            _balanceAlignEnabled = fidelity.Balance.Value;
    }

    private void ApplyRuntimeCosmeticsConfig(DemoTracerCosmeticsConfig? cosmetics, Action<string> reply)
    {
        if (cosmetics == null)
            return;

        if (!string.IsNullOrWhiteSpace(cosmetics.Preset))
        {
            if (ParseCosmeticPreset(cosmetics.Preset) is { } preset)
                ApplyCosmeticPreset(preset);
            else
                reply($"[DTR WARN] ignored config cosmetics.preset=\"{cosmetics.Preset}\"");
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
            ResetCosmeticAlignState();
        _ = SyncBotRandomizerCosmeticLease(announce: false);
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
            _handoffThreat360Enabled = handoff.Threat360.Value;

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
            ClearReplayLeftHandDesiredLatches();
        if (_viewmodelContinuityMode == ViewmodelContinuityMode.Release)
            RestoreRetainedReplayBotViewmodels();
        BotControllerNative.SetReplayNativeFovOverride(_handoffThreat360Enabled);
        if (_replayIdentityMode != ReplayIdentityMode.Avatar)
        {
            ClearHumanTeamAvatarOverrides("identity_disabled");
            BotControllerNative.ClearAvatarOverrides();
            Server.ExecuteCommand("sv_reliableavatardata false");
        }
        else if (_session.TeamAvatarOverrides.Count > 0)
        {
            ScheduleHumanTeamAvatarOverrideReconciliation();
        }
        if (_session.LoadedSlots.Count > 0 || _retainedBotHiderPresentation.Count > 0)
            _ = SyncBotHiderPresentationLease(announce: false);
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private void ReplyRuntimeSettings(Action<string> reply, string prefix)
    {
        reply($"{prefix} playback identity={ReplayIdentityModeName()} allow_partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)} chat_auto={FormatOnOff(_chatAutoEnabled)} round_banner={FormatOnOff(_roundBannerEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} handoff_360={FormatOnOff(_handoffThreat360Enabled)}");
        reply($"{prefix} fidelity preset={AlignPresetName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_mode=birth_once crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand={FormatOnOff(_leftHandDesiredEnabled)} balance={FormatOnOff(_balanceAlignEnabled)}");
        reply($"{prefix} cosmetics preset={CosmeticPresetName()} risk={FormatOnOff(_cosmeticAlignEnabled)} weapons={FormatOnOff(_cosmeticWeaponsEnabled)} knives={FormatOnOff(_cosmeticKnivesEnabled)} gloves={FormatOnOff(_cosmeticGlovesEnabled)} names={FormatOnOff(_cosmeticNamesEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)}");
    }

    private static bool TryParseReplayIdentityMode(string value, out ReplayIdentityMode mode)
    {
        ReplayIdentityMode? parsed = value.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" => ReplayIdentityMode.Off,
            "name" => ReplayIdentityMode.Name,
            "steam" or "sid" or "steamid" or "1" or "on" or "true" => ReplayIdentityMode.Steam,
            "avatar" or "avatars" or "event_avatar" or "event-avatar" => ReplayIdentityMode.Avatar,
            "full" => ReplayIdentityMode.Avatar,
            _ => null,
        };
        mode = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }

    public sealed class DemoTracerRuntimeConfig
    {
        public string? Identity { get; set; }

        public bool? AllowPartial { get; set; }

        public bool? Playoff { get; set; }

        public bool? ChatAuto { get; set; }

        public bool? RoundBanner { get; set; }

        public DemoTracerHandoffConfig? Handoff { get; set; }

        [JsonPropertyName("align")]
        public JsonElement? UnsupportedAlign { get; set; }

        public DemoTracerFidelityConfig? Fidelity { get; set; }

        public DemoTracerCosmeticsConfig? Cosmetics { get; set; }
    }

    public sealed class DemoTracerHandoffConfig
    {
        public string? Mode { get; set; }

        public string? Scope { get; set; }

        [JsonPropertyName("threat_360")]
        public bool? Threat360 { get; set; }

        public string? ViewmodelContinuity { get; set; }
    }

    public sealed class DemoTracerFidelityConfig
    {
        public string? Preset { get; set; }

        public bool? Weapons { get; set; }

        public bool? Projectiles { get; set; }

        public bool? Crosshair { get; set; }

        public bool? LeftHandDesired { get; set; }

        public bool? Balance { get; set; }
    }

    public sealed class DemoTracerCosmeticsConfig
    {
        public string? Preset { get; set; }

        public bool? Weapons { get; set; }

        public bool? Knives { get; set; }

        public bool? Gloves { get; set; }

        public bool? Names { get; set; }

        public bool? Agents { get; set; }

        public bool? Stickers { get; set; }

        public bool? Charms { get; set; }

        public bool? PreserveNative { get; set; }
    }
}
