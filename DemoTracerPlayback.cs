using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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

        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: true))
            return;

        ActivatePendingReplayRetentionPriority();
        StopAndUnloadLoaded();
        CancelReplayPrefetch();
        ResetPlayoffProgress();
        _session.SequenceManifestPath = manifestPath;
        _session.SequenceRounds = rounds;
        _session.SequenceIndex = Array.IndexOf(rounds, startRound);
        _session.SequenceActive = _session.SequenceIndex >= 0;
        _session.SequencePrepared = false;
        _session.SequencePreparedRound = -1;
        _session.SequencePreparePollToken++;
        InvalidateFreezePreroll();
        _session.Armed = false;
        _session.ArmedPrepared = false;
        _session.ArmedPreparePollToken++;
        _session.ArmedManifestPath = string.Empty;
        _session.ArmedSourceRound = -1;
        PrefetchRoundReplays(manifestPath, manifest, startRound, stableManifestStamp);

        command.ReplyToCommand(
            restart
                ? $"[DTR OK] Planned SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; restart=now."
                : $"[DTR OK] Armed SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; waiting for next round_start.");
        command.ReplyToCommand(
            $"[DTR OK] Sequence has {rounds.Length - _session.SequenceIndex} round(s) remaining from source_round={startRound}.");
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

        if (!CheckReplayStartGates(reply, stopCurrentForOverride: true))
            return;

        ActivatePendingReplayRetentionPriority();
        StopAndUnloadLoaded();
        CancelReplayPrefetch();
        _session.SequenceActive = false;
        _session.SequenceManifestPath = string.Empty;
        _session.SequenceRounds = [];
        _session.SequenceIndex = 0;
        _session.SequencePrepared = false;
        _session.SequencePreparedRound = -1;
        _session.SequencePreparePollToken++;
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        _session.Armed = true;
        _session.ArmedLoop = loop;
        _session.ArmedPrepared = false;
        _session.ArmedPreparePollToken++;
        _session.ArmedManifestPath = manifestPath;
        _session.ArmedSourceRound = round;
        _session.ArmedLabel = $"source_round={round} manifest={manifestPath}";
        PrefetchRoundReplays(manifestPath, manifest, round, stableManifestStamp);
        reply(
            restart
                ? $"[DTR OK] Planned SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; restart=now."
                : $"[DTR OK] Armed SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; waiting for next round_start.");
        reply("[DTR OK] This plan will not advance to later manifest rounds.");
        IssueRestartIfRequested(restart, reply);
    }

    private bool PrepareNextSequenceRound(
        string reason,
        bool pollIfPending = true)
    {
        if (_session.SequenceIndex < 0 || _session.SequenceIndex >= _session.SequenceRounds.Length)
        {
            _session.SequenceActive = false;
            Server.PrintToConsole("dtr: sequence complete");
            return false;
        }

        if (_session.SequencePrepared)
            return true;

        var round = _session.SequenceRounds[_session.SequenceIndex];
        if (!ReplayPrefetchReady())
        {
            if (pollIfPending)
                PollPendingSequencePreparation(round, reason);
            Server.PrintToConsole(
                $"dtr: sequence round {round} is still decoding off-thread; the game thread will not wait");
            return false;
        }

        var load = LoadRound(_session.SequenceManifestPath, round);
        if (!load.Ok)
        {
            _session.SequencePrepared = false;
            _session.SequencePreparedRound = -1;
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {round} could not be prepared on {reason}; " +
                $"keeping it armed for the next round_start: {load.Message}");
            return false;
        }

        PreloadLoadedReplays();
        _session.SequencePrepared = true;
        _session.SequencePreparedRound = round;
        TryStartDtrRoundBanner($"sequence_r{round}");
        Server.PrintToConsole($"dtr: prepared sequence round {round} on {reason}: {load.Message}");
        return true;
    }

    private void PollPendingSequencePreparation(int round, string reason)
    {
        var token = ++_session.SequencePreparePollToken;
        void Poll()
        {
            Server.NextFrame(() =>
            {
                if (token != _session.SequencePreparePollToken ||
                    !_session.SequenceActive ||
                    _session.SequencePrepared ||
                    _session.SequenceIndex < 0 ||
                    _session.SequenceIndex >= _session.SequenceRounds.Length ||
                    _session.SequenceRounds[_session.SequenceIndex] != round)
                {
                    return;
                }

                // Loading replay buffers after freeze time would mutate bot
                // presentation during live play. Leave the completed prefetch
                // cached for the next round_start instead.
                if (!TryReadFreezePhaseRemaining(out _, out _))
                    return;
                if (!ReplayPrefetchReady())
                {
                    Poll();
                    return;
                }

                if (PrepareNextSequenceRound(
                        $"{reason} prefetch ready",
                        pollIfPending: false))
                {
                    ScheduleFreezePrerollStart($"sequence round {round}");
                }
            });
        }

        Poll();
    }

    private bool PrepareArmedRound(
        string reason,
        bool pollIfPending = true)
    {
        if (!_session.Armed)
            return false;
        if (_session.ArmedPrepared)
            return true;
        if (string.IsNullOrWhiteSpace(_session.ArmedManifestPath) || _session.ArmedSourceRound < 0)
        {
            _session.Armed = false;
            _session.ArmedPrepared = false;
            Server.PrintToConsole("[DTR ERR] single-round plan is missing manifest/source_round");
            return false;
        }

        var manifestPath = _session.ArmedManifestPath;
        var sourceRound = _session.ArmedSourceRound;
        var loop = _session.ArmedLoop;
        var label = _session.ArmedLabel;
        if (!ReplayPrefetchReady())
        {
            if (pollIfPending)
                PollPendingArmedPreparation(manifestPath, sourceRound, reason);
            Server.PrintToConsole(
                $"dtr: single source_round={sourceRound} is still decoding off-thread; the game thread will not wait");
            return false;
        }

        var load = LoadRound(manifestPath, sourceRound);
        if (!load.Ok)
        {
            _session.Armed = false;
            _session.ArmedPrepared = false;
            _session.ArmedManifestPath = string.Empty;
            _session.ArmedSourceRound = -1;
            Server.PrintToConsole($"[DTR ERR] single source_round={sourceRound} failed while preparing on {reason}: {load.Message}");
            return false;
        }

        _session.Armed = true;
        _session.ArmedPrepared = true;
        _session.ArmedManifestPath = manifestPath;
        _session.ArmedSourceRound = sourceRound;
        _session.ArmedLoop = loop;
        _session.ArmedLabel = label;
        PreloadLoadedReplays();
        TryStartDtrRoundBanner($"single_r{sourceRound}");
        Server.PrintToConsole($"[DTR OK] round_start: loaded SINGLE source_round={sourceRound} on {reason}: {load.Message}");
        return true;
    }

    private void PollPendingArmedPreparation(
        string manifestPath,
        int sourceRound,
        string reason)
    {
        var token = ++_session.ArmedPreparePollToken;
        void Poll()
        {
            Server.NextFrame(() =>
            {
                if (token != _session.ArmedPreparePollToken ||
                    !_session.Armed ||
                    _session.ArmedPrepared ||
                    _session.ArmedSourceRound != sourceRound ||
                    !_session.ArmedManifestPath.Equals(
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

                if (PrepareArmedRound(
                        $"{reason} prefetch ready",
                        pollIfPending: false))
                {
                    ScheduleFreezePrerollStart(_session.ArmedLabel);
                }
            });
        }

        Poll();
    }

    private void StartPreparedSequenceRound()
    {
        if (!_session.SequencePrepared)
        {
            var pendingRound = _session.SequenceIndex >= 0 && _session.SequenceIndex < _session.SequenceRounds.Length
                ? _session.SequenceRounds[_session.SequenceIndex]
                : -1;
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {pendingRound} was not prepared by round_freeze_end; " +
                "skipping this server round and keeping the sequence armed for the next round_start");
            return;
        }

        var round = _session.SequencePreparedRound;
        var play = StartLoaded(loop: false);
        Server.PrintToConsole($"dtr: sequence round {round} start on round_freeze_end: {play}");

        _session.SequencePrepared = false;
        _session.SequencePreparedRound = -1;
        _session.SequenceIndex++;
        if (_session.SequenceIndex >= _session.SequenceRounds.Length)
        {
            _session.SequenceActive = false;
            Server.PrintToConsole(
                _playoffEnabled
                    ? "dtr: sequence complete; playoff continuation is armed"
                    : "dtr: sequence complete");
        }
        else
        {
            // Decode the next source round while the current replay is live.
            // Waiting until round_end leaves only the post-round/freeze window,
            // which is too short for some long v8 replay sets and can let the
            // next round enter freeze time without buy suppression or pre-roll.
            PrefetchRoundReplays(_session.SequenceManifestPath, _session.SequenceRounds[_session.SequenceIndex]);
        }
    }

    private void StopSequenceState()
    {
        var hadSequencePrefetch = _session.SequenceActive || _session.SequencePrepared ||
                                  _session.PlayoffPreparePending || _session.PlayoffPrepared;
        CancelPlayoffPreparation(unloadPrepared: true);
        _session.SequenceActive = false;
        _session.SequenceManifestPath = string.Empty;
        _session.SequenceRounds = [];
        _session.SequenceIndex = 0;
        _session.SequencePrepared = false;
        _session.SequencePreparedRound = -1;
        _session.SequencePreparePollToken++;
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        if (hadSequencePrefetch)
        {
            CancelReplayPrefetch();
            ReleaseUnusedWarmReplayBuffers();
        }
    }

}
