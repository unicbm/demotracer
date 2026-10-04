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
using DtrHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
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
        var preset = ParseCosmeticPreset(mode is "skins" or "skin" ? "weapons" : mode);
        if (preset.HasValue && (preset != CosmeticPreset.Weapons || command.ArgCount < 3))
            ApplyCosmeticPreset(preset.Value);
        else if (command.ArgCount < 3 || !SetCosmeticComponent(
                     mode, ParseOnOff(command.GetArg(2), preset == CosmeticPreset.Weapons && _cosmeticWeaponsEnabled),
                     command.ReplyToCommand))
        {
            command.ReplyToCommand("usage: dtr_cosmetics [status|off|weapons|basic|full]");
            command.ReplyToCommand("usage: dtr_cosmetics <weapons|knives|gloves|names|agents|stickers|charms|preserve_native> <on|off>");
            return;
        }

        ReplyCosmeticsStatus(command.ReplyToCommand);
        if (_cosmeticAlignEnabled)
            command.ReplyToCommand(CosmeticRiskNotice);
    }

    private static CosmeticPreset? ParseCosmeticPreset(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => CosmeticPreset.Off,
            "weapons" or "weapon" => CosmeticPreset.Weapons,
            "basic" => CosmeticPreset.Basic,
            "full" or "all" => CosmeticPreset.Full,
            _ => null,
        };

    private void ReplyCosmeticsStatus(Action<string> reply)
    {
        reply($"[DTR COSMETICS] preset={CosmeticPresetName()} risk={FormatOnOff(_cosmeticAlignEnabled)}");
        reply("[DTR COSMETICS] replay_identity_claims=agent,knife,gloves missing=native_agent,team_knife,no_gloves");
        reply($"[DTR COSMETICS] weapons={FormatOnOff(_cosmeticWeaponsEnabled)} knives={FormatOnOff(_cosmeticKnivesEnabled)} gloves={FormatOnOff(_cosmeticGlovesEnabled)} names={FormatOnOff(_cosmeticNamesEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)}");
        reply($"[DTR COSMETICS] {FormatCosmeticStatusCounts()}");
    }

    private void ReplyMatchStatus(Action<string> reply)
    {
        reply($"[DTR MATCH] preset={(_scoreboardAlignEnabled ? "scoreboard" : "off")}");
        reply($"[DTR MATCH] scoreboard={FormatOnOff(_scoreboardAlignEnabled)} {FormatScoreboardStatusCounts()}");
    }

    private void ApplyCosmeticPreset(CosmeticPreset preset)
    {
        _cosmeticWeaponsEnabled = _cosmeticNamesEnabled = preset != CosmeticPreset.Off;
        _cosmeticKnivesEnabled = _cosmeticGlovesEnabled = _cosmeticAgentsEnabled =
            preset is CosmeticPreset.Basic or CosmeticPreset.Full;
        _stickerAlignEnabled = _charmAlignEnabled = preset == CosmeticPreset.Full;

        RefreshCosmeticAlignEnabled();
        if (!_cosmeticAlignEnabled)
        {
            ResetCosmeticAlignState();
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

    private void RefreshCosmeticAlignEnabled()
    {
        _cosmeticAlignEnabled = AnyCosmeticFeatureEnabled();
        if (!_cosmeticAlignEnabled)
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private void SetStickerAlignEnabled(bool enabled)
    {
        _stickerAlignEnabled = enabled;
        RefreshCosmeticAlignEnabled();
    }

    private void SetCharmAlignEnabled(bool enabled)
    {
        _charmAlignEnabled = enabled;
        RefreshCosmeticAlignEnabled();
    }

    private void SetCrosshairAlignEnabled(bool enabled)
    {
        if (!enabled)
        {
            _crosshairAlignEnabled = false;
            ClearReplayCrosshairPresentation();
            return;
        }

        _crosshairAlignEnabled = true;
        if (_session.LoadedSlots.Count > 0)
            _ = RefreshReplayCrosshairPresentation();
    }

    private void SetScoreboardAlignEnabled(bool enabled)
    {
        _scoreboardAlignEnabled = enabled;
        if (!_scoreboardAlignEnabled)
            ResetScoreboardAlignState();
    }

}
