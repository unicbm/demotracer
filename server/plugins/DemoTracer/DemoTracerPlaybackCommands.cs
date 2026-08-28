/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    [ConsoleCommand("dtr_load", "dtr_load <round|slot> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void LoadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_load round <manifest.json> <source_round> | dtr_load slot <slot> <path.dtr>");
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        if (mode == "round")
        {
            if (!TryParseRoundArgs(command, "dtr_load round", out var manifestPath, out var round, argOffset: 2))
                return;

            ActivatePendingReplayRetentionPriority();
            var result = LoadRound(manifestPath, round);
            command.ReplyToCommand(result.Message);
            return;
        }

        var slotArg = mode == "slot" ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot) || command.ArgCount <= slotArg + 1)
        {
            command.ReplyToCommand("usage: dtr_load slot <slot> <path.dtr>");
            command.ReplyToCommand("legacy usage: dtr_load <slot> <path.dtr>");
            return;
        }

        var path = command.GetArg(slotArg + 1);
        if (!IsReplaySlotStillSafe(slot))
        {
            command.ReplyToCommand($"dtr: refused to load slot {slot}: not a safe bot target");
            return;
        }

        var ok = BotControllerNative.LoadReplayFromFile(slot, path, out var replayMetadata);
        if (ok)
        {
            RememberLoadedSlot(slot);
            TrackLoadedReplay(slot, path, $"slot{slot}", replayMetadata: replayMetadata);
        }

        command.ReplyToCommand(ok
            ? $"dtr: loaded slot {slot}: {path}"
            : $"dtr: failed to load slot {slot}: {path} ({BotControllerNative.LastLoadError})");
    }

    [ConsoleCommand("dtr_load_round", "dtr_load_round <manifest.json> <source_round>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void LoadRoundCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!TryParseRoundArgs(command, "dtr_load_round", out var manifestPath, out var round))
            return;

        ActivatePendingReplayRetentionPriority();
        var result = LoadRound(manifestPath, round);
        command.ReplyToCommand(result.Message);
    }

    [ConsoleCommand("dtr_play_loaded", "dtr_play_loaded [loop:0|1]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlayLoadedCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        var loop = command.ArgCount >= 2 && command.GetArg(1) != "0";
        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
            return;
        command.ReplyToCommand("[DTR WARN] dtr_play loaded is manual/debug playback; it bypasses round_start/round_freeze_end lifecycle alignment.");
        command.ReplyToCommand(PlayLoaded(loop));
    }

    [ConsoleCommand("dtr_play", "dtr_play <loaded|slot> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlayCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_play loaded [loop:0|1] | dtr_play slot <slot> [loop:0|1]");
            command.ReplyToCommand("legacy usage: dtr_play <slot> [loop:0|1]");
            return;
        }

        var mode = command.GetArg(1).ToLowerInvariant();
        if (mode == "loaded")
        {
            var loopLoaded = command.ArgCount >= 3 && command.GetArg(2) != "0";
            if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
                return;
            command.ReplyToCommand("[DTR WARN] dtr_play loaded is manual/debug playback; it bypasses round_start/round_freeze_end lifecycle alignment.");
            command.ReplyToCommand(PlayLoaded(loopLoaded));
            return;
        }

        var slotArg = mode == "slot" ? 2 : 1;
        if (!TryParseSlotAt(command, slotArg, out var slot))
            return;
        if (!IsReplaySlotStillSafe(slot))
        {
            command.ReplyToCommand($"dtr: refused to play slot {slot}: not a safe bot target");
            return;
        }
        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
            return;

        var loop = command.ArgCount > slotArg + 1 && command.GetArg(slotArg + 1) != "0";
        _session.ReplaySlots.Claim(slot);
        if (_session.LoadedReplays.TryGetValue(slot, out var replay))
            PreloadReplayWeaponsForSlot(slot, replay);
        _session.LastEnsuredWeaponDef.Remove(slot);

        var ok = StartReplayForSlot(slot, loop);
        if (ok)
        {
            MarkReplayStarted(slot);
        }
        else
        {
            ReleaseReplaySlot(slot, "manual_start_failed");
        }
        var state = ok ? default : BotControllerNative.GetReplayState(slot);
        command.ReplyToCommand(ok
            ? $"dtr: playing slot {slot}, loop={loop}"
            : $"dtr: failed to play slot {slot} (cursor={state.Cursor}, total={state.Total})");
    }

    [ConsoleCommand("dtr_stop", "dtr_stop <sequence|replay|slot|all> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("usage: dtr_stop sequence|replay|slot <slot>|all");
            command.ReplyToCommand("legacy usage: dtr_stop <slot>");
            return;
        }

        switch (command.GetArg(1).ToLowerInvariant())
        {
            case "sequence":
            case "seq":
                StopSequenceState();
                command.ReplyToCommand("[DTR OK] sequence scheduling stopped");
                return;
            case "replay":
            case "loaded":
                StopLoadedReplaySlots("manual_stop_replay");
                command.ReplyToCommand("[DTR OK] current loaded/running replay slots stopped");
                return;
            case "all":
                StopAllState("manual_stop_all");
                command.ReplyToCommand("[DTR OK] all DemoTracer replay state stopped");
                return;
            case "slot":
                if (!TryParseSlotAt(command, 2, out var namedSlot))
                    return;
                StopOneSlot(command, namedSlot, "manual_stop");
                return;
            default:
                if (!TryParseSlotAt(command, 1, out var legacySlot))
                    return;
                StopOneSlot(command, legacySlot, "manual_stop");
                return;
        }
    }

    [ConsoleCommand("dtr_kick", "dtr_kick <exact-name>|slot <slot>|sid <steamid64>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void KickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            ReplyKickUsage(command);
            return;
        }

        var snapshot = BuildTickPlayerSnapshot();
        var candidates = BuildKickCandidates(snapshot);
        if (candidates.Count == 0)
        {
            command.ReplyToCommand("[DTR ERR] no kickable DemoTracer replay bots found");
            return;
        }

        var mode = command.GetArg(1).Trim().ToLowerInvariant();
        List<DtrKickCandidate> matches;
        string label;
        if (mode is "slot")
        {
            if (!TryParseSlotAt(command, 2, out var slot))
                return;
            matches = candidates.Where(candidate => candidate.Slot == slot).ToList();
            label = $"slot={slot}";
        }
        else if (mode is "sid" or "steamid" or "steam")
        {
            if (command.ArgCount < 3 ||
                !ulong.TryParse(command.GetArg(2), NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) ||
                steamId == 0)
            {
                command.ReplyToCommand("usage: dtr_kick sid <steamid64>");
                return;
            }
            matches = candidates.Where(candidate => candidate.SteamId == steamId).ToList();
            label = $"sid={steamId}";
        }
        else
        {
            var name = CommandArgumentsFrom(command, 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ReplyKickUsage(command);
                return;
            }
            matches = candidates
                .Where(candidate =>
                    candidate.LoadedName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    candidate.LiveName.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            label = $"name=\"{name}\"";
        }

        if (matches.Count == 0)
        {
            command.ReplyToCommand($"[DTR ERR] no unique DemoTracer replay bot matched {label}");
            return;
        }
        if (matches.Count > 1)
        {
            command.ReplyToCommand($"[DTR ERR] ambiguous dtr_kick target for {label}; choose a slot explicitly.");
            foreach (var candidate in matches)
                command.ReplyToCommand($"[DTR HINT] dtr_kick slot {candidate.Slot}  {FormatKickCandidate(candidate)}");
            return;
        }

        KickReplayCandidate(command, matches[0]);
    }

    private void ReplyKickUsage(CommandInfo command)
    {
        command.ReplyToCommand("usage: dtr_kick <exact-name>");
        command.ReplyToCommand("usage: dtr_kick slot <slot>");
        command.ReplyToCommand("usage: dtr_kick sid <steamid64>");
    }

    private List<DtrKickCandidate> BuildKickCandidates(TickPlayerSnapshot snapshot)
    {
        var slots = new SortedSet<int>();
        foreach (var slot in _session.LoadedSlots)
            slots.Add(slot);
        foreach (var slot in _session.LoadedReplays.Keys)
            slots.Add(slot);
        foreach (var slot in _retainedBotHiderPresentation.Keys)
            slots.Add(slot);
        foreach (var slot in NativeReplaySlots())
        {
            var state = BotControllerNative.GetReplayState(slot);
            if (state.Playing || state.Total > 0)
                slots.Add(slot);
        }

        var candidates = new List<DtrKickCandidate>();
        foreach (var slot in slots)
        {
            if (slot is < 0 or >= MaxPlayerSlots)
                continue;
            if (!snapshot.TryGetSlot(slot, out var controller) ||
                controller is not { IsValid: true } ||
                !IsReplaySlotStillSafe(slot, snapshot))
            {
                continue;
            }

            _session.LoadedReplays.TryGetValue(slot, out var replay);
            _retainedBotHiderPresentation.TryGetValue(slot, out var retained);
            var replayPlayerName = !string.IsNullOrWhiteSpace(replay.PlayerName)
                ? replay.PlayerName
                : retained.PlayerName;
            var replaySteamId = replay.SteamId != 0
                ? replay.SteamId
                : retained.SteamId;
            candidates.Add(new DtrKickCandidate(
                slot,
                controller.UserId,
                controller.Team,
                controller.PlayerName ?? string.Empty,
                replayPlayerName ?? string.Empty,
                replaySteamId,
                replay.RetentionRank > 0
                    ? replay.RetentionRank
                    : retained.RetentionRank > 0
                        ? retained.RetentionRank
                        : ReplayRetentionPriorityParser.MaxPlayersPerTeam));
        }

        return candidates;
    }

    private void KickReplayCandidate(CommandInfo command, DtrKickCandidate candidate)
    {
        if (!candidate.UserId.HasValue)
        {
            command.ReplyToCommand($"[DTR ERR] cannot kick slot {candidate.Slot}: missing userid");
            return;
        }

        var slot = candidate.Slot;
        var userId = candidate.UserId.Value;
        StopVoiceTestPlayback("dtr_kick", printSummary: false);
        if (!TryReleaseAndKickReplayCandidate(candidate, "dtr_kick", out var stopped, out var unloaded))
        {
            command.ReplyToCommand($"[DTR ERR] cannot kick slot {candidate.Slot}: missing userid");
            return;
        }

        command.ReplyToCommand(
            $"[DTR OK] kicked slot={slot} userid={userId.ToString(CultureInfo.InvariantCulture)} stopped={FormatOnOff(stopped)} unloaded={FormatOnOff(unloaded)}");
    }

    private bool TryReleaseAndKickReplayCandidate(
        DtrKickCandidate candidate,
        string reason,
        out bool stopped,
        out bool unloaded)
    {
        if (!TryReleaseReplayCandidate(
                candidate,
                reason,
                out _,
                out stopped,
                out unloaded))
            return false;

        Server.ExecuteCommand($"kickid {candidate.UserId!.Value.ToString(CultureInfo.InvariantCulture)}");
        return true;
    }

    private bool TryReleaseReplayCandidate(
        DtrKickCandidate candidate,
        string reason,
        out CCSPlayerController controller,
        out bool stopped,
        out bool unloaded)
    {
        controller = null!;
        stopped = false;
        unloaded = false;
        if (!candidate.UserId.HasValue)
            return false;

        var current = Utilities.GetPlayerFromSlot(candidate.Slot);
        if (current is not { IsValid: true } ||
            current.UserId != candidate.UserId)
        {
            return false;
        }

        controller = current;
        RemoveReplaySlot(candidate.Slot, reason, out stopped, out unloaded);
        return true;
    }

    private static string CommandArgumentsFrom(CommandInfo command, int startArg)
    {
        var parts = new List<string>();
        for (var i = startArg; i < command.ArgCount; i++)
            parts.Add(command.GetArg(i));
        return string.Join(' ', parts).Trim();
    }

    private static string FormatKickCandidate(DtrKickCandidate candidate)
    {
        var userId = candidate.UserId.HasValue
            ? candidate.UserId.Value.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var steamId = candidate.SteamId == 0
            ? "unknown"
            : candidate.SteamId.ToString(CultureInfo.InvariantCulture);
        return $"userid={userId} sid={steamId} keep={candidate.RetentionRank} live=\"{EscapeConsoleString(candidate.LiveName)}\" loaded=\"{EscapeConsoleString(candidate.LoadedName)}\"";
    }

    [ConsoleCommand("dtr_stop_all", "dtr_stop_all")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopAllCommand(CCSPlayerController? player, CommandInfo command)
    {
        StopAllState("manual_stop_all");
        command.ReplyToCommand("[DTR OK] all DemoTracer replay state stopped");
    }

    [ConsoleCommand("dtr_unload", "dtr_unload <slot>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void UnloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command) || !TryParseSlot(command, out var slot))
            return;
        var hadRetainedPresentation = _retainedBotHiderPresentation.ContainsKey(slot);
        var ok = BotControllerNative.UnloadReplay(slot);
        if (ok || hadRetainedPresentation)
        {
            StopVoiceTestPlayback("unload", printSummary: false);
            CommitReplaySlotRemoval(slot, "unload");
        }

        if (!ok && !hadRetainedPresentation)
        {
            command.ReplyToCommand(
                $"dtr: failed to unload slot {slot}: {BotControllerNative.LastLoadError}");
        }
        else
        {
            command.ReplyToCommand(ok
                ? $"dtr: unloaded slot {slot}"
                : $"dtr: cleared retained BotHider presentation for slot {slot}");
            if (ok && !string.IsNullOrWhiteSpace(BotControllerNative.LastLoadError))
                command.ReplyToCommand($"[DTR WARN] {BotControllerNative.LastLoadError}");
        }
    }
}
