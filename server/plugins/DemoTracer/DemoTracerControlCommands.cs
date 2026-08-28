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
                $"[DTR OK] status plan={plan} loaded_slots={_session.ReplaySlots.LoadedCount} claimed_slots={_session.ReplaySlots.OwnedCount} playing_slots={_session.ReplaySlots.PlayingCount} settings identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_mode=birth_once cosmetics={FormatOnOff(_cosmeticAlignEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)} balance={FormatOnOff(_balanceAlignEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} allow_partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)}:{FormatPlayoffPlanStatus()} {FormatVoiceAutoStatusInline()} {FormatChatAutoStatusInline()} mp_freezetime={(float.IsFinite(freezeTime) ? freezeTime.ToString("F2", CultureInfo.InvariantCulture) : "unknown")} {(string.IsNullOrEmpty(freezeReason) ? "" : freezeReason)} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatViewmodelStatusCounts()} {FormatScoreboardStatusCounts()}");
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
            $"dtr: abi={BotControllerNative.AbiVersion} slot={slot} playing={state.Playing} cursor={state.Cursor} total={state.Total} handoff={FormatHandoffMode(_handoffMode)} scope={(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} handoff_360={_handoffThreat360Enabled}:{_handoffThreat360Range.ToString("F0", CultureInfo.InvariantCulture)} los={_handoffThreat360LosEnabled}:{_rayTraceLosProbe.ProbeStatus} partial={_partialReplayEnabled} identity={ReplayIdentityModeName()} projectile_align={_projectileAlignEnabled} projectile_mode=birth_once cosmetic_align={_cosmeticAlignEnabled} agent_align={_cosmeticAgentsEnabled} sticker_align={_stickerAlignEnabled} charm_align={_charmAlignEnabled} preserve_native={_preserveNativeBotCosmetics} crosshair_align={_crosshairAlignEnabled} left_hand_desired={_leftHandDesiredEnabled} balance_align={_balanceAlignEnabled} round_start_balance={roundStartBalance} balance_applied={_session.BalanceSyncedSlots.Contains(slot)} scoreboard_align={_scoreboardAlignEnabled} {FormatVoiceAutoStatusInline()} {FormatChatAutoStatusInline()}{sequence}{playoff}");
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
            $"[DTR DOCTOR] replay loaded={_session.ReplaySlots.LoadedCount} claimed={_session.ReplaySlots.OwnedCount} managed_playing={_session.ReplaySlots.PlayingCount} native_playing={loadedPlaying} identity={ReplayIdentityModeName()} weapons={FormatOnOff(_weaponAlignEnabled)} projectiles={FormatOnOff(_projectileAlignEnabled)} projectile_mode=birth_once cosmetics={FormatOnOff(_cosmeticAlignEnabled)} agents={FormatOnOff(_cosmeticAgentsEnabled)} stickers={FormatOnOff(_stickerAlignEnabled)} charms={FormatOnOff(_charmAlignEnabled)} preserve_native={FormatOnOff(_preserveNativeBotCosmetics)} crosshair={FormatOnOff(_crosshairAlignEnabled)} left_hand_desired={FormatOnOff(_leftHandDesiredEnabled)} scoreboard={FormatOnOff(_scoreboardAlignEnabled)} handoff={FormatHandoffMode(_handoffMode)}:{(_handoffAllSlots ? "all" : "slot")} viewmodel_continuity={ViewmodelContinuityModeName()} partial={FormatOnOff(_partialReplayEnabled)} playoff={FormatOnOff(_playoffEnabled)}:{FormatPlayoffPlanStatus()} raytrace={_rayTraceLosProbe.ProbeStatus} {FormatCosmeticStatusCounts()} {FormatCrosshairStatusCounts()} {FormatViewmodelStatusCounts()} {FormatScoreboardStatusCounts()}");

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
