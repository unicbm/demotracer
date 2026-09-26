/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class EpochWorkCoalescerTests
{
    [Fact]
    public void DuplicateWorkInTheSameEpochIsScheduledOnce()
    {
        var work = new EpochWorkCoalescer<string, long>();

        Assert.True(work.TrySchedule("slot:3", 7));
        Assert.False(work.TrySchedule("slot:3", 7));
        Assert.Equal(1, work.Count);

        Assert.True(work.TryConsume("slot:3", 7));
        Assert.False(work.TryConsume("slot:3", 7));
        Assert.Equal(0, work.Count);
    }

    [Fact]
    public void NewEpochReplacesPendingWorkAndExpiresTheOldCallback()
    {
        var work = new EpochWorkCoalescer<string, long>();
        work.TrySchedule("slot:3", 7);

        Assert.True(work.TrySchedule("slot:3", 8));
        Assert.False(work.TryConsume("slot:3", 7));
        Assert.True(work.TryConsume("slot:3", 8));
    }

    [Fact]
    public void CancelWhereRemovesOnlyTheSelectedWorkClass()
    {
        var work = new EpochWorkCoalescer<(int Slot, string Kind), long>();
        work.TrySchedule((2, "music"), 1);
        work.TrySchedule((2, "reconcile"), 1);
        work.TrySchedule((4, "music"), 1);

        Assert.Equal(2, work.CancelWhere(key => key.Kind == "music"));
        Assert.False(work.TryConsume((2, "music"), 1));
        Assert.True(work.TryConsume((2, "reconcile"), 1));
        Assert.False(work.TryConsume((4, "music"), 1));
    }

    [Fact]
    public void CompositeEpochLetsNewIdentityReplaceWorkForTheSameWriteOwner()
    {
        var work = new EpochWorkCoalescer<string, ReplaySlotWorkEpoch>();
        var oldIdentity = new ReplaySlotWorkEpoch(12, 4);
        var newIdentity = new ReplaySlotWorkEpoch(12, 5);

        Assert.True(work.TrySchedule("slot:3", oldIdentity));
        Assert.True(work.TrySchedule("slot:3", newIdentity));
        Assert.False(work.TryConsume("slot:3", oldIdentity));
        Assert.True(work.TryConsume("slot:3", newIdentity));
    }
}
