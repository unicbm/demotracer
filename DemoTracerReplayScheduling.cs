/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace DemoTracer;

internal enum ReplaySlotWorkKind
{
    Reconcile,
    LoadoutRetry,
}

internal enum ReplayRoundWorkKind
{
    PresentationSync,
    C4EarlyReconcile,
    C4LateReconcile,
    C4PostMutationReconcile,
}

internal readonly record struct ReplaySlotWorkKey(int Slot, ReplaySlotWorkKind Kind);

internal readonly record struct ReplaySlotWorkEpoch(long WriteEpoch, long IdentityGeneration);

internal readonly record struct ReplayWriteContext(
    int Slot,
    int UserId,
    long WriteEpoch,
    long IdentityGeneration)
{
    public ReplaySlotWorkEpoch WorkEpoch => new(WriteEpoch, IdentityGeneration);
}

public sealed partial class DemoTracerPlugin
{
    private readonly EpochWorkCoalescer<ReplaySlotWorkKey, ReplaySlotWorkEpoch> _replaySlotWork = new();
    private readonly EpochWorkCoalescer<ReplayRoundWorkKind, long> _replayRoundWork = new();
    private long _replayRoundWorkEpoch;

    private bool TryCaptureReplayWriteContext(int slot, out ReplayWriteContext context)
    {
        context = default;
        if (!_mapActive ||
            _lifecycleResetInProgress ||
            !_session.ReplaySlots.TryGet(slot, out var state) ||
            !state.OwnsWrites ||
            !_session.ReplayIdentityGenerationBySlot.TryGetValue(slot, out var identityGeneration))
        {
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } || player.UserId is not int userId)
            return false;

        context = new ReplayWriteContext(slot, userId, state.Epoch, identityGeneration);
        return true;
    }

    private bool TryRefreshReplayWriteContext(
        ReplayWriteContext expected,
        out ReplayWriteContext current)
    {
        return TryCaptureReplayWriteContext(expected.Slot, out current) &&
               current.UserId == expected.UserId &&
               current.WriteEpoch == expected.WriteEpoch &&
               current.IdentityGeneration == expected.IdentityGeneration &&
               IsReplaySlotStillSafe(expected.Slot);
    }

    private void ScheduleReplaySlotNextFrame(
        int slot,
        ReplaySlotWorkKind kind,
        Action<ReplayWriteContext> callback)
    {
        if (!TryCaptureReplayWriteContext(slot, out var scheduledContext))
            return;

        var key = new ReplaySlotWorkKey(slot, kind);
        if (!_replaySlotWork.TrySchedule(key, scheduledContext.WorkEpoch))
            return;

        Server.NextFrame(() =>
        {
            if (!_replaySlotWork.TryConsume(key, scheduledContext.WorkEpoch) ||
                !TryRefreshReplayWriteContext(scheduledContext, out var currentContext))
            {
                return;
            }

            callback(currentContext);
        });
    }

    private void ScheduleReplaySlotReconciliation(int slot)
    {
        ScheduleReplaySlotNextFrame(slot, ReplaySlotWorkKind.Reconcile, context =>
        {
            if (!_session.LoadedReplays.TryGetValue(context.Slot, out var replay))
                return;

            // A player_spawn invalidates the old pawn even if native replay
            // state still says Playing. Pawn equipment is keyed by entity
            // handle, while the weapon inventory remains independently cached.
            ApplyReplayLoadoutForSlot(context.Slot, replay);
            PreloadReplayWeaponsForSlot(context.Slot, replay);
            QueueLoadedReplayCosmeticAlignmentForSlot(context.Slot);
        });
    }

    private void ScheduleReplayLoadoutRetry(int slot, int retriesRemaining)
    {
        ScheduleReplaySlotNextFrame(slot, ReplaySlotWorkKind.LoadoutRetry, context =>
        {
            if (_session.LoadedReplays.TryGetValue(context.Slot, out var replay))
                ApplyReplayLoadoutForSlot(context.Slot, replay, retriesRemaining);
        });
    }

    private void BeginReplayRoundWorkEpoch()
    {
        _replayRoundWorkEpoch++;
        _replayRoundWork.Clear();
        ResetSafeC4RoundMutationState();
    }

    private bool IsReplayRoundWorkEpochCurrent(long epoch)
        => epoch == _replayRoundWorkEpoch &&
           _mapActive &&
           !_lifecycleResetInProgress;

    private void CancelReplaySlotDeferredWork(int slot)
        => _replaySlotWork.CancelWhere(key => key.Slot == slot);

    private void CancelPendingReplaySlotReconciliations()
        => _replaySlotWork.CancelWhere(key => key.Kind == ReplaySlotWorkKind.Reconcile);

    private void CancelAllReplayDeferredWork()
    {
        _replaySlotWork.Clear();
        BeginReplayRoundWorkEpoch();
    }

    private void ScheduleRoundBoundarySpawnReconciliation()
    {
        ScheduleReplayRoundNextFrame(
            ReplayRoundWorkKind.PresentationSync,
            () => SyncBotHiderPresentationLease(announce: false));
        ScheduleReplayRoundTimer(
            ReplayRoundWorkKind.C4EarlyReconcile,
            0.05f,
            () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true));
        ScheduleReplayRoundTimer(
            ReplayRoundWorkKind.C4LateReconcile,
            0.20f,
            () => AlignSafeC4OwnerForLoadedReplays(forceReconcile: true));
    }

    private void ScheduleReplayRoundNextFrame(ReplayRoundWorkKind kind, Action callback)
    {
        var epoch = _replayRoundWorkEpoch;
        if (!_replayRoundWork.TrySchedule(kind, epoch))
            return;

        Server.NextFrame(() =>
        {
            if (_replayRoundWork.TryConsume(kind, epoch) &&
                _mapActive &&
                !_lifecycleResetInProgress)
            {
                callback();
            }
        });
    }

    private void ScheduleReplayRoundTimer(
        ReplayRoundWorkKind kind,
        float delaySeconds,
        Action callback)
    {
        var epoch = _replayRoundWorkEpoch;
        if (!_replayRoundWork.TrySchedule(kind, epoch))
            return;

        AddTimer(
            delaySeconds,
            () =>
            {
                if (_replayRoundWork.TryConsume(kind, epoch) &&
                    _mapActive &&
                    !_lifecycleResetInProgress)
                {
                    callback();
                }
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }
}
