using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool _playoffEnabled;
    private bool _playoffPreparePending;
    private bool _playoffPendingCanLoad;
    private int _playoffPrepareToken;
    private int _playoffPendingTRound = -1;
    private int _playoffPendingCtRound = -1;
    private string _playoffPendingReason = string.Empty;
    private string _playoffPendingPrepareReason = string.Empty;
    private bool _playoffPrepared;
    private int _playoffPreparedTRound = -1;
    private int _playoffPreparedCtRound = -1;
    private string _playoffPreparedLabel = string.Empty;
    private int _playoffRoundIndex;

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
        if (enabled && string.IsNullOrWhiteSpace(_sequenceManifestPath))
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
        _sequenceManifestPath = manifestPath;
        _sequenceRounds = rounds;
        _sequenceIndex = Array.IndexOf(rounds, startRound);
        _sequenceActive = _sequenceIndex >= 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _sequencePreparePollToken++;
        InvalidateFreezePreroll();
        _armed = false;
        _armedPrepared = false;
        _armedPreparePollToken++;
        _armedManifestPath = string.Empty;
        _armedSourceRound = -1;
        PrefetchRoundReplays(manifestPath, manifest, startRound, stableManifestStamp);

        command.ReplyToCommand(
            restart
                ? $"[DTR OK] Planned SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; restart=now."
                : $"[DTR OK] Armed SEQUENCE. manifest=\"{manifestPath}\"; from_source_round={startRound}; waiting for next round_start.");
        command.ReplyToCommand(
            $"[DTR OK] Sequence has {rounds.Length - _sequenceIndex} round(s) remaining from source_round={startRound}.");
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
        _sequenceActive = false;
        _sequenceManifestPath = string.Empty;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _sequencePreparePollToken++;
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        _armed = true;
        _armedLoop = loop;
        _armedPrepared = false;
        _armedPreparePollToken++;
        _armedManifestPath = manifestPath;
        _armedSourceRound = round;
        _armedLabel = $"source_round={round} manifest={manifestPath}";
        PrefetchRoundReplays(manifestPath, manifest, round, stableManifestStamp);
        reply(
            restart
                ? $"[DTR OK] Planned SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; restart=now."
                : $"[DTR OK] Armed SINGLE ROUND. manifest=\"{manifestPath}\"; source_round={round}; waiting for next round_start.");
        reply("[DTR OK] This plan will not advance to later manifest rounds.");
        IssueRestartIfRequested(restart, reply);
    }

    private static string StripOuterQuotes(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }

    private static bool ParseLoopArgument(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("loop:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["loop:".Length..];

        return !normalized.Equals("0", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("off", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("false", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private bool PrepareNextSequenceRound(
        string reason,
        bool pollIfPending = true)
    {
        if (_sequenceIndex < 0 || _sequenceIndex >= _sequenceRounds.Length)
        {
            _sequenceActive = false;
            Server.PrintToConsole("dtr: sequence complete");
            return false;
        }

        if (_sequencePrepared)
            return true;

        var round = _sequenceRounds[_sequenceIndex];
        if (!ReplayPrefetchReady())
        {
            if (pollIfPending)
                PollPendingSequencePreparation(round, reason);
            Server.PrintToConsole(
                $"dtr: sequence round {round} is still decoding off-thread; the game thread will not wait");
            return false;
        }

        var load = LoadRound(_sequenceManifestPath, round);
        if (!load.Ok)
        {
            _sequencePrepared = false;
            _sequencePreparedRound = -1;
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {round} could not be prepared on {reason}; " +
                $"keeping it armed for the next round_start: {load.Message}");
            return false;
        }

        PreloadLoadedReplays();
        _sequencePrepared = true;
        _sequencePreparedRound = round;
        TryStartDtrRoundBanner($"sequence_r{round}");
        Server.PrintToConsole($"dtr: prepared sequence round {round} on {reason}: {load.Message}");
        return true;
    }

    private void PollPendingSequencePreparation(int round, string reason)
    {
        var token = ++_sequencePreparePollToken;
        void Poll()
        {
            Server.NextFrame(() =>
            {
                if (token != _sequencePreparePollToken ||
                    !_sequenceActive ||
                    _sequencePrepared ||
                    _sequenceIndex < 0 ||
                    _sequenceIndex >= _sequenceRounds.Length ||
                    _sequenceRounds[_sequenceIndex] != round)
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
        if (!_armed)
            return false;
        if (_armedPrepared)
            return true;
        if (string.IsNullOrWhiteSpace(_armedManifestPath) || _armedSourceRound < 0)
        {
            _armed = false;
            _armedPrepared = false;
            Server.PrintToConsole("[DTR ERR] single-round plan is missing manifest/source_round");
            return false;
        }

        var manifestPath = _armedManifestPath;
        var sourceRound = _armedSourceRound;
        var loop = _armedLoop;
        var label = _armedLabel;
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
            _armed = false;
            _armedPrepared = false;
            _armedManifestPath = string.Empty;
            _armedSourceRound = -1;
            Server.PrintToConsole($"[DTR ERR] single source_round={sourceRound} failed while preparing on {reason}: {load.Message}");
            return false;
        }

        _armed = true;
        _armedPrepared = true;
        _armedManifestPath = manifestPath;
        _armedSourceRound = sourceRound;
        _armedLoop = loop;
        _armedLabel = label;
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
        var token = ++_armedPreparePollToken;
        void Poll()
        {
            Server.NextFrame(() =>
            {
                if (token != _armedPreparePollToken ||
                    !_armed ||
                    _armedPrepared ||
                    _armedSourceRound != sourceRound ||
                    !_armedManifestPath.Equals(
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
                    ScheduleFreezePrerollStart(_armedLabel);
                }
            });
        }

        Poll();
    }

    private void StartPreparedSequenceRound()
    {
        if (!_sequencePrepared)
        {
            var pendingRound = _sequenceIndex >= 0 && _sequenceIndex < _sequenceRounds.Length
                ? _sequenceRounds[_sequenceIndex]
                : -1;
            Server.PrintToConsole(
                $"[DTR WARN] sequence source round {pendingRound} was not prepared by round_freeze_end; " +
                "skipping this server round and keeping the sequence armed for the next round_start");
            return;
        }

        var round = _sequencePreparedRound;
        var play = StartLoaded(loop: false);
        Server.PrintToConsole($"dtr: sequence round {round} start on round_freeze_end: {play}");

        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _sequenceIndex++;
        if (_sequenceIndex >= _sequenceRounds.Length)
        {
            _sequenceActive = false;
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
            PrefetchRoundReplays(_sequenceManifestPath, _sequenceRounds[_sequenceIndex]);
        }
    }

    private void StopSequenceState()
    {
        var hadSequencePrefetch = _sequenceActive || _sequencePrepared ||
                                  _playoffPreparePending || _playoffPrepared;
        CancelPlayoffPreparation(unloadPrepared: true);
        _sequenceActive = false;
        _sequenceManifestPath = string.Empty;
        _sequenceRounds = [];
        _sequenceIndex = 0;
        _sequencePrepared = false;
        _sequencePreparedRound = -1;
        _sequencePreparePollToken++;
        ResetPlayoffProgress();
        InvalidateFreezePreroll();
        if (hadSequencePrefetch)
        {
            CancelReplayPrefetch();
            ReleaseUnusedWarmReplayBuffers();
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
               !_sequenceActive &&
               !string.IsNullOrWhiteSpace(_sequenceManifestPath) &&
               _sequenceRounds.Length > 0 &&
               _sequenceIndex >= _sequenceRounds.Length;
    }

    private bool HasPlayoffSchedulingState()
        => IsPlayoffPlanReady() || _playoffPreparePending || _playoffPrepared;

    private string FormatPlayoffPlanStatus()
    {
        if (_playoffPrepared)
            return $"prepared:T=r{_playoffPreparedTRound},CT=r{_playoffPreparedCtRound}";
        if (_playoffPreparePending)
            return $"decoding:T=r{_playoffPendingTRound},CT=r{_playoffPendingCtRound}";
        if (IsPlayoffPlanReady())
            return $"ready:extra_round={_playoffRoundIndex + 1}";
        if (_sequenceActive && _playoffEnabled)
            return "waiting_for_sequence_end";
        return "none";
    }

    private void ResetPlayoffProgress()
    {
        ClearPlayoffPendingPreparation(cancelDecode: true);
        _playoffPrepared = false;
        _playoffPreparedTRound = -1;
        _playoffPreparedCtRound = -1;
        _playoffPreparedLabel = string.Empty;
        _playoffRoundIndex = 0;
    }

    private void CancelPlayoffPreparation(bool unloadPrepared)
    {
        var hadPrepared = _playoffPrepared;
        ClearPlayoffPendingPreparation(cancelDecode: true);
        _playoffPrepared = false;
        _playoffPreparedTRound = -1;
        _playoffPreparedCtRound = -1;
        _playoffPreparedLabel = string.Empty;
        if (!unloadPrepared || !hadPrepared)
            return;

        InvalidateFreezePreroll();
        StopAndUnloadLoaded(clearArmedPlan: false);
    }

    private void ClearPlayoffPendingPreparation(bool cancelDecode)
    {
        var wasPending = _playoffPreparePending;
        _playoffPreparePending = false;
        _playoffPendingCanLoad = false;
        _playoffPrepareToken++;
        _playoffPendingTRound = -1;
        _playoffPendingCtRound = -1;
        _playoffPendingReason = string.Empty;
        _playoffPendingPrepareReason = string.Empty;
        if (cancelDecode && wasPending)
            FinishReplayPrefetchRound();
    }

    private bool PrepareNextPlayoffRound(string prepareReason, bool allowLoad = true)
    {
        if (!IsPlayoffPlanReady())
            return false;
        if (_playoffPrepared)
            return true;
        if (_playoffPreparePending)
        {
            if (!allowLoad)
                return false;

            _playoffPendingCanLoad = true;
            if (ReplayPrefetchReady())
            {
                return CompletePendingPlayoffPreparation(
                    waitForDecode: false,
                    scheduleFreezePreroll: false);
            }

            PollPendingPlayoffPreparation(_playoffPrepareToken);
            return false;
        }

        var manifestPath = ResolveReadableManifestPath(_sequenceManifestPath);
        if (!TryGetPrefetchedManifest(manifestPath, out var manifest) &&
            !TryReadManifest(manifestPath, out manifest, out var readError))
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: failed to read manifest: {readError}");
            return false;
        }
        if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: map mismatch server={currentMap} manifest={manifest.Map}");
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
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: {rosterError}");
            return false;
        }
        if (tSteamIds.Count == 0 && ctSteamIds.Count == 0)
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: no replay bot targets");
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
                $"dtr: playoff skipped extra round {_playoffRoundIndex + 1}: {chooseError}");
            return false;
        }

        PrefetchPlayoffRoundReplays(
            manifestPath,
            manifest,
            tRound,
            ctRound,
            tSteamIds,
            ctSteamIds);
        _playoffPreparePending = true;
        _playoffPendingCanLoad = allowLoad;
        _playoffPendingTRound = tRound;
        _playoffPendingCtRound = ctRound;
        _playoffPendingReason =
            $"T=r{tRound} from {tCandidateCount} full-buy candidate(s), " +
            $"CT=r{ctRound} from {ctCandidateCount} full-buy candidate(s)";
        _playoffPendingPrepareReason = prepareReason;
        var token = ++_playoffPrepareToken;
        Server.PrintToConsole(
            $"dtr: playoff extra round {_playoffRoundIndex + 1} selected on {prepareReason}; " +
            $"{_playoffPendingReason}; decoding replay data off-thread");
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
            if (_loadedReplays.TryGetValue(bot.Slot, out var loaded))
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

        var candidates = manifest.Rounds
            .Where(round => !round.PistolRound && IsPlayoffFullBuy(round, side))
            .Select(round => round.Round)
            .Where(round => PlayoffRoundCoversRoster(manifest, round, side, steamIds))
            .Distinct()
            .Order()
            .ToArray();
        candidateCount = candidates.Length;
        if (candidates.Length == 0)
        {
            error = $"side={side} has no full-buy source round covering every retained SteamID";
            return false;
        }

        selectedRound = candidates[Random.Shared.Next(candidates.Length)];
        return true;
    }

    private static bool IsPlayoffFullBuy(ManifestRound round, string side)
    {
        var economy = side.Equals("t", StringComparison.OrdinalIgnoreCase)
            ? round.TEconomy
            : round.CtEconomy;
        return string.Equals(economy?.Class, "full", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PlayoffRoundCoversRoster(
        ConversionManifest manifest,
        int round,
        string side,
        IReadOnlySet<ulong> steamIds)
    {
        var fileCountsBySteamId = manifest.Files
            .Where(file => file.Round == round &&
                           file.Side.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                           file.SteamId != 0)
            .GroupBy(file => file.SteamId)
            .ToDictionary(group => group.Key, group => group.Count());
        return steamIds.All(steamId =>
            fileCountsBySteamId.TryGetValue(steamId, out var count) && count == 1);
    }

    private void PollPendingPlayoffPreparation(int token)
    {
        Server.NextFrame(() =>
        {
            if (!_playoffPreparePending || token != _playoffPrepareToken)
                return;
            if (!_playoffPendingCanLoad)
                return;
            if (!ReplayPrefetchReady())
            {
                PollPendingPlayoffPreparation(token);
                return;
            }

            _ = CompletePendingPlayoffPreparation(
                waitForDecode: false,
                scheduleFreezePreroll: true);
        });
    }

    private bool CompletePendingPlayoffPreparation(
        bool waitForDecode,
        bool scheduleFreezePreroll)
    {
        if (!_playoffPreparePending)
            return _playoffPrepared;
        if (!waitForDecode && !ReplayPrefetchReady())
            return false;

        var tRound = _playoffPendingTRound;
        var ctRound = _playoffPendingCtRound;
        var reason = _playoffPendingReason;
        var prepareReason = _playoffPendingPrepareReason;
        ClearPlayoffPendingPreparation(cancelDecode: false);
        if (!IsPlayoffPlanReady())
            return false;

        var load = LoadPlayoffRound(_sequenceManifestPath, tRound, ctRound);
        if (!load.Ok)
        {
            Server.PrintToConsole(
                $"dtr: playoff failed extra round {_playoffRoundIndex + 1}: {load.Message}");
            return false;
        }

        PreloadLoadedReplays();
        _playoffPrepared = true;
        _playoffPreparedTRound = tRound;
        _playoffPreparedCtRound = ctRound;
        _playoffPreparedLabel = $"{reason}; {load.Message}";
        TryStartDtrRoundBanner($"playoff_t{tRound}_ct{ctRound}");
        Server.PrintToConsole(
            $"dtr: prepared playoff extra round {_playoffRoundIndex + 1} on {prepareReason} -> {_playoffPreparedLabel}");
        if (scheduleFreezePreroll &&
            TryReadFreezePhaseRemaining(out var freezeRemaining, out _) &&
            freezeRemaining > 0.0f)
        {
            ScheduleFreezePrerollStart($"playoff extra round {_playoffRoundIndex + 1}");
        }
        return true;
    }

    private void StartPreparedPlayoffRound()
    {
        var extraRound = _playoffRoundIndex + 1;
        if (!_playoffPrepared)
        {
            Server.PrintToConsole(
                $"dtr: playoff skipped start for extra round {extraRound}: replay prefetch was not ready by round_freeze_end");
            ClearPlayoffPendingPreparation(cancelDecode: true);
            _playoffRoundIndex++;
            return;
        }

        var label = _playoffPreparedLabel;
        var play = StartLoaded(loop: false);
        Server.PrintToConsole(
            $"dtr: playoff extra round {extraRound} start on round_freeze_end -> {label}; {play}");
        _playoffRoundIndex++;
        _playoffPrepared = false;
        _playoffPreparedTRound = -1;
        _playoffPreparedCtRound = -1;
        _playoffPreparedLabel = string.Empty;
        ReleaseUnusedWarmReplayBuffers();
    }

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
        var loop = command.ArgCount > slotArg + 1 && command.GetArg(slotArg + 1) != "0";
        if (_loadedReplays.TryGetValue(slot, out var replay))
            PreloadReplayWeaponsForSlot(slot, replay);
        _lastEnsuredWeaponDef.Remove(slot);

        if (!IsReplaySlotStillSafe(slot))
        {
            command.ReplyToCommand($"dtr: refused to play slot {slot}: not a safe bot target");
            return;
        }
        if (!CheckReplayStartGates(message => command.ReplyToCommand(message), stopCurrentForOverride: false))
            return;

        var ok = StartReplayForSlot(slot, loop);
        if (ok)
        {
            MarkReplayStarted(slot);
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
        foreach (var slot in _loadedSlots)
            slots.Add(slot);
        foreach (var slot in _loadedReplays.Keys)
            slots.Add(slot);
        foreach (var slot in _demoTracerOwnedSlots)
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

            _loadedReplays.TryGetValue(slot, out var replay);
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
        stopped = BotControllerNative.StopReplay(candidate.Slot);
        unloaded = BotControllerNative.UnloadReplay(candidate.Slot);
        ReleaseReplaySlot(candidate.Slot, reason);
        _loadedSlots.Remove(candidate.Slot);
        ForgetRetainedBotHiderPresentation(candidate.Slot);
        ForgetLoadedReplayMetadata(candidate.Slot);
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
            _loadedSlots.Remove(slot);
            ReleaseReplaySlot(slot, "unload");
            ForgetRetainedBotHiderPresentation(slot);
            ForgetLoadedReplayMetadata(slot);
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

    private static TickPlayerSnapshot BuildTickPlayerSnapshot()
    {
        var controllers = FindPlayerControllers();
        return new TickPlayerSnapshot(controllers, FindTeamPlayers(controllers));
    }

    private List<CCSPlayerController> FindReplayTargets()
    {
        var players = FindTeamPlayers();
        var targets = players
            .Where(IsReplayTargetBot)
            .OrderBy(player => player.IsBot ? 0 : 1)
            .ThenBy(player => player.Slot)
            .ToList();
        return targets;
    }

    private bool IsReplayTargetBot(CCSPlayerController player)
    {
        return IsReplayTargetBot(player, null);
    }

    private bool IsReplayTargetBot(
        CCSPlayerController player,
        IReadOnlyList<CCSPlayerController>? playerControllers)
    {
        if (!IsReplayControllerSafe(player) || IsReplayPawnTakenByController(player, playerControllers))
            return false;
        return player.IsBot || _botHiderBridge.IsManagedBot(player.Slot);
    }

    private bool IsReplaySlotStillSafe(
        int slot,
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true } && IsReplayTargetBot(player, playerControllers);
    }

    private bool IsReplaySlotStillSafe(int slot, TickPlayerSnapshot playerSnapshot)
    {
        return playerSnapshot.TryGetSlot(slot, out var player) &&
               player is { IsValid: true } &&
               IsReplayTargetBot(player, playerSnapshot.Controllers);
    }

    private static bool IsReplayControllerSafe(CCSPlayerController player)
    {
        return TryGetControllingBotState(player, out var controllingBot) && !controllingBot;
    }

    private static bool TryGetControllingBotState(CCSPlayerController player, out bool controllingBot)
    {
        controllingBot = false;
        if (player is not { IsValid: true })
            return false;

        try
        {
            controllingBot = player.ControllingBot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReplayPawnTakenByController(
        CCSPlayerController replayTarget,
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        if (replayTarget.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
            return true;

        var replayPawnIndex = replayPawn.Value.Index;
        var controllers = playerControllers ?? FindPlayerControllers();
        foreach (var controller in controllers)
        {
            if (controller is not { IsValid: true } || controller.Slot == replayTarget.Slot)
                continue;
            if (!TryGetControllingBotState(controller, out var controllingBot) || !controllingBot)
                continue;

            if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } controlledPawn &&
                controlledPawn.Value.Index == replayPawnIndex)
                return true;

            if (controller.OriginalControllerOfCurrentPawn is { IsValid: true, Value.IsValid: true } original &&
                original.Value.Slot == replayTarget.Slot)
                return true;
        }

        return false;
    }

    private static List<CCSPlayerController> FindPlayerControllers()
    {
        return Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid)
            .ToList();
    }

    private static List<CCSPlayerController> FindTeamPlayers(
        IReadOnlyList<CCSPlayerController>? playerControllers = null)
    {
        return (playerControllers ?? FindPlayerControllers())
            .Where(player => player.IsValid &&
                             (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist) &&
                             player.PlayerPawn is { IsValid: true, Value.IsValid: true })
            .OrderBy(player => player.Team)
            .ThenBy(player => player.Slot)
            .ToList();
    }
}
