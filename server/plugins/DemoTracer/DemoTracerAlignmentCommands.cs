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
        command.ReplyToCommand($"dtr: projectile_align={_projectileAlignEnabled} mode=engine_birth_once");
    }

    [ConsoleCommand("dtr_projectile_align_log", "dtr_projectile_align_log [clear|all|molotov|fire]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ProjectileAlignLogCommand(CCSPlayerController? player, CommandInfo command)
    {
        var mode = command.ArgCount >= 2 ? command.GetArg(1).Trim().ToLowerInvariant() : "all";
        if (mode is "clear" or "reset")
        {
            _session.ProjectileAlignLog.Clear();
            command.ReplyToCommand("dtr: projectile_align_log cleared");
            return;
        }

        var filterFire = mode is "molotov" or "fire" or "incendiary" or "incgrenade";
        var lines = _session.ProjectileAlignLog
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

    [ConsoleCommand("dtr_align", "dtr_align [status|default|full|handoff_safe|off|weapons|projectiles|left_hand|crosshair|balance] [on|off]")]
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
                    balance: false,
                    command.ReplyToCommand);
                return;
            case "full":
                ApplyReplayFidelityPreset(
                    weapons: true,
                    projectiles: true,
                    leftHandDesired: true,
                    crosshair: true,
                    balance: true,
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
                    balance: false,
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
                    balance: false,
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
        reply($"[DTR ALIGN] weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_mode=birth_once crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand={FormatOnOff(_leftHandDesiredEnabled)} balance={FormatOnOff(_balanceAlignEnabled)}");
        reply("[DTR ALIGN] note: cosmetics moved to dtr_cosmetics; scoreboard moved to dtr_match");
    }

    private static void ReplyAlignUsage(Action<string> reply)
    {
        reply("usage: dtr_align [status|default|full|handoff_safe|off]");
        reply("usage: dtr_align <weapons|projectiles|crosshair|left_hand|balance> <on|off>");
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
        if (_weaponAlignEnabled && _projectileAlignEnabled && _crosshairAlignEnabled && _leftHandDesiredEnabled && _balanceAlignEnabled)
            return "full";
        if (_weaponAlignEnabled && _projectileAlignEnabled && _crosshairAlignEnabled && _leftHandDesiredEnabled && !_balanceAlignEnabled)
            return "default";
        if (_weaponAlignEnabled && _projectileAlignEnabled && _crosshairAlignEnabled && !_leftHandDesiredEnabled && !_balanceAlignEnabled)
            return "handoff_safe";
        if (!_weaponAlignEnabled && !_projectileAlignEnabled && !_crosshairAlignEnabled && !_leftHandDesiredEnabled && !_balanceAlignEnabled)
            return "off";
        return "custom";
    }

    private void ApplyReplayFidelityPreset(
        bool weapons,
        bool projectiles,
        bool leftHandDesired,
        bool crosshair,
        bool balance,
        Action<string> reply)
    {
        SetWeaponAlignEnabled(weapons);
        SetProjectileAlignEnabled(projectiles);
        ApplyLeftHandDesiredMode(leftHandDesired, reply);
        SetCrosshairAlignEnabled(crosshair);
        _balanceAlignEnabled = balance;
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
            case "balance":
            case "money":
            case "cash":
                _balanceAlignEnabled = enabled;
                reply($"[DTR OK] dtr_align balance={FormatOnOff(_balanceAlignEnabled)}");
                return true;
            default:
                return false;
        }
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
        {
            _ = SyncBotRandomizerCosmeticLease(announce: false);
            return;
        }

        ClearAllPendingWeaponSlotReplacements("weapon_alignment_disabled");
        _session.RebuiltInventorySlots.Clear();
        _session.LastReplayWeaponDef.Clear();
        _session.LastLockedWeaponTarget.Clear();
        foreach (var slot in _session.LoadedSlots)
            BotControllerNative.UnlockWeaponSlot(slot);
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private void SetProjectileAlignEnabled(bool enabled)
    {
        _projectileAlignEnabled = enabled;
        if (_projectileAlignEnabled)
            return;

        _session.ProjectileAlignNextBySlot.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
    }

}
