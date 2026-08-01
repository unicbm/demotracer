/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool _playoffEnabled;

    [ConsoleCommand("dtr_playoff", "dtr_playoff <true|false>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void PlayoffCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand(
                $"[DTR OK] playoff={FormatOnOff(_playoffEnabled)} plan={FormatPlayoffPlanStatus()}");
            command.ReplyToCommand("usage: dtr_playoff <true|false>");
            return;
        }

        if (!TryParsePlayoffToggle(command.GetArg(1), out var enabled))
        {
            command.ReplyToCommand("usage: dtr_playoff <true|false>");
            return;
        }

        _playoffEnabled = enabled;
        if (!enabled)
            CancelPlayoffPreparation(unloadPrepared: true);

        command.ReplyToCommand(
            $"[DTR OK] playoff={FormatOnOff(_playoffEnabled)} plan={FormatPlayoffPlanStatus()}");
        if (enabled && string.IsNullOrWhiteSpace(_session.Plan.SequenceManifestPath))
        {
            command.ReplyToCommand(
                "[DTR HINT] playoff is enabled and will attach to the next manifest sequence.");
        }
        else if (!enabled)
        {
            command.ReplyToCommand(
                "[DTR OK] Future playoff scheduling is disabled; an already-live replay is not stopped.");
        }
    }

    private static bool TryParsePlayoffToggle(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "yes":
                enabled = true;
                return true;
            case "0":
            case "false":
            case "off":
            case "no":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }

    private bool IsPlayoffPlanReady()
    {
        return _playoffEnabled &&
               !_session.Plan.SequenceActive &&
               !string.IsNullOrWhiteSpace(_session.Plan.SequenceManifestPath) &&
               _session.Plan.SequenceRounds.Length > 0 &&
               _session.Plan.SequenceIndex >= _session.Plan.SequenceRounds.Length;
    }

    private bool HasPlayoffSchedulingState()
        => IsPlayoffPlanReady() || _session.Plan.PlayoffPreparePending || _session.Plan.PlayoffPrepared;

    private string FormatPlayoffPlanStatus()
    {
        if (_session.Plan.PlayoffPrepared)
            return $"prepared:T=r{_session.Plan.PlayoffPreparedTRound},CT=r{_session.Plan.PlayoffPreparedCtRound}";
        if (_session.Plan.PlayoffPreparePending)
            return $"decoding:T=r{_session.Plan.PlayoffPendingTRound},CT=r{_session.Plan.PlayoffPendingCtRound}";
        if (IsPlayoffPlanReady())
            return $"ready:extra_round={_session.Plan.PlayoffRoundIndex + 1}";
        if (_session.Plan.SequenceActive && _playoffEnabled)
            return "waiting_for_sequence_end";
        return "none";
    }

    private void ResetPlayoffProgress()
    {
        ClearPlayoffPendingPreparation(cancelDecode: true);
        _session.Plan.ClearPlayoffPrepared(resetRoundIndex: true);
    }

    private void CancelPlayoffPreparation(bool unloadPrepared)
    {
        ClearPlayoffPendingPreparation(cancelDecode: true);
        var hadPrepared = _session.Plan.ClearPlayoffPrepared(resetRoundIndex: false);
        if (!unloadPrepared || !hadPrepared)
            return;

        InvalidateFreezePreroll();
        StopAndUnloadLoaded(clearArmedPlan: false);
    }

    private void ClearPlayoffPendingPreparation(bool cancelDecode)
    {
        var wasPending = _session.Plan.ClearPlayoffPending();
        if (cancelDecode && wasPending)
            FinishReplayPrefetchRound();
    }

    private bool PrepareNextPlayoffRound(string prepareReason, bool allowLoad = true)
    {
        if (!IsPlayoffPlanReady())
            return false;
        if (_session.Plan.PlayoffPrepared)
            return true;
        if (_session.Plan.PlayoffPreparePending)
        {
            if (!allowLoad)
                return false;

            _session.Plan.PlayoffPendingCanLoad = true;
            if (ReplayPrefetchReady())
            {
                return CompletePendingPlayoffPreparation(
                    waitForDecode: false,
                    scheduleFreezePreroll: false);
            }

            PollPendingPlayoffPreparation(_session.Plan.PlayoffPrepareToken);
            return false;
        }

        var manifestPath = ResolveReadableManifestPath(_session.Plan.SequenceManifestPath);
        if (!TryGetPrefetchedManifest(manifestPath, out var manifest) &&
            !TryReadManifest(manifestPath, out manifest, out var readError))
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_session.Plan.PlayoffRoundIndex + 1}: failed to read manifest: {readError}");
            return false;
        }
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_session.Plan.PlayoffRoundIndex + 1}: map mismatch server={currentMap} manifest={manifest.Map}");
            return false;
        }

        var hasTRoster = TryGetPlayoffRosterSteamIds(
            CsTeam.Terrorist,
            out var tSteamIds,
            out var tRosterError);
        var hasCtRoster = TryGetPlayoffRosterSteamIds(
            CsTeam.CounterTerrorist,
            out var ctSteamIds,
            out var ctRosterError);
        if (!hasTRoster || !hasCtRoster)
        {
            var rosterError = !string.IsNullOrWhiteSpace(tRosterError) ? tRosterError : ctRosterError;
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_session.Plan.PlayoffRoundIndex + 1}: {rosterError}");
            return false;
        }
        if (tSteamIds.Count == 0 && ctSteamIds.Count == 0)
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_session.Plan.PlayoffRoundIndex + 1}: no replay bot targets");
            return false;
        }

        var hasTRound = TryChoosePlayoffSourceRound(
                manifest,
                "t",
                tSteamIds,
                out var tRound,
                out var tCandidateCount,
                out var tChooseError);
        var hasCtRound = TryChoosePlayoffSourceRound(
                manifest,
                "ct",
                ctSteamIds,
                out var ctRound,
                out var ctCandidateCount,
                out var ctChooseError);
        if (!hasTRound || !hasCtRound)
        {
            var chooseError = !string.IsNullOrWhiteSpace(tChooseError) ? tChooseError : ctChooseError;
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_session.Plan.PlayoffRoundIndex + 1}: {chooseError}");
            return false;
        }

        PrefetchPlayoffRoundReplays(
            manifestPath,
            manifest,
            tRound,
            ctRound,
            tSteamIds,
            ctSteamIds);
        _session.Plan.PlayoffPreparePending = true;
        _session.Plan.PlayoffPendingCanLoad = allowLoad;
        _session.Plan.PlayoffPendingTRound = tRound;
        _session.Plan.PlayoffPendingCtRound = ctRound;
        _session.Plan.PlayoffPendingReason =
            $"T=r{tRound} from {tCandidateCount} full-buy candidate(s), " +
            $"CT=r{ctRound} from {ctCandidateCount} full-buy candidate(s)";
        _session.Plan.PlayoffPendingPrepareReason = prepareReason;
        var token = ++_session.Plan.PlayoffPrepareToken;
        Server.PrintToConsole(
            $"dtr: playoff extra round {_session.Plan.PlayoffRoundIndex + 1} selected on {prepareReason}; " +
            $"{_session.Plan.PlayoffPendingReason}; decoding replay data off-thread");
        if (allowLoad)
            PollPendingPlayoffPreparation(token);
        return false;
    }

    private bool TryGetPlayoffRosterSteamIds(
        CsTeam team,
        out HashSet<ulong> steamIds,
        out string error)
    {
        steamIds = new HashSet<ulong>();
        error = string.Empty;
        var targets = FindReplayTargets().Where(bot => bot.Team == team).ToList();
        foreach (var bot in targets)
        {
            ulong steamId = 0;
            if (_session.LoadedReplays.TryGetValue(bot.Slot, out var loaded))
                steamId = loaded.SteamId;
            else if (_retainedBotHiderPresentation.TryGetValue(bot.Slot, out var retained))
                steamId = retained.SteamId;

            if (steamId == 0)
            {
                error = $"team={team} slot={bot.Slot} has no retained DTR SteamID evidence";
                return false;
            }
            if (!steamIds.Add(steamId))
            {
                error = $"team={team} has duplicate retained DTR SteamID {steamId}";
                return false;
            }
        }
        return true;
    }

    private static bool TryChoosePlayoffSourceRound(
        ConversionManifest manifest,
        string side,
        IReadOnlySet<ulong> steamIds,
        out int selectedRound,
        out int candidateCount,
        out string error)
    {
        selectedRound = -1;
        candidateCount = 0;
        error = string.Empty;
        if (steamIds.Count == 0)
            return true;

        var replaySteamIdsByRound = manifest.Files
            .Where(file => file.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => file.Round)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ulong>)group.Select(file => file.SteamId).ToArray());
        var candidates = PlayoffRoundSelectionPolicy.FindEligibleRounds(
            manifest.Rounds.Select(round => new PlayoffRoundCandidate(
                round.Round,
                round.PistolRound,
                side.Equals("t", StringComparison.OrdinalIgnoreCase)
                    ? round.TEconomy?.Class
                    : round.CtEconomy?.Class,
                replaySteamIdsByRound.TryGetValue(round.Round, out var replaySteamIds)
                    ? replaySteamIds
                    : Array.Empty<ulong>())),
            steamIds);
        candidateCount = candidates.Length;
        if (candidates.Length == 0)
        {
            error = $"side={side} has no full-buy source round covering every retained SteamID";
            return false;
        }

        selectedRound = candidates[Random.Shared.Next(candidates.Length)];
        return true;
    }

    private void PollPendingPlayoffPreparation(int token)
    {
        AddTimer(ReplayReadinessPollSeconds, () =>
        {
            if (!_session.Plan.PlayoffPreparePending || token != _session.Plan.PlayoffPrepareToken)
                return;
            if (!_session.Plan.PlayoffPendingCanLoad)
                return;
            if (!ReplayPrefetchReady())
            {
                PollPendingPlayoffPreparation(token);
                return;
            }

            _ = CompletePendingPlayoffPreparation(
                waitForDecode: false,
                scheduleFreezePreroll: true);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool CompletePendingPlayoffPreparation(
        bool waitForDecode,
        bool scheduleFreezePreroll)
    {
        if (!_session.Plan.PlayoffPreparePending)
            return _session.Plan.PlayoffPrepared;
        if (!waitForDecode && !ReplayPrefetchReady())
            return false;

        var tRound = _session.Plan.PlayoffPendingTRound;
        var ctRound = _session.Plan.PlayoffPendingCtRound;
        var reason = _session.Plan.PlayoffPendingReason;
        var prepareReason = _session.Plan.PlayoffPendingPrepareReason;
        ClearPlayoffPendingPreparation(cancelDecode: false);
        if (!IsPlayoffPlanReady())
            return false;

        var load = LoadPlayoffRound(_session.Plan.SequenceManifestPath, tRound, ctRound);
        if (!load.Ok)
        {
            Server.PrintToConsole(
                $"dtr: playoff failed extra round {_session.Plan.PlayoffRoundIndex + 1}: {load.Message}");
            return false;
        }

        PrepareLoadedReplayOwnership();
        _session.Plan.PlayoffPrepared = true;
        _session.Plan.PlayoffPreparedTRound = tRound;
        _session.Plan.PlayoffPreparedCtRound = ctRound;
        _session.Plan.PlayoffPreparedLabel = $"{reason}; {load.Message}";
        TryStartDtrRoundBanner($"playoff_t{tRound}_ct{ctRound}");
        Server.PrintToConsole(
            $"dtr: prepared playoff extra round {_session.Plan.PlayoffRoundIndex + 1} on {prepareReason} -> {_session.Plan.PlayoffPreparedLabel}");
        if (scheduleFreezePreroll &&
            TryReadFreezePhaseRemaining(out var freezeRemaining, out _) &&
            freezeRemaining > 0.0f)
        {
            ScheduleFreezePrerollStart($"playoff extra round {_session.Plan.PlayoffRoundIndex + 1}");
        }
        return true;
    }

    private void StartPreparedPlayoffRound()
    {
        var extraRound = _session.Plan.PlayoffRoundIndex + 1;
        if (!_session.Plan.PlayoffPrepared)
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped start for extra round {extraRound}: replay prefetch was not ready by round_freeze_end");
            ClearPlayoffPendingPreparation(cancelDecode: true);
            _session.Plan.PlayoffRoundIndex++;
            return;
        }

        var label = _session.Plan.PlayoffPreparedLabel;
        var play = StartLoaded(loop: false);
        Server.PrintToConsole(
            $"dtr: playoff extra round {extraRound} start on round_freeze_end -> {label}; {play}");
        _session.Plan.PlayoffRoundIndex++;
        _session.Plan.ClearPlayoffPrepared(resetRoundIndex: false);
        ReleaseUnusedWarmReplayBuffers();
    }
}
