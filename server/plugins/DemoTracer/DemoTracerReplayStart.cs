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
    private string StartLoaded(bool loop)
        => StartLoaded(loop, ReplayStartAnchor.Live, null);

    private string StartLoaded(
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        var respawned = RespawnDeadLoadedReplayBots();
        if (respawned > 0)
        {
            Server.NextFrame(() =>
            {
                PreloadLoadedReplays();
                Server.PrintToConsole(
                    $"dtr: queued start after respawn: {StartLoadedReady(loop, anchor, freezeTimeSeconds)}");
            });
            return $"dtr: respawned {respawned} replay bot(s), start queued";
        }

        return StartLoadedReady(loop, anchor, freezeTimeSeconds);
    }

    private string StartLoadedReady(
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        if (IsWarmupPeriod())
            return "[DTR ERR] 热身阶段无法进行回放";

        if (!TryAssignInitialRoundSpawns(out var spawnReason))
            return $"[DTR ERR] initial spawn assignment is not ready: {spawnReason}";

        var ok = 0;
        foreach (var slot in _session.LoadedSlots)
        {
            _session.LastEnsuredWeaponDef.Remove(slot);
            _session.LastReplayWeaponDef.Remove(slot);
            _session.LastLockedWeaponTarget.Remove(slot);

            if (!IsReplaySlotStillSafe(slot))
            {
                ReleaseReplaySlot(slot, "unsafe_start_target");
                continue;
            }
            _session.ReplaySlots.Claim(slot);

            if (_session.LoadedReplays.TryGetValue(slot, out var replay))
            {
                if (!ResetReplayPawnRoundStartHealth(slot))
                {
                    ReleaseReplaySlot(slot, "dead_start_target");
                    continue;
                }

                // Revalidate pawn and controller equipment at the final start
                // boundary. Preload can run before CS2 finishes its spawn-side
                // controller mirrors, so an earlier successful write is not
                // sufficient evidence that armor is visible at playback start.
                ApplyReplayLoadoutForSlot(slot, replay);
                ApplyReplayRoundStartBalanceForSlot(slot, replay);
            }

            if (StartReplayForSlot(slot, loop, anchor, freezeTimeSeconds))
            {
                MarkReplayStarted(slot);
                ok++;
            }
            else
            {
                ReleaseReplaySlot(slot, "start_failed");
            }
        }
        var voice = TryStartLoadedAutoVoicePlayback(anchor, freezeTimeSeconds, ok);
        var chat = TryStartLoadedAutoChatPlayback(anchor, freezeTimeSeconds, ok);
        return $"dtr: started {ok}/{_session.LoadedSlots.Count} loaded slots, loop={loop}{voice}{chat}";
    }

    private void ScheduleInitialRoundSpawnAssignment()
    {
        if (_session.InitialSpawnAssignmentComplete ||
            _session.InitialSpawnAssignmentScheduled ||
            _session.LoadedSlots.Count == 0)
        {
            return;
        }

        var token = _session.InitialSpawnAssignmentToken;
        var attempts = 0;
        _session.InitialSpawnAssignmentScheduled = true;

        void TryAssign()
        {
            if (token != _session.InitialSpawnAssignmentToken)
                return;

            attempts++;
            if (TryAssignInitialRoundSpawns(out var reason))
            {
                _session.InitialSpawnAssignmentComplete = true;
                _session.InitialSpawnAssignmentScheduled = false;
                return;
            }

            if (attempts >= InitialSpawnAssignmentMaxAttempts)
            {
                _session.InitialSpawnAssignmentScheduled = false;
                Server.PrintToConsole(
                    $"[DTR WARN] initial spawn assignment was not ready after {attempts} attempts: {reason}");
                return;
            }

            AddTimer(
                ReplayReadinessPollSeconds,
                TryAssign,
                TimerFlags.STOP_ON_MAPCHANGE);
        }

        // Match the stable round-start path: attempt assignment in the event
        // frame, then poll only when the pawn is not ready yet. Deferring an
        // already-ready spawn adds a visible one-frame movement reset.
        TryAssign();
    }

    private bool TryAssignInitialRoundSpawns(out string reason)
    {
        if (_session.InitialSpawnAssignmentComplete)
        {
            reason = string.Empty;
            return true;
        }

        var players = FindTeamPlayers();
        var replayPlacements = new List<(
            CCSPlayerController Player,
            int Slot,
            CsTeam Team,
            ReplayVector3 Destination)>();
        var humanRelocations = new List<(
            CCSPlayerController Player,
            int VacatedBySlot,
            CsTeam Team,
            ReplayVector3 Origin)>();
        var summaries = new List<(CsTeam Team, int ReplaySlots, int SubstituteHumansMoved)>();

        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var plannedSlots = _session.LoadedSlots
                .Where(slot => _session.LoadedReplays.TryGetValue(slot, out var replay) && replay.ManifestTeam == team)
                .ToArray();
            if (plannedSlots.Length == 0 || plannedSlots.Length >= StandardTeamSize)
                continue;

            var replaySpawns = new List<(
                CCSPlayerController Player,
                int Slot,
                ReplayVector3 Destination,
                ReplayVector3 Current)>();
            foreach (var slot in plannedSlots)
            {
                if (!_session.LoadedReplays.TryGetValue(slot, out var replay) ||
                    replay.RoundStartOrigin is not { } destination ||
                    !IsFiniteReplayPosition(destination))
                {
                    reason = $"slot={slot} team={team} has no valid recorded round-start origin";
                    return false;
                }

                var replayPlayer = players.FirstOrDefault(candidate => candidate.Slot == slot);
                if (replayPlayer is not { IsValid: true, PawnIsAlive: true } ||
                    replayPlayer.Team != team ||
                    !TryGetPawnOrigin(replayPlayer, out var current))
                {
                    reason = $"slot={slot} team={team} pawn is not spawned yet";
                    return false;
                }

                replaySpawns.Add((
                    replayPlayer,
                    slot,
                    destination,
                    new ReplayVector3(current.X, current.Y, current.Z)));
            }

            var humans = new List<(CCSPlayerController Player, ReplayVector3 Current)>();
            foreach (var candidate in players)
            {
                if (candidate.Team != team ||
                    !candidate.PawnIsAlive ||
                    candidate.IsBot ||
                    _botHiderBridge.IsManagedBot(candidate.Slot) ||
                    _session.LoadedSlots.Contains(candidate.Slot) ||
                    (TryGetControllingBotState(candidate, out var controllingBot) && controllingBot) ||
                    !TryGetPawnOrigin(candidate, out var current))
                {
                    continue;
                }

                humans.Add((
                    candidate,
                    new ReplayVector3(current.X, current.Y, current.Z)));
            }

            var conflicts = humans
                .Where(human => replaySpawns.Any(
                    replay => PlayerHullsOverlap(human.Current, replay.Destination)))
                .ToList();
            var occupiedOrigins = humans
                .Where(human => !conflicts.Any(conflict => conflict.Player.Slot == human.Player.Slot))
                .Select(human => human.Current)
                .ToList();
            var relocationSpawns = new List<(int Slot, ReplayVector3 Origin)>();
            foreach (var replay in replaySpawns)
            {
                if (replaySpawns.Any(candidate =>
                        PlayerHullsOverlap(replay.Current, candidate.Destination)) ||
                    occupiedOrigins.Any(origin => PlayerHullsOverlap(replay.Current, origin)) ||
                    relocationSpawns.Any(candidate =>
                        PlayerHullsOverlap(replay.Current, candidate.Origin)))
                {
                    continue;
                }

                relocationSpawns.Add((replay.Slot, replay.Current));
            }

            var teamRelocations = new List<(
                CCSPlayerController Player,
                int VacatedBySlot,
                CsTeam Team,
                ReplayVector3 Origin)>();
            foreach (var conflict in conflicts)
            {
                if (relocationSpawns.Count == 0)
                {
                    reason =
                        $"slot={conflict.Player.Slot} team={team} has no safe DTR-native spawn to inherit";
                    return false;
                }

                var relocation = relocationSpawns
                    .OrderBy(candidate => PositionDistanceSquared(conflict.Current, candidate.Origin))
                    .First();

                relocationSpawns.Remove(relocation);
                occupiedOrigins.Add(relocation.Origin);
                teamRelocations.Add((conflict.Player, relocation.Slot, team, relocation.Origin));
            }

            humanRelocations.AddRange(teamRelocations);
            replayPlacements.AddRange(replaySpawns.Select(replay => (
                replay.Player,
                replay.Slot,
                team,
                replay.Destination)));
            summaries.Add((team, replaySpawns.Count, teamRelocations.Count));
        }

        try
        {
            // Preserve the game's native spawn allocation for substitute
            // humans before placing replay bots at demo-backed positions.
            foreach (var relocation in humanRelocations)
            {
                var pawn = relocation.Player.PlayerPawn.Value;
                if (pawn is not { IsValid: true })
                {
                    reason =
                        $"human slot={relocation.Player.Slot} team={relocation.Team} pawn became invalid";
                    return false;
                }

                pawn.Teleport(
                    new Vector(relocation.Origin.X, relocation.Origin.Y, relocation.Origin.Z),
                    null,
                    null);
            }

            foreach (var replay in replayPlacements)
            {
                var pawn = replay.Player.PlayerPawn.Value;
                if (pawn is not { IsValid: true })
                {
                    reason = $"replay slot={replay.Slot} team={replay.Team} pawn became invalid";
                    return false;
                }

                pawn.Teleport(
                    new Vector(replay.Destination.X, replay.Destination.Y, replay.Destination.Z),
                    null,
                    null);
            }

            foreach (var summary in summaries)
            {
                Server.PrintToConsole(
                    "dtr: initial partial-roster spawns assigned " +
                    $"team={summary.Team} dtr_slots={summary.ReplaySlots}/{StandardTeamSize} " +
                    $"substitute_humans_moved={summary.SubstituteHumansMoved}");
            }
        }
        catch (Exception ex)
        {
            reason = $"initial spawn teleport failed: {ex.Message}";
            return false;
        }

        _session.InitialSpawnAssignmentComplete = true;
        reason = string.Empty;
        return true;
    }

    private void InvalidateInitialSpawnAssignment()
    {
        _session.InitialSpawnAssignmentToken++;
        _session.InitialSpawnAssignmentComplete = false;
        _session.InitialSpawnAssignmentScheduled = false;
    }

    private static bool IsFiniteReplayPosition(ReplayVector3 position)
        => float.IsFinite(position.X) &&
           float.IsFinite(position.Y) &&
           float.IsFinite(position.Z);

    private static bool PlayerHullsOverlap(ReplayVector3 left, ReplayVector3 right)
        => MathF.Abs(left.X - right.X) < PlayerHullWidth &&
           MathF.Abs(left.Y - right.Y) < PlayerHullWidth &&
           MathF.Abs(left.Z - right.Z) < PlayerHullHeight;

    private static float PositionDistanceSquared(ReplayVector3 left, ReplayVector3 right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private bool StartReplayForSlot(int slot, bool loop)
        => StartReplayForSlot(slot, loop, ReplayStartAnchor.Live, null);

    private bool StartReplayForSlot(
        int slot,
        bool loop,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        if (IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

        var startIndex = 0u;
        if (_session.LoadedReplays.TryGetValue(slot, out var replay))
        {
            if (anchor == ReplayStartAnchor.FreezePreroll)
            {
                if (replay.PlayStartTickIndex == 0)
                    return false;
                if (_session.FreezePrerollSlots.Contains(slot) &&
                    BotControllerNative.GetReplayState(slot).Playing)
                {
                    return true;
                }

                startIndex = FreezePrerollStartIndex(replay, freezeTimeSeconds ?? 0.0f);
                var startedUntil = startIndex < replay.PlayStartTickIndex &&
                                   RegisterReplayPawnForSlot(slot) &&
                                   BotControllerNative.StartReplayUntil(
                                       slot,
                                       loop,
                                       startIndex,
                                       replay.PlayStartTickIndex);
                if (!startedUntil)
                    return false;

                // Keep native Update/Upkeep alive during pre-roll. Replay
                // hooks own movement/view/input, while the persistent buy skip
                // gates only vanilla purchase intent. Handoff depends on the
                // native bot continuing to perceive and warm its state here.
                _session.FreezePrerollSlots.Add(slot);
                _session.ResumedFreezePrerollSlots.Remove(slot);
                _session.ReplaySlots.Claim(slot);
                return true;
            }

            startIndex = anchor switch
            {
                ReplayStartAnchor.Live => replay.PlayStartTickIndex,
                _ => 0,
            };
        }
        _session.FreezePrerollSlots.Remove(slot);
        if (anchor == ReplayStartAnchor.Live &&
            _session.ResumedFreezePrerollSlots.Remove(slot) &&
            BotControllerNative.GetReplayState(slot).Playing)
        {
            _session.ReplaySlots.Claim(slot);
            return true;
        }
        var started = RegisterReplayPawnForSlot(slot) &&
                      BotControllerNative.StartReplayAt(slot, loop, startIndex);
        if (started)
            _session.ReplaySlots.Claim(slot);
        return started;
    }

    private static bool RegisterReplayPawnForSlot(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        // Best-effort on old native builds; the normal start path still does
        // the authoritative lock/replay validation.
        _ = BotControllerNative.SetReplayPawn(slot, player.PlayerPawn.Value.Handle);
        return true;
    }

    private void ScheduleFreezePrerollStart(string label)
    {
        if (!TryGetFreezePrerollSchedule(
                out var freezeTimeSeconds,
                out var delaySeconds,
                out _,
                out var reason))
        {
            Server.PrintToConsole($"dtr: freeze pre-roll skipped for {label}: {reason}");
            return;
        }

        var token = ++_session.FreezePrerollToken;
        var attempts = 0;
        var replayPreparationApplied = false;
        _session.FreezePrerollStarted = false;
        void TryStart()
        {
            if (token != _session.FreezePrerollToken || _session.FreezePrerollStarted)
                return;

            attempts++;
            if (!TryGetFreezePrerollSchedule(
                    out var currentFreezeTimeSeconds,
                    out _,
                    out var playbackPrerollSeconds,
                    out var currentReason))
            {
                Server.PrintToConsole(
                    $"[DTR WARN] freeze pre-roll expired before {label} became ready: {currentReason}");
                return;
            }

            if (!LoadedReplayPawnsReadyForFreezePreroll(out var readinessReason) ||
                !ReassertLoadedReplayBuySkips(out readinessReason))
            {
                if (attempts == 1 || attempts % 64 == 0)
                {
                    Server.PrintToConsole(
                        $"[DTR WARN] freeze pre-roll waiting for replay pawns on {label}: {readinessReason}");
                }
                AddTimer(
                    ReplayReadinessPollSeconds,
                    TryStart,
                    TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }

            if (!replayPreparationApplied)
            {
                PreloadLoadedReplays();
                replayPreparationApplied = true;
            }
            var message = StartLoadedReady(
                loop: false,
                ReplayStartAnchor.FreezePreroll,
                playbackPrerollSeconds);
            var missingSlots = MissingFreezePrerollStartSlots();
            if (missingSlots.Length > 0)
            {
                if (attempts == 1 || attempts % 64 == 0)
                {
                    Server.PrintToConsole(
                        $"[DTR WARN] freeze pre-roll retry {label}: slots={string.Join(",", missingSlots)}; {message}");
                }
                AddTimer(
                    ReplayReadinessPollSeconds,
                    TryStart,
                    TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }

            _session.FreezePrerollStarted = true;
            Server.PrintToConsole(
                $"dtr: freeze pre-roll start {label}: mp_freezetime={currentFreezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}s playback={playbackPrerollSeconds.ToString("F2", CultureInfo.InvariantCulture)}s delay={delaySeconds.ToString("F2", CultureInfo.InvariantCulture)}s; {message}");
        }

        if (delaySeconds <= 0.01f)
        {
            Server.NextFrame(TryStart);
        }
        else
        {
            AddTimer(delaySeconds, TryStart);
            Server.PrintToConsole(
                $"dtr: freeze pre-roll scheduled {label}: mp_freezetime={freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}s delay={delaySeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        }
    }

    private int[] FreezePrerollExpectedSlots()
        => _session.LoadedSlots
            .Where(slot => _session.LoadedReplays.TryGetValue(slot, out var replay) &&
                           replay.PlayStartTickIndex > 0)
            .Distinct()
            .Order()
            .ToArray();

    private bool LoadedReplayPawnsReadyForFreezePreroll(out string reason)
    {
        foreach (var slot in FreezePrerollExpectedSlots())
        {
            if (!IsReplaySlotStillSafe(slot))
            {
                reason = $"slot={slot} is no longer a safe replay bot";
                return false;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, PawnIsAlive: true } ||
                player.PlayerPawn is not { IsValid: true, Value.IsValid: true } ||
                player.PlayerPawn.Value.WeaponServices == null)
            {
                reason = $"slot={slot} pawn or weapon services are not ready";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool ReassertLoadedReplayBuySkips(out string reason)
    {
        foreach (var slot in FreezePrerollExpectedSlots())
        {
            if (!BotControllerNative.SetBuySkip(slot))
            {
                reason = $"slot={slot} native buy suppression failed";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private int[] MissingFreezePrerollStartSlots()
        => FreezePrerollExpectedSlots()
            .Where(slot => !_session.FreezePrerollSlots.Contains(slot))
            .ToArray();

    private int[] MissingFreezePrerollResumeSlots()
        => FreezePrerollExpectedSlots()
            .Where(slot => !_session.ResumedFreezePrerollSlots.Contains(slot))
            .ToArray();

    private void InvalidateFreezePreroll()
    {
        _session.FreezePrerollToken++;
        _session.FreezePrerollStarted = false;
    }

    private void ResumeFreezePrerollReplays(bool loop)
    {
        foreach (var slot in _session.FreezePrerollSlots.ToArray())
        {
            if (_session.LoadedReplays.TryGetValue(slot, out var replay) &&
                replay.PlayStartTickIndex > 0 &&
                BotControllerNative.GetReplayState(slot).Playing &&
                BotControllerNative.StartReplayAt(
                    slot,
                    loop,
                    replay.PlayStartTickIndex))
            {
                _session.ResumedFreezePrerollSlots.Add(slot);
                continue;
            }

            // Never leave a held freeze command active after the server enters
            // live play. The normal next-frame start path may retry this slot.
            _ = BotControllerNative.StopReplay(slot);
            _session.ResumedFreezePrerollSlots.Remove(slot);
        }
        _session.FreezePrerollSlots.Clear();
    }

    private void ClearFreezePrerollReplayState()
    {
        _session.FreezePrerollSlots.Clear();
        _session.ResumedFreezePrerollSlots.Clear();
    }

    private bool TryGetFreezePrerollSchedule(
        out float freezeTimeSeconds,
        out float delaySeconds,
        out float playbackPrerollSeconds,
        out string reason)
    {
        freezeTimeSeconds = 0.0f;
        delaySeconds = 0.0f;
        playbackPrerollSeconds = 0.0f;
        if (!TryReadFreezeTimeConVar(out freezeTimeSeconds, out reason))
            return false;
        if (freezeTimeSeconds <= 0.0f)
        {
            reason = $"{FreezeTimeConVarName} is {freezeTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}";
            return false;
        }

        var maxRecordedPrerollSeconds = 0.0f;
        foreach (var replay in _session.LoadedReplays.Values)
        {
            if (replay.PlayStartTickIndex == 0 || replay.TickRate <= 0.0f)
                continue;
            maxRecordedPrerollSeconds = Math.Max(
                maxRecordedPrerollSeconds,
                replay.PlayStartTickIndex / replay.TickRate);
        }

        if (maxRecordedPrerollSeconds <= 0.0f)
        {
            reason = "loaded replays have no recorded freeze pre-roll";
            return false;
        }

        var scheduleWindowSeconds = freezeTimeSeconds;
        if (TryReadFreezePhaseRemaining(out var phaseRemainingSeconds, out _) &&
            phaseRemainingSeconds > 0.0f)
        {
            scheduleWindowSeconds = phaseRemainingSeconds;
        }

        var timing = ReplayRuntimePolicy.ComputeFreezePrerollTiming(
            freezeTimeSeconds,
            scheduleWindowSeconds,
            maxRecordedPrerollSeconds);
        delaySeconds = timing.DelaySeconds;
        playbackPrerollSeconds = timing.PlaybackSeconds;
        reason = string.Empty;
        return true;
    }

    private static bool TryReadFreezePhaseRemaining(out float seconds, out string reason)
    {
        seconds = 0.0f;
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            if (proxy is not { IsValid: true })
            {
                reason = "cs_gamerules entity was not found";
                return false;
            }

            var rules = proxy.GameRules;
            if (rules == null)
            {
                reason = "cs_gamerules has no rules object";
                return false;
            }

            if (!rules.FreezePeriod)
            {
                reason = "game rules are not in freeze period";
                return false;
            }

            var phaseTime = rules.TimeUntilNextPhaseStarts;
            if (!float.IsFinite(phaseTime))
            {
                reason = "game rules phase end time is invalid";
                return false;
            }

            seconds = phaseTime > Server.CurrentTime
                ? phaseTime - Server.CurrentTime
                : phaseTime;
            if (seconds > 0.0f && float.IsFinite(seconds))
            {
                reason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = $"failed to read game rules freeze phase: {ex.Message}";
            return false;
        }

        reason = "game rules freeze phase has no remaining time";
        return false;
    }

    private static bool IsWarmupPeriod()
    {
        try
        {
            var proxy = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(entity => entity is { IsValid: true });
            return proxy is { IsValid: true } &&
                   proxy.GameRules != null &&
                   proxy.GameRules.WarmupPeriod;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadFreezeTimeConVar(out float seconds, out string reason)
    {
        seconds = 0.0f;
        var conVar = ConVar.Find(FreezeTimeConVarName);
        if (conVar == null)
        {
            reason = $"server ConVar {FreezeTimeConVarName} was not found";
            return false;
        }

        try
        {
            seconds = conVar.GetPrimitiveValue<float>();
        }
        catch
        {
            try
            {
                seconds = conVar.GetPrimitiveValue<int>();
            }
            catch (Exception ex)
            {
                reason = $"failed to read {FreezeTimeConVarName}: {ex.Message}";
                return false;
            }
        }

        if (float.IsFinite(seconds) && seconds >= 0.0f)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{FreezeTimeConVarName} has invalid value {seconds.ToString(CultureInfo.InvariantCulture)}";
        return false;
    }

    private static uint FreezePrerollStartIndex(LoadedReplay replay, float freezeTimeSeconds)
    {
        if (replay.PlayStartTickIndex == 0 || replay.TickRate <= 0.0f || freezeTimeSeconds <= 0.0f)
            return replay.PlayStartTickIndex;

        var serverFreezeTicks = (uint)Math.Round(freezeTimeSeconds * replay.TickRate);
        return serverFreezeTicks >= replay.PlayStartTickIndex
            ? 0
            : replay.PlayStartTickIndex - serverFreezeTicks;
    }

    private int RespawnDeadLoadedReplayBots()
    {
        var respawned = 0;
        foreach (var slot in _session.LoadedSlots)
        {
            if (!_session.LoadedReplays.TryGetValue(slot, out var replay))
                continue;

            if (!IsReplaySlotStillSafe(slot))
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || player.PawnIsAlive)
                continue;

            try
            {
                InvalidateLoadedReplayCosmeticAlignmentForSlot(slot);
                player.Respawn();
                _session.WeaponLoadoutSyncedSlots.Remove(slot);
                _session.PawnEquipmentSync.Invalidate(slot);
                _session.RebuiltInventorySlots.Remove(slot);
                _session.LastEnsuredWeaponDef.Remove(slot);
                _session.LastReplayWeaponDef.Remove(slot);
                _session.LastLockedWeaponTarget.Remove(slot);
                respawned++;
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"dtr: failed to respawn replay bot slot={slot}: {ex.Message}");
            }
        }

        return respawned;
    }
}
