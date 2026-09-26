/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplaySlotRegistryTests
{
    [Fact]
    public void LoadStartReleaseAndReclaimFollowOneExplicitLifecycle()
    {
        var slots = new ReplaySlotRegistry();

        var claimed = slots.LoadAndClaim(7);
        Assert.Equal(ReplaySlotPhase.Claimed, claimed.Phase);
        Assert.True(slots.IsLoaded(7));
        Assert.True(slots.IsOwned(7));
        Assert.False(slots.IsPlaying(7));

        var playing = slots.MarkPlaying(7);
        Assert.Equal(ReplaySlotPhase.Playing, playing.Phase);
        Assert.Equal(claimed.Epoch, playing.Epoch);
        Assert.True(slots.IsPlaying(7));

        Assert.True(slots.Release(7));
        Assert.True(slots.TryGet(7, out var released));
        Assert.Equal(ReplaySlotPhase.Loaded, released.Phase);
        Assert.True(released.Epoch > playing.Epoch);
        Assert.False(slots.IsOwned(7));
        Assert.False(slots.IsPlaying(7));

        var reclaimed = slots.Claim(7);
        Assert.Equal(ReplaySlotPhase.Claimed, reclaimed.Phase);
        Assert.True(reclaimed.Epoch > released.Epoch);
    }

    [Fact]
    public void ReloadIsUniqueAndMovesTheSlotToANewClaimEpoch()
    {
        var slots = new ReplaySlotRegistry();
        var first = slots.LoadAndClaim(3);
        slots.MarkPlaying(3);

        var reloaded = slots.LoadAndClaim(3);

        Assert.Equal([3], slots.LoadedSlots);
        Assert.Equal(ReplaySlotPhase.Claimed, reloaded.Phase);
        Assert.True(reloaded.Epoch > first.Epoch);
        Assert.False(slots.IsPlaying(3));
    }

    [Fact]
    public void UnloadAndClearRemoveEveryDerivedIndex()
    {
        var slots = new ReplaySlotRegistry();
        slots.LoadAndClaim(5);
        slots.MarkPlaying(5);
        slots.LoadAndClaim(2);

        Assert.True(slots.Unload(5));
        Assert.Equal([2], slots.LoadedSlots);
        Assert.False(slots.IsLoaded(5));
        Assert.False(slots.IsOwned(5));
        Assert.False(slots.IsPlaying(5));

        slots.Clear();
        Assert.False(slots.HasAnyState);
        Assert.Empty(slots.LoadedSlots);
        Assert.Empty(slots.OwnedSlots);
        Assert.Empty(slots.PlayingSlots);
    }

    [Fact]
    public void ClaimAndStartRejectUnknownSlots()
    {
        var slots = new ReplaySlotRegistry();

        Assert.Throws<InvalidOperationException>(() => slots.Claim(9));
        Assert.Throws<InvalidOperationException>(() => slots.MarkPlaying(9));
    }

    [Fact]
    public void InvalidatingWritesKeepsThePhaseButExpiresPriorCallbacks()
    {
        var slots = new ReplaySlotRegistry();
        var playing = slots.MarkPlaying(slots.LoadAndClaim(4).Slot);

        Assert.True(slots.InvalidateWrites(4));
        Assert.True(slots.TryGet(4, out var invalidated));
        Assert.Equal(ReplaySlotPhase.Playing, invalidated.Phase);
        Assert.True(invalidated.Epoch > playing.Epoch);
        Assert.False(slots.IsCurrentEpoch(4, playing.Epoch));
        Assert.True(slots.IsCurrentEpoch(4, invalidated.Epoch));
    }

    [Fact]
    public void ClearDoesNotLetOldCallbacksMatchANewSlotIncarnation()
    {
        var slots = new ReplaySlotRegistry();
        var beforeClear = slots.LoadAndClaim(6);

        slots.Clear();
        var afterClear = slots.LoadAndClaim(6);

        Assert.True(afterClear.Epoch > beforeClear.Epoch);
        Assert.False(slots.IsCurrentEpoch(6, beforeClear.Epoch));
    }

    [Fact]
    public void LoopWaitsForEveryParticipantAndDoesNotIncludeNonLoopingSlots()
    {
        var slots = new ReplaySlotRegistry();
        foreach (var slot in new[] { 2, 5, 8 })
            slots.LoadAndClaim(slot);
        slots.MarkPlaying(2, loop: true);
        slots.MarkPlaying(5, loop: true);
        slots.MarkPlaying(8);

        Assert.False(slots.IsCompletedLoop([]));
        Assert.False(slots.IsCompletedLoop([2]));
        Assert.True(slots.IsCompletedLoop([2, 5]));
        Assert.False(slots.IsCompletedLoop([2, 5, 8]));
    }

    [Fact]
    public void HandoffAndStopRemoveLoopIntentIncludingFinishedWaiters()
    {
        var slots = new ReplaySlotRegistry();
        slots.MarkPlaying(slots.LoadAndClaim(2).Slot, loop: true);
        slots.MarkPlaying(slots.LoadAndClaim(5).Slot, loop: true);

        slots.Release(5);
        Assert.True(slots.IsCompletedLoop([2]));
        Assert.False(slots.IsCompletedLoop([2, 5]));
        Assert.True(slots.TryGet(5, out var handedOff));
        Assert.False(handedOff.Loop);

        slots.Release(2);
        Assert.False(slots.IsCompletedLoop([2]));
        Assert.False(slots.IsCompletedLoop([]));
    }

    [Fact]
    public void LoopAndOwnerChangesExpireDeferredProjectileAndUtilityWrites()
    {
        var slots = new ReplaySlotRegistry();
        var first = slots.MarkPlaying(slots.LoadAndClaim(2).Slot, loop: true);
        var projectileBoundary = slots.CapturePlaybackBoundary();
        Assert.True(slots.IsCurrentPlaybackFromBoundary(2, projectileBoundary));

        // A second iteration keeps its loop intention while invalidating the
        // old utility callback and the projectile born before the boundary.
        slots.InvalidateWrites(2);
        var second = slots.MarkPlaying(2, loop: true);
        Assert.True(second.Loop);
        Assert.False(slots.IsCurrentEpoch(2, first.Epoch));
        Assert.False(slots.IsCurrentPlaybackFromBoundary(2, projectileBoundary));
        Assert.True(slots.IsCurrentPlaybackFromBoundary(2, slots.CapturePlaybackBoundary()));

        var secondBoundary = slots.CapturePlaybackBoundary();
        slots.Release(2);
        Assert.False(slots.IsCurrentPlaybackFromBoundary(2, secondBoundary));
        slots.Claim(2);
        slots.MarkPlaying(2);
        Assert.False(slots.IsCurrentPlaybackFromBoundary(2, secondBoundary));
        Assert.False(slots.IsCompletedLoop([2]));
    }

    [Fact]
    public void ProjectileBornDuringPreparationCannotBindToLaterPlayback()
    {
        var slots = new ReplaySlotRegistry();
        slots.LoadAndClaim(2);
        var projectileBoundary = slots.CapturePlaybackBoundary();
        slots.MarkPlaying(2, loop: true);

        Assert.False(slots.IsCurrentPlaybackFromBoundary(2, projectileBoundary));
        Assert.True(slots.IsCurrentPlaybackFromBoundary(2, slots.CapturePlaybackBoundary()));
    }

    [Fact]
    public void ManualSlotLoopsDoNotAcquireRoundMediaPlayback()
    {
        var slots = new ReplaySlotRegistry();
        var manual = slots.MarkPlaying(slots.LoadAndClaim(2).Slot, loop: true);
        Assert.False(manual.LoopRoundMedia);

        var round = slots.MarkPlaying(2, loop: true, roundMedia: true);
        Assert.True(round.LoopRoundMedia);
        slots.InvalidateWrites(2);
        Assert.True(slots.TryGet(2, out var next));
        Assert.True(next.LoopRoundMedia);

        slots.Release(2);
        Assert.False(slots.MarkPlaying(2, loop: true).LoopRoundMedia);
    }
}
