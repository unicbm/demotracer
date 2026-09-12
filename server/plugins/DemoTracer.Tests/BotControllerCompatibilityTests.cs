/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;
using BotControllerApi;

namespace DemoTracer.Tests;

public sealed class BotControllerCompatibilityTests
{
    [Fact]
    public void PublicApiLayoutsMatchImprover144AndNativeReplayLayouts()
    {
        Assert.Equal(92, Marshal.SizeOf<MovementSnapshot>());
        Assert.Equal(228, Marshal.SizeOf<ReplayTick>());
        Assert.Equal(Marshal.SizeOf<NativeReplayTick>(), Marshal.SizeOf<ReplayTick>());
        Assert.Equal(192, Marshal.OffsetOf<ReplayTick>(nameof(ReplayTick.EventFlags)).ToInt32());
        Assert.Equal(224, Marshal.OffsetOf<ReplayTick>(nameof(ReplayTick.EventDropVelocityZ)).ToInt32());
        Assert.Equal(224, Marshal.OffsetOf<NativeReplayTick>(nameof(NativeReplayTick.EventDropVelocityZ)).ToInt32());
        Assert.Equal(28, Marshal.SizeOf<SubtickMove>());
        Assert.Equal(68, Marshal.SizeOf<ReplayCommandFrame>());
        Assert.Equal(48, Marshal.SizeOf<ReplayMovementExtra>());
        Assert.Equal(76, Marshal.SizeOf<BotProfileData>());
    }

    [Fact]
    public void ImproverControlCannotMutateDemoTracerOwnedSlots()
    {
        var api = new BotControllerApiImpl(slot => slot == 4);
        Assert.False(api.Lock(4, LockKind.All));
        Assert.False(api.Lock(4, LockTarget.Slot3));
        Assert.False(api.Unlock(4, LockKind.Weapon));
        Assert.False(api.SwitchBotWeapon(4, 9001));
        Assert.Equal(-1, api.InjectUsercmd(4, 1UL << 35));
        Assert.Equal(-1, api.StartUsercmdSuppression(4, 1UL << 5));
        Assert.Equal(-1, api.StartUsercmdMovement(4, 1, 0));
        Assert.False(api.UpdateUsercmdMovement(4, 1, 0, 1));
        Assert.False(api.SuppressUsercmd(4, 1UL << 5, 100));
        Assert.False(api.LoadReplay(4, [], []));
        Assert.False(api.SetReplayPawn(4, 1));
        Assert.False(api.StartReplay(4));
        Assert.False(api.StopReplay(4));
        Assert.False(api.SetBuyPlan(4, "ak47"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public void InvalidSlotsNeverReachEngineMemory(int slot)
    {
        var api = new BotControllerApiImpl(_ => throw new InvalidOperationException("invalid slot reached ownership check"));
        Assert.False(api.SwitchBotWeapon(slot, 9001));
        Assert.Equal(-1, api.StartUsercmdSuppression(slot, 32));
    }

    [Fact]
    public void CleanupReleasesOnlySuccessfulOwnedResourcesAndIsIdempotent()
    {
        var owned = new OwnedSlotResources();
        owned.Track(4, SlotResource.Replay, false);
        owned.Track(5, SlotResource.Replay, true);
        owned.Track(5, SlotResource.Input, 123L);
        owned.Track(5, SlotResource.BuyPlan, true);
        owned.Track(6, SlotResource.Input, -1L);
        var releases = new List<(int, SlotResource)>();
        owned.ReleaseAll(_ => false, (slot, resources) => releases.Add((slot, resources)));
        Assert.Equal([(5, SlotResource.Replay | SlotResource.Input | SlotResource.BuyPlan)], releases);
        owned.ReleaseAll(_ => false, (_, _) => throw new Exception("released twice"));
        Assert.Empty(owned.Slots);
    }

    [Fact]
    public void DemoTracerTakeoverPreservesItsControlButReleasesOurRecorder()
    {
        var owned = new OwnedSlotResources();
        owned.Track(7, SlotResource.Control | SlotResource.Recording, true);
        SlotResource released = SlotResource.None;
        owned.Release(7, true, (_, resources) => released = resources);
        Assert.Equal(SlotResource.Recording, released);
        Assert.Empty(owned.Slots);
    }

    [Fact]
    public void ObservedTakeoverCannotClearAnotherOwnersLaterReplay()
    {
        var owned = new OwnedSlotResources();
        owned.Track(7, SlotResource.Control, true);
        owned.Forget(7, SlotResource.Control);
        owned.Release(7, false, (_, _) => throw new Exception("foreign control released"));
    }

    [Fact]
    public void FailedReplayStartReleasesItsBufferWithoutDroppingIndependentRecording()
    {
        var owned = new OwnedSlotResources();
        owned.Track(7, SlotResource.Replay | SlotResource.Recording | SlotResource.Input, true);
        SlotResource released = SlotResource.None;
        owned.Release(7, SlotResource.Replay, false, (_, resources) => released = resources);
        Assert.Equal(SlotResource.Replay, released);
        Assert.True(owned.Has(7, SlotResource.Recording | SlotResource.Input));
        Assert.False(owned.Has(7, SlotResource.Replay));
    }

    [Fact]
    public void CleanupFailureDoesNotSkipOtherSlots()
    {
        var owned = new OwnedSlotResources();
        owned.Track(2, SlotResource.Replay, true);
        owned.Track(3, SlotResource.Recording, true);
        var visited = new List<int>();
        Assert.Throws<AggregateException>(() => owned.ReleaseAll(_ => false, (slot, _) =>
        {
            visited.Add(slot);
            if (slot == 2) throw new InvalidOperationException("native unavailable");
        }));
        Assert.Equal([2, 3], visited);
        Assert.Empty(owned.Slots);
    }
}
