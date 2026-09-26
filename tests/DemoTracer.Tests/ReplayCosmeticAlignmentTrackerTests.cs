/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayCosmeticAlignmentTrackerTests
{
    private static readonly ReplayCosmeticPawnIdentity Identity = new(
        UserId: 7,
        PawnHandle: (nint)0x1234,
        ReplayIdentityGeneration: 11,
        ReplaySteamId: 76561198000000007UL);

    [Fact]
    public void RepeatedQueueKeepsOnlyLatestSlotCallback()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();

        var stale = tracker.Queue(7);
        var current = tracker.Queue(7);

        Assert.False(tracker.TryConsume(7, stale));
        Assert.True(tracker.TryConsume(7, current));
        Assert.False(tracker.TryConsume(7, current));
    }

    [Fact]
    public void SamePawnAndReplayIdentityAlignOnlyOnce()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();

        Assert.False(tracker.IsAligned(7, Identity));

        tracker.MarkAligned(7, Identity);

        Assert.True(tracker.IsAligned(7, Identity));
        Assert.False(tracker.IsAligned(7, Identity with { PawnHandle = (nint)0x5678 }));
        Assert.False(tracker.IsAligned(7, Identity with { ReplayIdentityGeneration = 12 }));
    }

    [Fact]
    public void ReleasedControlCanRetainTheSameReplayAlignmentWithoutResolvingTheBotPawn()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();
        tracker.MarkAligned(7, Identity);

        Assert.True(tracker.HasReplayAlignment(
            7,
            Identity.ReplayIdentityGeneration,
            Identity.ReplaySteamId));
        Assert.False(tracker.HasReplayAlignment(
            7,
            Identity.ReplayIdentityGeneration + 1,
            Identity.ReplaySteamId));
        Assert.False(tracker.HasReplayAlignment(
            7,
            Identity.ReplayIdentityGeneration,
            Identity.ReplaySteamId + 1));
    }

    [Fact]
    public void FailedEvidenceWritesNeverCompleteAlignment()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();

        Assert.False(tracker.TryMarkAligned(7, Identity, failedWrites: 1));
        Assert.False(tracker.IsAligned(7, Identity));

        Assert.True(tracker.TryMarkAligned(7, Identity, failedWrites: 0));
        Assert.True(tracker.IsAligned(7, Identity));
    }

    [Fact]
    public void PawnInvalidationCancelsAlignmentAndPendingWork()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();
        tracker.MarkAligned(7, Identity);
        var token = tracker.Queue(7);

        tracker.Invalidate(7);

        Assert.False(tracker.IsAligned(7, Identity));
        Assert.False(tracker.TryConsume(7, token));
    }

    [Fact]
    public void ControlReleaseCancelsPendingWorkWithoutForgettingAlignment()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();
        tracker.MarkAligned(7, Identity);
        var token = tracker.Queue(7);

        tracker.CancelPending(7);

        Assert.True(tracker.IsAligned(7, Identity));
        Assert.False(tracker.TryConsume(7, token));
    }

    [Fact]
    public void LifecycleResetClearsEverySlot()
    {
        var tracker = new ReplayCosmeticAlignmentTracker();
        tracker.MarkAligned(7, Identity);
        tracker.MarkAligned(3, Identity with { UserId = 3, PawnHandle = (nint)0x9999 });
        var token = tracker.Queue(7);

        tracker.Clear();

        Assert.False(tracker.IsAligned(7, Identity));
        Assert.False(tracker.IsAligned(3, Identity with { UserId = 3, PawnHandle = (nint)0x9999 }));
        Assert.False(tracker.TryConsume(7, token));
    }
}
