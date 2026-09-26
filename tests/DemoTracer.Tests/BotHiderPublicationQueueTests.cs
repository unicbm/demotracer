/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotHiderImpl;

namespace DemoTracer.Tests;

public sealed class BotHiderPublicationQueueTests
{
    [Fact]
    public void PlayerAndTakeoverEventsReconcileOnlyTheirParticipantsOnce()
    {
        var pending = new PendingPresentationPublications();
        pending.RequestSlot(2);
        pending.RequestSlot(2);
        pending.RequestSlot(7);
        pending.RequestPing(2);
        pending.RequestPing(8);

        var work = pending.Drain();

        Assert.False(work.All);
        Assert.Equal((1UL << 2) | (1UL << 7), work.Slots);
        Assert.Equal(1UL << 8, work.PingSlots);
        Assert.Equal((false, 0UL, 0UL), pending.Drain());
    }

    [Fact]
    public void RoundAndSessionReconcileSupersedeAllPerSlotPublications()
    {
        var pending = new PendingPresentationPublications();
        pending.RequestPing(5);
        pending.RequestAll();
        pending.RequestSlot(63);

        Assert.Equal((true, 0UL, 0UL), pending.Drain());
        pending.RequestPing(5);
        Assert.Equal((false, 0UL, 1UL << 5), pending.Drain());
    }

    [Fact]
    public void MapBoundaryDiscardsOldWorkAndInvalidSlotsNeverWrap()
    {
        var pending = new PendingPresentationPublications();
        pending.RequestAll();
        pending.RequestSlot(4);
        pending.RequestPing(4);
        pending.Clear();
        pending.RequestSlot(-1);
        pending.RequestSlot(64);
        pending.RequestPing(-1);
        pending.RequestPing(64);

        Assert.Equal((false, 0UL, 0UL), pending.Drain());
    }
}
