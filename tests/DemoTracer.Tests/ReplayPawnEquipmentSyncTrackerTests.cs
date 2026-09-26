/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayPawnEquipmentSyncTrackerTests
{
    private static readonly ReplayPawnEquipmentIdentity Identity = new(
        UserId: 7,
        PawnEntityHandle: 0x1234,
        ReplayIdentityGeneration: 11,
        ReplaySteamId: 76561198000000007UL);

    [Fact]
    public void SamePawnAndReplayEquipmentSyncOnlyOnce()
    {
        var tracker = new ReplayPawnEquipmentSyncTracker();

        Assert.False(tracker.IsSynced(7, Identity));
        tracker.MarkSynced(7, Identity);

        Assert.True(tracker.IsSynced(7, Identity));
    }

    [Fact]
    public void NewPawnRequiresEquipmentEvenWhenSlotAndReplayAreUnchanged()
    {
        var tracker = new ReplayPawnEquipmentSyncTracker();
        tracker.MarkSynced(7, Identity);

        Assert.False(tracker.IsSynced(
            7,
            Identity with { PawnEntityHandle = 0x5678 }));
    }

    [Fact]
    public void NewReplayIdentityRequiresEquipmentOnTheSamePawn()
    {
        var tracker = new ReplayPawnEquipmentSyncTracker();
        tracker.MarkSynced(7, Identity);

        Assert.False(tracker.IsSynced(
            7,
            Identity with { ReplayIdentityGeneration = 12 }));
        Assert.False(tracker.IsSynced(
            7,
            Identity with { ReplaySteamId = Identity.ReplaySteamId + 1 }));
    }

    [Fact]
    public void InvalidateAndClearForgetCompletedEquipmentSync()
    {
        var tracker = new ReplayPawnEquipmentSyncTracker();
        tracker.MarkSynced(7, Identity);
        tracker.MarkSynced(3, Identity with { UserId = 3, PawnEntityHandle = 0x9999 });

        tracker.Invalidate(7);
        Assert.False(tracker.IsSynced(7, Identity));
        Assert.True(tracker.IsSynced(3, Identity with { UserId = 3, PawnEntityHandle = 0x9999 }));

        tracker.Clear();
        Assert.False(tracker.IsSynced(3, Identity with { UserId = 3, PawnEntityHandle = 0x9999 }));
    }
}
