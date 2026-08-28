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

    private void ReplyCosmeticsStatus(Action<string> reply)
    {
        reply($"[DTR COSMETICS] preset={CosmeticPresetName()} risk={FormatOnOff(_cosmeticAlignEnabled)}");
        reply("[DTR COSMETICS] replay_identity_claims=agent,knife,gloves missing=native_agent,team_knife,no_gloves");
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
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

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
