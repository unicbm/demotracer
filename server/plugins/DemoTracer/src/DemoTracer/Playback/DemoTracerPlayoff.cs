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
    private readonly PlayoffRoundHistory _playoffHistory = new();
    private PlayoffRoundSources? _loadedRoundSources;
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

        if (!TryParseBooleanToggle(command.GetArg(1), out var enabled))
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

    private static bool TryParseBooleanToggle(string value, out bool enabled)
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

    private bool ShouldRetainLoadedPlayoffFallback(bool switchingTeamsAtRoundReset)
        => PlayoffReplayFallbackPolicy.ShouldRetainLoadedReplay(
            planReady: IsPlayoffPlanReady(),
            prepared: _session.Plan.PlayoffPrepared,
            preparationPending: _session.Plan.PlayoffPreparePending,
            prefetchReady: PlayoffPrefetchReady(switchingTeamsAtRoundReset),
            hasLoadedReplay: _session.LoadedSlots.Count > 0,
            switchingTeamsAtRoundReset: switchingTeamsAtRoundReset);

    private string FormatPlayoffPlanStatus()
    {
        if (_session.Plan.PlayoffPrepared)
            return $"prepared:T=r{_session.Plan.PlayoffPreparedTRound},CT=r{_session.Plan.PlayoffPreparedCtRound}";
        if (_session.Plan.PlayoffPreparePending)
            return "decoding:available side assignments";
        if (IsPlayoffPlanReady())
            return $"ready:extra_round={_session.Plan.PlayoffRoundIndex + 1}";
        if (_session.Plan.SequenceActive && _playoffEnabled)
            return "waiting_for_sequence_end";
        return "none";
    }

    private void ResetPlayoffProgress()
    {
        _playoffHistory.Clear();
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

    private bool PrepareNextPlayoffRound(
        string prepareReason,
        bool allowLoad = true,
        bool switchingTeamsAtRoundReset = false)
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
            return CompletePendingPlayoffPreparation(switchingTeamsAtRoundReset);
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
            switchingTeamsAtRoundReset,
            out var tSteamIds,
            out var tRosterError);
        var hasCtRoster = TryGetPlayoffRosterSteamIds(
            CsTeam.CounterTerrorist,
            switchingTeamsAtRoundReset,
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
        var hasSameSides = TryChoosePlayoffSources(manifest, tSteamIds, ctSteamIds, out var sameSides, out var sameError);
        var hasSwappedSides = TryChoosePlayoffSources(manifest, ctSteamIds, tSteamIds, out var swappedSides, out var swappedError);
        if (!hasSameSides && !hasSwappedSides)
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_session.Plan.PlayoffRoundIndex + 1}: same sides: {sameError}; swapped sides: {swappedError}");
            return false;
        }

        // The next round's swap flag is not known at live prefetch time.
        // Decode available orientations now; choose by retained roster identity at
        // round_prestart, using the same upcoming-team rule as bot assignment.
        var sources = new PlayoffSourceSelection(tSteamIds, ctSteamIds,
            hasSameSides ? sameSides : null, hasSwappedSides ? swappedSides : null);
        PrefetchPlayoffRoundReplays(manifestPath, manifest, sources);
        _session.Plan.PlayoffPreparePending = true;
        _session.Plan.PlayoffPendingCanLoad = allowLoad;
        _session.Plan.PlayoffPendingSources = sources;
        _session.Plan.PlayoffPendingPrepareReason = prepareReason;
        _session.Plan.PlayoffPrepareToken++;
        Server.PrintToConsole(
            $"dtr: playoff extra round {_session.Plan.PlayoffRoundIndex + 1} selected on {prepareReason}; " +
            $"same sides: {(hasSameSides ? sameSides.Reason : sameError)}; " +
            $"swapped sides: {(hasSwappedSides ? swappedSides.Reason : swappedError)}; decoding replay data off-thread");
        return false;
    }

    private bool TryGetPlayoffRosterSteamIds(
        CsTeam team,
        bool switchingTeamsAtRoundReset,
        out HashSet<ulong> steamIds,
        out string error)
    {
        steamIds = new HashSet<ulong>();
        error = string.Empty;
        var targets = FindPlayoffReplayTargets().Where(bot => ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(
            bot.Team, switchingTeamsAtRoundReset) == team).ToList();
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

    private bool TryChoosePlayoffSources(
        ConversionManifest manifest,
        IReadOnlySet<ulong> tSteamIds,
        IReadOnlySet<ulong> ctSteamIds,
        out PlayoffRoundSources sources,
        out string error)
    {
        sources = default;
        if (!TryChoosePlayoffSourceRound(manifest, "t", tSteamIds,
                out var tRound, out var tCount, out error) ||
            !TryChoosePlayoffSourceRound(manifest, "ct", ctSteamIds,
                out var ctRound, out var ctCount, out error))
            return false;

        sources = new PlayoffRoundSources(tRound, ctRound,
            $"T=r{tRound} from {tCount} candidate(s), CT=r{ctRound} from {ctCount} candidate(s)");
        return true;
    }

    private bool TryChoosePlayoffSourceRound(
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

        var candidates = FindEligiblePlayoffRounds(manifest, side, steamIds);
        candidateCount = candidates.Length;
        if (candidates.Length == 0)
        {
            error = $"side={side} has no non-pistol source round with known loadouts covering every retained SteamID";
            return false;
        }

        selectedRound = _playoffHistory.Choose(side, steamIds, candidates);
        return true;
    }

    private void RecordStartedPlayoffSources(int tRound, int ctRound)
    {
        if (_session.ReplaySlots.PlayingCount == 0)
            return;
        foreach (var side in new[] { (Team: CsTeam.Terrorist, Name: "t", Round: tRound),
                                     (Team: CsTeam.CounterTerrorist, Name: "ct", Round: ctRound) })
        {
            var players = _session.LoadedReplays.Where(pair => pair.Value.ManifestTeam == side.Team).ToArray();
            if (players.Any(pair => _session.ReplaySlots.IsPlaying(pair.Key)))
                _playoffHistory.Record(side.Name, players.Select(pair => pair.Value.SteamId).ToHashSet(), side.Round);
        }
    }

    private static int[] FindEligiblePlayoffRounds(
        ConversionManifest manifest,
        string side,
        IReadOnlySet<ulong> steamIds)
    {
        var replayPlayersByRound = manifest.Files
            .Where(file => file.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => file.Round)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PlayoffPlayerLoadout>)group.Select(file =>
                    new PlayoffPlayerLoadout(file.SteamId, file.Loadout?.WeaponDefIndices)).ToArray());
        return PlayoffRoundSelectionPolicy.FindEligibleRounds(
            manifest.Rounds.Select(round => new PlayoffRoundCandidate(
                round.Round,
                round.PistolRound,
                replayPlayersByRound.TryGetValue(round.Round, out var replayPlayers)
                    ? replayPlayers
                    : Array.Empty<PlayoffPlayerLoadout>())),
            steamIds);
    }

    private bool CompletePendingPlayoffPreparation(bool switchingTeamsAtRoundReset)
    {
        if (!_session.Plan.PlayoffPreparePending)
            return _session.Plan.PlayoffPrepared;

        var pending = _session.Plan.PlayoffPendingSources;
        if (!TryGetPlayoffRosterSteamIds(CsTeam.Terrorist, switchingTeamsAtRoundReset,
                out var tSteamIds, out var rosterError) ||
            !TryGetPlayoffRosterSteamIds(CsTeam.CounterTerrorist, switchingTeamsAtRoundReset,
                out var ctSteamIds, out rosterError))
        {
            Server.PrintToConsole($"[DTR ERR] playoff upcoming roster unavailable: {rosterError}");
            return false;
        }
        if (pending == null || !pending.TryResolve(tSteamIds, ctSteamIds, out var sources, out var swapped))
        {
            ClearPlayoffPendingPreparation(cancelDecode: true);
            Server.PrintToConsole("[DTR WARN] playoff has no prefetched source for the upcoming roster/sides; selecting new sources");
            return PrepareNextPlayoffRound("roster changed", switchingTeamsAtRoundReset: switchingTeamsAtRoundReset);
        }
        if (!PlayoffOrientationPrefetchReady(swapped))
            return false;
        // The unused orientation must not mask a ready entry in the selected cache.
        (swapped ? _dtrReplayPrefetch : _playoffSwappedReplayPrefetch).Cancel();
        var tRound = sources.TRound;
        var ctRound = sources.CtRound;
        var reason = sources.Reason;
        var prepareReason = _session.Plan.PlayoffPendingPrepareReason;
        ClearPlayoffPendingPreparation(cancelDecode: false);
        if (!IsPlayoffPlanReady())
            return false;

        var load = LoadPlayoffRound(
            _session.Plan.SequenceManifestPath,
            tRound,
            ctRound,
            switchingTeamsAtRoundReset);
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
        return true;
    }

    private bool PlayoffPrefetchReady(bool switchingTeamsAtRoundReset)
        => _session.Plan.PlayoffPendingSources is { } pending &&
           TryGetPlayoffRosterSteamIds(CsTeam.Terrorist, switchingTeamsAtRoundReset, out var tRoster, out _) &&
           TryGetPlayoffRosterSteamIds(CsTeam.CounterTerrorist, switchingTeamsAtRoundReset, out var ctRoster, out _) &&
           pending.TryResolve(tRoster, ctRoster, out _, out var swapped) &&
           PlayoffOrientationPrefetchReady(swapped);

    private bool PlayoffOrientationPrefetchReady(bool swapped)
        => (swapped ? _playoffSwappedReplayPrefetch : _dtrReplayPrefetch).AllPendingCompleted();

    private void StartPreparedPlayoffRound()
    {
        var extraRound = _session.Plan.PlayoffRoundIndex + 1;
        if (!_session.Plan.PlayoffPrepared)
        {
            if (_session.LoadedSlots.Count > 0)
            {
                // Reuse the loaded round while the next source is decoding.
                _session.Plan.PlayoffPendingCanLoad = false;
                PrepareLoadedReplayOwnership();
                var fallback = StartLoaded(loop: false, CompletePlayoffRoundStart);
                Server.PrintToConsole(
                    $"dtr: playoff extra round {extraRound} reused the last loaded DTR while the next source finishes decoding; {fallback}");
                return;
            }

            _session.Plan.PlayoffPendingCanLoad = false;
            Server.PrintToConsole(
                $"[DTR ERR] playoff extra round {extraRound} has neither a prepared replay nor a loaded DTR fallback; keeping the source selection armed");
            return;
        }

        var label = _session.Plan.PlayoffPreparedLabel;
        var play = StartLoaded(loop: false, CompletePlayoffRoundStart);
        Server.PrintToConsole(
            $"dtr: playoff extra round {extraRound} start on round_freeze_end -> {label}; {play}");
    }

    private void CompletePlayoffRoundStart(int started)
    {
        if (!_session.Plan.TryCompletePlayoffStart(started))
            return;
        if (_loadedRoundSources is { } sources)
            RecordStartedPlayoffSources(sources.TRound, sources.CtRound);
        ReleaseUnusedWarmReplayBuffers();
        _ = PrepareNextPlayoffRound(
            $"playoff extra round {_session.Plan.PlayoffRoundIndex} live prefetch",
            allowLoad: false);
    }
}

internal static class PlayoffReplayFallbackPolicy
{
    internal static bool ShouldRetainLoadedReplay(
        bool planReady,
        bool prepared,
        bool preparationPending,
        bool prefetchReady,
        bool hasLoadedReplay,
        bool switchingTeamsAtRoundReset = false)
        => planReady &&
           !switchingTeamsAtRoundReset &&
           !prepared &&
           hasLoadedReplay &&
           (!preparationPending || !prefetchReady);
}
