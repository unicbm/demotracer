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

    [ConsoleCommand("dtr_seq_restart", "dtr_seq_restart <manifest.json> [from_source_round]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void SequenceRestartCommand(CCSPlayerController? player, CommandInfo command)
        => RunManifestSequence(command, "dtr_seq_restart", restart: true);

    [ConsoleCommand("dtr_round_restart", "dtr_round_restart <manifest.json> <source_round>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RoundRestartCommand(CCSPlayerController? player, CommandInfo command)
        => ArmSingleRound(command, "dtr_round_restart", restart: true);

    [ConsoleCommand("dtr_run_manifest", "dtr_run_manifest <manifest.json> [from_source_round]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RunManifestCommand(CCSPlayerController? player, CommandInfo command)
        => RunManifestSequence(command, "dtr_run_manifest", restart: false);

    [ConsoleCommand("dtr_stop_sequence", "dtr_stop_sequence")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void StopSequenceCommand(CCSPlayerController? player, CommandInfo command)
    {
        StopSequenceState();
        command.ReplyToCommand("dtr: sequence stopped");
    }

    [ConsoleCommand("dtr_arm_round", "dtr_arm_round <manifest.json> <source_round> [loop:0|1]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void ArmRoundCommand(CCSPlayerController? player, CommandInfo command)
        => ArmSingleRound(command, "dtr_arm_round", restart: false);

    private void DispatchPlanCommand(CommandInfo command, string commandName, bool restart)
    {
        if (!CheckAbi(command))
            return;
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
                RunManifestSequence(command, $"{commandName} seq", restart, argOffset: 2);
                return;
            case "round":
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
            command.ReplyToCommand($"usage: {commandName} <manifest.json> [from_source_round]");
            return;
        }

        var manifestPath = command.GetArg(argOffset);
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var hasManifestStampBefore = ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampBefore);
        if (!TryReadManifest(resolvedManifestPath, out var manifest, out var readError))
        {
            command.ReplyToCommand($"dtr: failed to read manifest: {readError}");
            return;
        }
        var stableManifestStamp = hasManifestStampBefore &&
                                  ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampAfter) &&
                                  manifestStampBefore == manifestStampAfter
            ? manifestStampAfter
            : (ReplayFileStamp?)null;
        if (!CheckManifestMap(command, manifest.Map, manifestPath))
            return;

        var rounds = manifest.Files
            .Select(file => file.Round)
            .Distinct()
            .Order()
            .ToArray();

        if (rounds.Length == 0)
        {
            command.ReplyToCommand("dtr: manifest has no playable rounds");
            return;
        }

        var startRound = rounds[0];
        if (command.ArgCount > argOffset + 1 &&
            (!int.TryParse(command.GetArg(argOffset + 1), out startRound) || !rounds.Contains(startRound)))
        {
            command.ReplyToCommand($"[DTR ERR] from_source_round={command.GetArg(argOffset + 1)} was not found in manifest.");
            command.ReplyToCommand($"[DTR HINT] Available source rounds: {string.Join(", ", rounds)}.");
            return;
        }

        var missingRound = ReplaySequenceContinuityPolicy.FindFirstMissingRound(rounds, startRound);
        if (missingRound.HasValue)
        {
            command.ReplyToCommand(
                $"[DTR ERR] Sequence cannot cross missing source_round={missingRound.Value}; its replay files were not exported.");
            command.ReplyToCommand(
                "[DTR HINT] Use single-round playback, or reconvert a contiguous source-round range.");
            return;
        }

        var deferExistingReplayCleanup =
            ReplayPlanOverridePolicy.DeferExistingReplayCleanupUntilRoundStart(restart);
        if (!CheckReplayStartGates(
                message => command.ReplyToCommand(message),
                stopCurrentForOverride: true,
                deferStopUntilRoundStart: deferExistingReplayCleanup))
            return;

        ActivatePendingReplayRetentionPriority();
        if (!deferExistingReplayCleanup)
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

    private void PlanSingleRound(
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
            return;
        }
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var hasManifestStampBefore = ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampBefore);
        if (!TryReadManifest(resolvedManifestPath, out var manifest, out var readError))
        {
            reply($"[DTR ERR] failed to read manifest: {readError}");
            return;
        }
        var stableManifestStamp = hasManifestStampBefore &&
                                  ReplayFileStamp.TryRead(resolvedManifestPath, out var manifestStampAfter) &&
                                  manifestStampBefore == manifestStampAfter
            ? manifestStampAfter
            : (ReplayFileStamp?)null;
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            reply($"[DTR ERR] map mismatch: server=\"{currentMap}\" manifest=\"{manifest.Map}\" path=\"{manifestPath}\"");
            return;
        }
        if (!ManifestContainsSourceRound(manifest, round, out var validateError))
        {
            reply(validateError);
            return;
        }

        var deferExistingReplayCleanup =
            ReplayPlanOverridePolicy.DeferExistingReplayCleanupUntilRoundStart(restart);
        if (!CheckReplayStartGates(
                reply,
                stopCurrentForOverride: true,
                deferStopUntilRoundStart: deferExistingReplayCleanup))
            return;

        ActivatePendingReplayRetentionPriority();
        if (!deferExistingReplayCleanup)
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

        var load = LoadRound(
            _session.Plan.SequenceManifestPath,
            round,
            switchingTeamsAtRoundReset);
        if (!load.Ok)
        {
            _session.Plan.SequencePrepared = false;
            _session.Plan.SequencePreparedRound = -1;
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {round} could not be prepared on {reason}; " +
                $"keeping it armed for the next round_prestart: {load.Message}");
            return false;
        }

        PrepareLoadedReplayOwnership();
        _session.Plan.SequencePrepared = true;
        _session.Plan.SequencePreparedRound = round;
        TryStartDtrRoundBanner($"sequence_r{round}");
        Server.PrintToConsole($"dtr: prepared sequence round {round} on {reason}: {load.Message}");
        return true;
    }

    private void PollPendingSequenceRestart(int round, string reason)
    {
        var token = ++_session.Plan.SequencePreparePollToken;
        void Poll()
        {
            AddTimer(ReplayReadinessPollSeconds, () =>
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
        TryStartDtrRoundBanner($"single_r{sourceRound}");
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
            AddTimer(ReplayReadinessPollSeconds, () =>
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
        var play = StartLoaded(loop: false);
        Server.PrintToConsole($"dtr: sequence round {round} start on round_freeze_end: {play}");

        _session.Plan.SequencePrepared = false;
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
            // Decode the next source round while the current replay is live.
            // Waiting until round_end leaves only the post-round/freeze window,
            // which is too short for some long v8 replay sets and can let the
            // next round enter freeze time without buy suppression or pre-roll.
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
