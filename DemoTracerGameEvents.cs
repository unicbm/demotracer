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
    [GameEventHandler]
    public HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        // CS2 constructs the new pawn inventory after round_prestart but before
        // round_start. BotRandomizer must therefore receive the complete replay
        // plan here so its GiveNamedItem hook can build the correct item views.
        BeginReplayRoundWorkEpoch();
        ResetDtrRoundBannerForRound();
        BeginBotHiderPresentationTransition();
        BeginBotRandomizerCosmeticLeaseTransition();
        try
        {
            var retainedPlayoffFallback = ShouldRetainLoadedPlayoffFallback();
            if (retainedPlayoffFallback)
            {
                StopLoadedReplaySlots("playoff_pending_fallback");
                InvalidateInitialSpawnAssignment();
            }
            else if (StopReplayStateForRoundBoundary("round_prestart"))
            {
                Server.PrintToConsole("[DTR WARN] round_prestart stopped stale DTR replay state");
            }

            if ((_session.Plan.SequenceActive || _session.Plan.Armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
            {
                Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
                StopAllState("warmup_block");
                return HookResult.Continue;
            }

            if (!TryReadSwitchingTeamsAtRoundReset(
                    out var switchingTeamsAtRoundReset,
                    out var teamTransitionError))
            {
                Server.PrintToConsole(
                    $"[DTR ERR] round_prestart cannot resolve the upcoming team transition: {teamTransitionError}");
                return HookResult.Continue;
            }

            if (_session.Plan.SequenceActive)
            {
                _ = PrepareNextSequenceRound(
                    "round_prestart",
                    switchingTeamsAtRoundReset);
            }
            else if (IsPlayoffPlanReady())
            {
                _ = PrepareNextPlayoffRound(
                    "round_prestart",
                    switchingTeamsAtRoundReset: switchingTeamsAtRoundReset);
            }
            else if (_session.Plan.Armed)
            {
                _ = PrepareArmedRound(
                    "round_prestart",
                    switchingTeamsAtRoundReset);
            }
            if (retainedPlayoffFallback &&
                !_session.Plan.PlayoffPrepared &&
                _session.LoadedSlots.Count > 0)
            {
                PrepareLoadedReplayOwnership();
            }
            return HookResult.Continue;
        }
        finally
        {
            EndBotHiderPresentationTransition();
            EndBotRandomizerCosmeticLeaseTransition();
        }
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if ((_session.Plan.SequenceActive || _session.Plan.Armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            StopAllState("warmup_block");
            return HookResult.Continue;
        }

        // Preparation after this event is too late for spawn-time cosmetic
        // construction. Only schedule playback for a plan accepted in
        // round_prestart; otherwise leave it armed for the next server round.
        if (_session.Plan.SequenceActive && _session.Plan.SequencePrepared)
            ScheduleFreezePrerollStart($"sequence round {_session.Plan.SequencePreparedRound}");
        else if (IsPlayoffPlanReady() && _session.Plan.PlayoffPrepared)
            ScheduleFreezePrerollStart($"playoff extra round {_session.Plan.PlayoffRoundIndex + 1}");
        else if (_session.Plan.Armed && _session.Plan.ArmedPrepared)
            ScheduleFreezePrerollStart(_session.Plan.ArmedLabel);

        if (_session.LoadedSlots.Count > 0)
            ScheduleRoundBoundarySpawnReconciliation();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        InvalidateFreezePreroll();

        if ((_session.Plan.SequenceActive || _session.Plan.Armed || HasPlayoffSchedulingState()) && IsWarmupPeriod())
        {
            Server.PrintToConsole("[DTR ERR] 热身阶段无法进行回放");
            StopAllState("warmup_block");
            return HookResult.Continue;
        }

        var resumeLoop = !_session.Plan.SequenceActive &&
                         !HasPlayoffSchedulingState() &&
                         _session.Plan.Armed &&
                         _session.Plan.ArmedLoop;
        ResumeFreezePrerollReplays(resumeLoop);
        ScheduleAvatarOverrideUserInfoRefresh();

        var missingFreezePrerollSlots = MissingFreezePrerollResumeSlots();
        if (missingFreezePrerollSlots.Length > 0)
        {
            Server.PrintToConsole(
                "[DTR WARN] freeze pre-roll ownership was incomplete; " +
                $"retrying affected slots from their live replay index slots={string.Join(",", missingFreezePrerollSlots)}");
        }

        if (_session.Plan.SequenceActive)
        {
            Server.NextFrame(StartPreparedSequenceRound);
            return HookResult.Continue;
        }

        if (HasPlayoffSchedulingState())
        {
            Server.NextFrame(StartPreparedPlayoffRound);
            return HookResult.Continue;
        }

        if (!_session.Plan.Armed)
            return HookResult.Continue;
        if (!_session.Plan.ArmedPrepared)
        {
            Server.PrintToConsole($"[DTR WARN] armed round is waiting for the next full round_prestart: {_session.Plan.ArmedLabel}");
            return HookResult.Continue;
        }

        var loop = _session.Plan.ArmedLoop;
        var label = _session.Plan.ArmedLabel;
        _session.Plan.ClearArmed();
        Server.NextFrame(() =>
        {
            var message = StartLoaded(loop, ReplayStartAnchor.Live, null);
            Server.PrintToConsole($"dtr: auto-start {label}: {message}");
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player)
        {
            var spawnedSlot = player.Slot;
            if (_session.LoadedReplays.TryGetValue(spawnedSlot, out var spawnedReplay) &&
                !ReplayTeamAssignmentPolicy.LiveTeamMatches(
                    spawnedReplay.ManifestTeam,
                    player.Team))
            {
                Server.PrintToConsole(
                    $"[DTR ERR] spawn team mismatch slot={spawnedSlot} " +
                    $"actual={player.Team} manifest={spawnedReplay.ManifestTeam}; removing replay slot");
                RemoveReplaySlot(
                    spawnedSlot,
                    "spawn_team_mismatch",
                    out _,
                    out _);
            }
            if (_retainedReplayViewmodelSlots.Contains(player.Slot) &&
                !_session.ReplaySlots.IsPlaying(player.Slot))
            {
                RestoreReplayBotViewmodel(player.Slot);
            }
            InvalidateReplayWriteEpoch(spawnedSlot);
            _session.PawnEquipmentSync.Invalidate(spawnedSlot);
            // A normal round spawn already carries the engine-restored weapon
            // inventory, but armor, helmet, and defuser belong to the new pawn.
            // Keep only the weapon preparation flags here; clearing them for
            // every spawn can detach an already-correct primary such as an AWP.
            InvalidateLoadedReplayCosmeticAlignmentForSlot(player.Slot);
            if (_session.LoadedSlots.Count > 0)
            {
                if (_session.ReplaySlots.IsOwned(player.Slot) &&
                    _session.LoadedReplays.ContainsKey(player.Slot))
                {
                    // Refresh the complete plan at the natural spawn boundary.
                    // Round-start preparation normally installed it before item
                    // construction; a newly accepted plan waits for the next spawn.
                    _ = SyncBotHiderPresentationLease(announce: false);
                    _ = SyncBotRandomizerCosmeticLease(announce: false);
                    // Buy plans are slot-scoped, but the engine creates a new
                    // pawn each round. Reassert the skip edge at spawn and redo
                    // loadout preparation once the new pawn is fully usable.
                    _ = BotControllerNative.SetBuySkip(spawnedSlot);
                    ScheduleReplaySlotReconciliation(spawnedSlot);
                }
                ScheduleInitialRoundSpawnAssignment();
                ScheduleRoundBoundarySpawnReconciliation();
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (@event.Userid is not { IsValid: true } player)
            return HookResult.Continue;

        if (IsHumanAvatarOverrideCandidate(player))
            ScheduleHumanTeamAvatarOverrideReconciliation();

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var handoffSlot = HandoffIncludesDeath(_handoffMode) && HasActiveReplaySlots()
            ? GetDeathHandoffSlot(@event)
            : -1;
        if (handoffSlot >= 0)
            HandoffActiveReplays($"player_death_slot{handoffSlot}", handoffSlot);

        if (@event.Userid is { IsValid: true } victim &&
            handoffSlot != victim.Slot)
        {
            if (IsReplaySlotPlaying(victim.Slot))
            {
                BotControllerNative.StopReplay(victim.Slot);
                ReleaseReplaySlot(victim.Slot, "replay_target_death");
                _ = SyncBotHiderPresentationLease(announce: false, forceReplace: true);
            }
            else if (_retainedReplayViewmodelSlots.Contains(victim.Slot))
            {
                RestoreReplayBotViewmodel(victim.Slot);
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (!HandoffIncludesC4(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        var triggerSlot = @event.Userid is { IsValid: true } planter && IsReplaySlotPlaying(planter.Slot)
            ? planter.Slot
            : -1;
        HandoffActiveReplays(
            triggerSlot >= 0 ? $"bomb_planted_slot{triggerSlot}" : "bomb_planted",
            triggerSlot,
            forceAll: true);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBulletDamage(EventBulletDamage @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Victim, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_session.PendingBulletDamages.TryGetValue(victimSlot, out var damage) &&
            damage.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(damage.Time))
        {
            _session.PendingBulletDamages.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage.Damage);
        }
        else
        {
            _session.PendingBulletHits[victimSlot] = new PendingBulletHit(attackerSlot, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!HandoffIncludesContact(_handoffMode) || !HasActiveReplaySlots())
            return HookResult.Continue;

        if (!TryGetEnemyBulletHandoffPair(@event.Attacker, @event.Userid, out var victimSlot, out var attackerSlot))
            return HookResult.Continue;

        var damage = Math.Max(0, @event.DmgHealth) + Math.Max(0, @event.DmgArmor);
        if (damage < BulletHandoffMinDamage)
            return HookResult.Continue;

        PruneExpiredBulletHandoffState();
        if (_session.PendingBulletHits.TryGetValue(victimSlot, out var hit) &&
            hit.AttackerSlot == attackerSlot &&
            IsFreshBulletHandoffEvent(hit.Time))
        {
            _session.PendingBulletHits.Remove(victimSlot);
            TryHandoffBulletDamagedReplay(victimSlot, attackerSlot, damage);
        }
        else
        {
            _session.PendingBulletDamages[victimSlot] = new PendingBulletDamage(attackerSlot, damage, Server.CurrentTime);
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        TickRuntimeHealthHeartbeat();

        if (!_mapActive || _lifecycleResetInProgress)
            return;

        _botHiderBridge.BeginTickQueryScope();
        try
        {
            EnsureBotHiderPresentationLease();
            EnsureBotRandomizerCosmeticLease();
            ProcessReplayTick();
        }
        finally
        {
            _botHiderBridge.EndTickQueryScope();
        }
    }
}
