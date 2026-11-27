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
    [ConsoleCommand("dtr_go", "dtr_go <seq|round> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void GoCommand(CCSPlayerController? player, CommandInfo command)
        => DispatchPlanCommand(command, "dtr_go", restart: true);

    [ConsoleCommand("dtr_arm", "dtr_arm <seq|round> ...")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ArmCommand(CCSPlayerController? player, CommandInfo command)
        => DispatchPlanCommand(command, "dtr_arm", restart: false);

    private void DispatchPlanCommand(CommandInfo command, string commandName, bool restart)
    {
        var archive = command.ArgCount > 2 ? command.GetArg(2) : null;
        void ReportProviderFailure(string detail)
        {
            if (!string.IsNullOrWhiteSpace(archive)) ReportPlaybackResult(ResolveReadableManifestPath(archive), "error", detail: detail);
        }
        if (!CheckAbi(command))
        {
            ReportProviderFailure(BotControllerNative.RuntimeSummary);
            return;
        }
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[DTR ERR] Missing mode. Usage: {commandName} <seq|round> ...");
            command.ReplyToCommand($"[DTR HINT] {commandName} seq <manifest_json> [from_source_round]");
            command.ReplyToCommand($"[DTR HINT] {commandName} round <manifest_json> <source_round>");
            return;
        }

        switch (command.GetArg(1).ToLowerInvariant())
        {
            case "seq":
            case "sequence":
                if (!EnsureHiderForCommand(command, onFailure: ReportProviderFailure)) return;
                RunManifestSequence(command, $"{commandName} seq", restart, argOffset: 2);
                return;
            case "round":
                if (!EnsureHiderForCommand(command, onFailure: ReportProviderFailure)) return;
                ArmSingleRound(command, $"{commandName} round", restart, argOffset: 2);
                return;
            default:
                command.ReplyToCommand("[DTR ERR] Ambiguous command. Choose a mode: seq or round.");
                command.ReplyToCommand($"[DTR HINT] Use \"{commandName} seq <manifest_json> 0\" for sequence playback.");
                command.ReplyToCommand($"[DTR HINT] Use \"{commandName} round <manifest_json> 0\" for single-round playback.");
                return;
        }
    }

    private void RunManifestSequence(
        CommandInfo command,
        string commandName,
        bool restart,
        int argOffset = 1)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount <= argOffset)
        {
            command.ReplyToCommand($"usage: {commandName} <replay.dtr> [from_source_round]");
            return;
        }

        var manifestPath = command.GetArg(argOffset);
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var hasManifestStampBefore = ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampBefore);
        if (!TryReadManifest(resolvedManifestPath, out var manifest, out var readError))
        {
            ReportPlaybackResult(resolvedManifestPath, "error", detail: readError);
            command.ReplyToCommand($"dtr: failed to read manifest: {readError}");
            return;
        }
        var stableManifestStamp = hasManifestStampBefore &&
                                  ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampAfter) &&
                                  manifestStampBefore == manifestStampAfter
            ? manifestStampAfter
            : (ReplayFileStamp?)null;
        if (!CheckManifestMap(command.ReplyToCommand, manifest.Map, resolvedManifestPath))
            return;

        var rounds = manifest.Files
            .Select(file => file.Round)
            .Distinct()
            .Order()
            .ToArray();

        if (rounds.Length == 0)
        {
            ReportPlaybackResult(resolvedManifestPath, "no_rounds");
            command.ReplyToCommand("dtr: manifest has no playable rounds");
            return;
        }

        var startRound = rounds[0];
        if (command.ArgCount > argOffset + 1 &&
            (!int.TryParse(command.GetArg(argOffset + 1), out startRound) || !rounds.Contains(startRound)))
        {
            ReportPlaybackResult(resolvedManifestPath, "round_missing", detail: command.GetArg(argOffset + 1));
            command.ReplyToCommand($"[DTR ERR] from_source_round={command.GetArg(argOffset + 1)} was not found in manifest.");
            command.ReplyToCommand($"[DTR HINT] Available source rounds: {string.Join(", ", rounds)}.");
            return;
        }

        var missingRound = ReplaySequenceContinuityPolicy.FindFirstMissingRound(rounds, startRound);
        if (missingRound.HasValue)
        {
            ReportPlaybackResult(resolvedManifestPath, "sequence_gap", missingRound.Value);
            command.ReplyToCommand(
                $"[DTR ERR] Sequence cannot cross missing source_round={missingRound.Value}; its replay files were not exported.");
            command.ReplyToCommand(
                "[DTR HINT] Use single-round playback, or reconvert a contiguous source-round range.");
            return;
        }

        if (!CheckReplayStartGates(
                message => command.ReplyToCommand(message),
                stopCurrentForOverride: true,
                deferStopUntilRoundStart: restart))
        {
            ReportPlaybackResult(resolvedManifestPath, "warmup", startRound);
            return;
        }

        CancelSoulChallenge("replaced_by_sequence");
        ActivatePendingReplayRetentionPriority();
        if (!restart)
            StopAndUnloadLoaded();
        CancelReplayPrefetch();
        ResetPlayoffProgress();
        _session.Plan.SequenceManifestPath = manifestPath;
        _session.Plan.SequenceRounds = rounds;
        _session.Plan.SequenceIndex = Array.IndexOf(rounds, startRound);
        _session.Plan.SequenceActive = _session.Plan.SequenceIndex >= 0;
        _session.Plan.SequencePrepared = false;
        _session.Plan.SequencePreparedRound = -1;
        _session.Plan.SequencePreparePollToken++;
        InvalidateFreezePreroll();
        _session.Plan.ClearArmed();
        PrefetchRoundReplays(manifestPath, manifest, startRound, stableManifestStamp);

        command.ReplyToCommand(
            restart
                ? $"[DTR OK] Planned SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; restart=now."
                : $"[DTR OK] Armed SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; waiting for next round_prestart.");
        command.ReplyToCommand(
            $"[DTR OK] Sequence has {rounds.Length - _session.Plan.SequenceIndex} round(s) remaining from source_round={startRound}.");
        IssueRestartIfRequested(command, restart);
    }

    private void ArmSingleRound(
        CommandInfo command,
        string commandName,
        bool restart,
        int argOffset = 1)
    {
        if (!TryParseRoundArgs(command, commandName, out var manifestPath, out var round, argOffset))
            return;

        var loop = command.ArgCount > argOffset + 2 && command.GetArg(argOffset + 2) != "0";
        PlanSingleRound(
            commandName,
            manifestPath,
            round,
            loop,
            restart,
            message => command.ReplyToCommand(message));
    }

    private bool PlanSingleRound(
        string commandName,
        string manifestPath,
        int round,
        bool loop,
        bool restart,
        Action<string> reply)
    {
        if (!BotControllerNative.IsCompatible)
        {
            reply($"dtr: ABI mismatch; {BotControllerNative.RuntimeSummary}");
            return false;
        }
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var hasManifestStampBefore = ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampBefore);
        if (!TryReadManifest(resolvedManifestPath, out var manifest, out var readError))
        {
            ReportPlaybackResult(resolvedManifestPath, "error", round, readError);
            reply($"[DTR ERR] failed to read manifest: {readError}");
            return false;
        }
        var stableManifestStamp = hasManifestStampBefore &&
                                  ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampAfter) &&
                                  manifestStampBefore == manifestStampAfter
            ? manifestStampAfter
            : (ReplayFileStamp?)null;
        if (!CheckManifestMap(reply, manifest.Map, resolvedManifestPath))
            return false;
        if (!ManifestContainsSourceRound(manifest, round, out var validateError))
        {
            ReportPlaybackResult(resolvedManifestPath, "round_missing", round);
            reply(validateError);
            return false;
        }

        if (!CheckReplayStartGates(
                reply,
                stopCurrentForOverride: true,
                deferStopUntilRoundStart: restart))
        {
            ReportPlaybackResult(resolvedManifestPath, "warmup", round);
            return false;
        }

        CancelSoulChallenge("replaced_by_single_round");
        ActivatePendingReplayRetentionPriority();
        if (!restart)
            StopAndUnloadLoaded();
        CancelReplayPrefetch();
        _session.Plan.ClearSequence();
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        _session.Plan.Armed = true;
        _session.Plan.ArmedLoop = loop;
        _session.Plan.ArmedPrepared = false;
        _session.Plan.ArmedPreparePollToken++;
        _session.Plan.ArmedManifestPath = manifestPath;
        _session.Plan.ArmedSourceRound = round;
        _session.Plan.ArmedLabel = $"source_round={round} manifest={manifestPath}";
        PrefetchRoundReplays(manifestPath, manifest, round, stableManifestStamp);
        reply(
            restart
                ? $"[DTR OK] Planned SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; restart=now."
                : $"[DTR OK] Armed SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; waiting for next round_prestart.");
        reply("[DTR OK] This plan will not advance to later manifest rounds.");
        IssueRestartIfRequested(restart, reply);
        return true;
    }

    private bool PrepareNextSequenceRound(
        string reason,
        bool switchingTeamsAtRoundReset = false)
    {
        if (_session.Plan.SequenceIndex < 0 || _session.Plan.SequenceIndex >= _session.Plan.SequenceRounds.Length)
        {
            _session.Plan.SequenceActive = false;
            Server.PrintToConsole("dtr: sequence complete");
            return false;
        }

        if (_session.Plan.SequencePrepared)
            return true;

        var round = _session.Plan.SequenceRounds[_session.Plan.SequenceIndex];
        if (!ReplayPrefetchReady())
        {
            PollPendingSequenceRestart(round, reason);
            Server.PrintToConsole(
                $"dtr: sequence round {round} is still decoding on round_prestart; keeping it armed for the next server round");
            return false;
        }

        var sequencePath = _session.Plan.SequenceManifestPath;
        var load = LoadRound(
            sequencePath,
            round,
            switchingTeamsAtRoundReset,
            sequence: true);
        if (!load.Ok)
        {
            _session.Plan.SequencePrepared = false;
            _session.Plan.SequencePreparedRound = -1;
            ReportPlaybackResult(ResolveReadableManifestPath(sequencePath), "error", round, load.Message);
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {round} could not be prepared on {reason}; " +
                $"keeping it armed for the next round_prestart: {load.Message}");
            return false;
        }

        PrepareLoadedReplayOwnership();
        _session.Plan.SequencePrepared = true;
        _session.Plan.SequenceNativeOnly = _session.LoadedSlots.Count == 0;
        _session.Plan.SequencePreparedRound = round;
        Server.PrintToConsole($"dtr: prepared sequence round {round} on {reason}: {load.Message}");
        return true;
    }

    private void PollPendingSequenceRestart(int round, string reason)
    {
        var token = ++_session.Plan.SequencePreparePollToken;
        void Poll()
        {
            AddOwnedTimer(ReplayReadinessPollSeconds, () =>
            {
                if (token != _session.Plan.SequencePreparePollToken ||
                    !_session.Plan.SequenceActive ||
                    _session.Plan.SequencePrepared ||
                    _session.Plan.SequenceIndex < 0 ||
                    _session.Plan.SequenceIndex >= _session.Plan.SequenceRounds.Length ||
                    _session.Plan.SequenceRounds[_session.Plan.SequenceIndex] != round)
                {
                    return;
                }
                if (!TryReadFreezePhaseRemaining(out _, out _))
                    return;
                if (!ReplayPrefetchReady())
                {
                    Poll();
                    return;
                }

                // The current pawn inventory already exists. Restart instead of
                // loading late so round_prestart can submit the plan before the
                // next GiveNamedItem construction.
                _session.Plan.SequencePreparePollToken++;
                Server.PrintToConsole(
                    $"dtr: sequence round {round} finished decoding after spawn on {reason}; restarting once for spawn-safe preparation");
                Server.ExecuteCommand("mp_restartgame 1");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        Poll();
    }

    private bool PrepareArmedRound(
        string reason,
        bool switchingTeamsAtRoundReset = false)
    {
        if (!_session.Plan.Armed)
            return false;
        if (_session.Plan.ArmedPrepared)
            return true;
        if (string.IsNullOrWhiteSpace(_session.Plan.ArmedManifestPath) || _session.Plan.ArmedSourceRound < 0)
        {
            _session.Plan.ClearArmed();
            Server.PrintToConsole("[DTR ERR] single-round plan is missing manifest/source_round");
            return false;
        }

        var manifestPath = _session.Plan.ArmedManifestPath;
        var sourceRound = _session.Plan.ArmedSourceRound;
        var loop = _session.Plan.ArmedLoop;
        var label = _session.Plan.ArmedLabel;
        if (!ReplayPrefetchReady())
        {
            PollPendingArmedRestart(manifestPath, sourceRound, reason);
            Server.PrintToConsole(
                $"dtr: single source_round={sourceRound} is still decoding on round_prestart; keeping it armed for the next server round");
            return false;
        }

        var load = LoadRound(manifestPath, sourceRound, switchingTeamsAtRoundReset);
        if (!load.Ok)
        {
            _session.Plan.ClearArmed();
            ReportPlaybackResult(ResolveReadableManifestPath(manifestPath), "error", sourceRound, load.Message);
            Server.PrintToConsole($"[DTR ERR] single source_round={sourceRound} failed while preparing on {reason}: {load.Message}");
            return false;
        }

        _session.Plan.Armed = true;
        _session.Plan.ArmedPrepared = true;
        _session.Plan.ArmedManifestPath = manifestPath;
        _session.Plan.ArmedSourceRound = sourceRound;
        _session.Plan.ArmedLoop = loop;
        _session.Plan.ArmedLabel = label;
        PrepareLoadedReplayOwnership();
        Server.PrintToConsole($"[DTR OK] round_prestart: loaded SINGLE source_round={sourceRound} on {reason}: {load.Message}");
        return true;
    }

    private void PollPendingArmedRestart(
        string manifestPath,
        int sourceRound,
        string reason)
    {
        var token = ++_session.Plan.ArmedPreparePollToken;
        void Poll()
        {
            AddOwnedTimer(ReplayReadinessPollSeconds, () =>
            {
                if (token != _session.Plan.ArmedPreparePollToken ||
                    !_session.Plan.Armed ||
                    _session.Plan.ArmedPrepared ||
                    _session.Plan.ArmedSourceRound != sourceRound ||
                    !_session.Plan.ArmedManifestPath.Equals(
                        manifestPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (!TryReadFreezePhaseRemaining(out _, out _))
                    return;
                if (!ReplayPrefetchReady())
                {
                    Poll();
                    return;
                }

                _session.Plan.ArmedPreparePollToken++;
                Server.PrintToConsole(
                    $"dtr: single source_round={sourceRound} finished decoding after spawn on {reason}; restarting once for spawn-safe preparation");
                Server.ExecuteCommand("mp_restartgame 1");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        Poll();
    }

    private void StartPreparedSequenceRound()
    {
        if (!_session.Plan.SequencePrepared)
        {
            var pendingRound = _session.Plan.SequenceIndex >= 0 && _session.Plan.SequenceIndex < _session.Plan.SequenceRounds.Length
                ? _session.Plan.SequenceRounds[_session.Plan.SequenceIndex]
                : -1;
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {pendingRound} was not prepared by round_freeze_end; " +
                "skipping this server round and keeping the sequence armed for the next round_prestart");
            return;
        }

        var round = _session.Plan.SequencePreparedRound;
        if (_session.Plan.SequenceNativeOnly)
        {
            ReportPlaybackResult(ResolveReadableManifestPath(_session.Plan.SequenceManifestPath), "native_only", round);
            AdvanceSequenceRound();
            return;
        }
        var play = StartLoaded(loop: false, started => CompleteSequenceRoundStart(round, started));
        Server.PrintToConsole($"dtr: sequence round {round} start on round_freeze_end: {play}");
    }

    private void CompleteSequenceRoundStart(int round, int started)
    {
        if (started <= 0)
            return;
        RecordStartedPlayoffSources(round, round);
        AdvanceSequenceRound();
    }

    private void AdvanceSequenceRound()
    {
        _session.Plan.SequencePrepared = false;
        _session.Plan.SequenceNativeOnly = false;
        _session.Plan.SequencePreparedRound = -1;
        _session.Plan.SequenceIndex++;
        if (_session.Plan.SequenceIndex >= _session.Plan.SequenceRounds.Length)
        {
            _session.Plan.SequenceActive = false;
            Server.PrintToConsole(
                _playoffEnabled
                    ? "dtr: sequence complete; playoff continuation is armed"
                    : "dtr: sequence complete");
            if (_playoffEnabled)
            {
                _ = PrepareNextPlayoffRound(
                    "final sequence round live prefetch",
                    allowLoad: false);
            }
        }
        else
        {
            // Prefetch during live playback; round_end can leave too little decode time.
            PrefetchRoundReplays(_session.Plan.SequenceManifestPath, _session.Plan.SequenceRounds[_session.Plan.SequenceIndex]);
        }
    }

    private void StopSequenceState()
    {
        var hadSequencePrefetch = _session.Plan.SequenceActive || _session.Plan.SequencePrepared ||
                                  _session.Plan.PlayoffPreparePending || _session.Plan.PlayoffPrepared;
        CancelPlayoffPreparation(unloadPrepared: true);
        _session.Plan.ClearSequence();
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        if (hadSequencePrefetch)
        {
            CancelReplayPrefetch();
            ReleaseUnusedWarmReplayBuffers();
        }
    }

}
