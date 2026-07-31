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
                _session.PendingThreat360.Clear();
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
            _session.PendingThreat360.Clear();
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
            _session.PendingThreat360.Clear();
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
        reply($"[DTR ALIGN] weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand={FormatOnOff(_leftHandDesiredEnabled)} balance={FormatOnOff(_balanceAlignEnabled)}");
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
            RestoreAllReplayMusicKits("cosmetics_disabled");
        _ = SyncBotRandomizerCosmeticLease(announce: false);
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

        _session.PendingWeaponSlotReplacements.Clear();
        _session.RebuiltInventorySlots.Clear();
        _session.LastReplayWeaponDef.Clear();
        _session.LastLockedWeaponTarget.Clear();
        _session.ActiveWeaponCosmetics.Clear();
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
        _session.PendingProjectileAlign.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
    }

    private void SetProjectileAlignTicks(int totalWrites)
    {
        _projectileAlignTotalWrites = totalWrites;
        foreach (var pending in _session.PendingProjectileAlign.Values)
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
        if (_session.LoadedSlots.Count > 0)
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
            if (_session.LoadedReplays.TryGetValue(bot.Slot, out var replay) &&
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
            var plan = _session.Plan.SequenceActive
                ? _session.Plan.SequenceIndex < _session.Plan.SequenceRounds.Length
                    ? $"sequence from_source_round={_session.Plan.SequenceRounds[_session.Plan.SequenceIndex]} prepared={_session.Plan.SequencePrepared}:{_session.Plan.SequencePreparedRound}"
                    : "sequence complete"
                : HasPlayoffSchedulingState()
                    ? $"playoff {FormatPlayoffPlanStatus()}"
                : _session.Plan.Armed
                    ? $"single source_round={_session.Plan.ArmedSourceRound} prepared={_session.Plan.ArmedPrepared}"
                    : "none";
            command.ReplyToCommand(
                $"[DTR OK] status plan={plan} loaded_slots={_session.LoadedSlots.Count} settings identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)} balance={FormatOnOff(_balanceAlignEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} allow_partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)}:{FormatPlayoffPlanStatus()} {FormatVoiceAutoStatusInline()} {FormatChatAutoStatusInline()} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatViewmodelStatusCounts()} {FormatScoreboardStatusCounts()}");
            return;
        }

        var slotArg = command.GetArg(1).Equals("slot", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot))
            return;
        var state = BotControllerNative.GetReplayState(slot);
        var sequence = _session.Plan.SequenceActive && _session.Plan.SequenceIndex < _session.Plan.SequenceRounds.Length
            ? $" sequence_next={_session.Plan.SequenceRounds[_session.Plan.SequenceIndex]}"
            : string.Empty;
        var playoff = _playoffEnabled
            ? $" playoff={FormatPlayoffPlanStatus()}"
            : string.Empty;
        var roundStartBalance = _session.LoadedReplays.TryGetValue(slot, out var loadedReplay) &&
                                loadedReplay.RoundStartBalance is uint recordedBalance
            ? recordedBalance.ToString(CultureInfo.InvariantCulture)
            : "none";
        command.ReplyToCommand(
            $"dtr: abi={BotControllerNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} handoff_360={_handoffThreat360Enabled}:{_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled}:{_rayTraceLosProbe.ProbeStatus} partial={_partialReplayEnabled} identity={ReplayIdentityModeName()} projectile_align={_projectileAlignEnabled} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} cosmetic_align={_cosmeticAlignEnabled} agent_align={_cosmeticAgentsEnabled} sticker_align={_stickerAlignEnabled} charm_align={_charmAlignEnabled} preserve_native={_preserveNativeBotCosmetics} crosshair_align={_crosshairAlignEnabled} left_hand_desired={_leftHandDesiredEnabled} balance_align={_balanceAlignEnabled} round_start_balance={roundStartBalance} balance_applied={_session.BalanceSyncedSlots.Contains(slot)} scoreboard_align={_scoreboardAlignEnabled} {FormatVoiceAutoStatusInline()} {FormatChatAutoStatusInline()}{sequence}{playoff}");
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
        var loadedPlaying = _session.LoadedSlots.Count(slot => BotControllerNative.GetReplayState(slot).Playing);

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
            $"[DTR DOCTOR] replay loaded={_session.LoadedSlots.Count} playing={loadedPlaying} identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_ticks={FormatProjectileAlignTicks()} molotov_point={FormatMolotovPointAlignMode(_molotovPointAlignMode)}:{_molotovPointAlignLeadTicks} cosmetics={FormatOnOff(_cosmeticAlignEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)}:{FormatPlayoffPlanStatus()} raytrace={_rayTraceLosProbe.ProbeStatus} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatViewmodelStatusCounts()} {FormatScoreboardStatusCounts()}");

        if (command.ArgCount >= 2)
            ReplyDoctorManifest(command, command.GetArg(1));
    }

    private void ReplyDoctorManifest(CommandInfo command, string manifestPath)
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
}
