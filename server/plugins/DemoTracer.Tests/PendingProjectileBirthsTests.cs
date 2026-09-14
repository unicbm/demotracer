/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class PendingProjectileBirthsTests
{
    private static PendingProjectileBirth Birth(uint handle = 0x8001)
        => new(1, handle, ReplayProjectileKind.Molotov, 100, 6400, true, default);

    [Fact]
    public void FirstPhysicsConsumesBirthOnceWithoutWaitingForAnotherTick()
    {
        var pending = new PendingProjectileBirths();
        pending.Track(123, Birth());
        Assert.True(pending.TryConsume(123, 0x8001, out var birth));
        Assert.Equal(6400, birth.ObservedSpawnTick);
        Assert.False(pending.TryConsume(123, 0x8001, out _));
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void ReusedPointerAndIndexCannotConsumeAnotherSerialsBirth()
    {
        var pending = new PendingProjectileBirths();
        pending.Track(123, Birth());
        Assert.False(pending.TryConsume(123, 0x10001, out _));
        Assert.False(pending.TryConsume(123, 0x8001, out _));
        pending.Track(123, Birth(0x10001));
        Assert.True(pending.TryConsume(123, 0x10001, out _));
    }

    [Fact]
    public void DeleteAndLifecycleResetCancelUnsimulatedBirths()
    {
        var pending = new PendingProjectileBirths();
        pending.Track(123, Birth());
        pending.Track(456, Birth());
        pending.Remove(123);
        Assert.False(pending.TryConsume(123, 0x8001, out _));
        Assert.True(pending.TryPeek(456, out _));
        pending.Clear();
        Assert.False(pending.TryConsume(456, 0x8001, out _));
    }

    [Fact]
    public void RestartBeforeFirstPhysicsCannotAdoptAnOldPlaybackBoundary()
    {
        var slots = new ReplaySlotRegistry();
        slots.LoadAndClaim(1);
        slots.MarkPlaying(1);
        var pending = new PendingProjectileBirths();
        pending.Track(123, Birth() with { PlaybackBoundary = slots.CapturePlaybackBoundary() });
        slots.Release(1);
        slots.Claim(1);
        slots.MarkPlaying(1);
        Assert.True(pending.TryConsume(123, 0x8001, out var birth));
        Assert.False(slots.IsCurrentPlaybackFromBoundary(1, birth.PlaybackBoundary));
    }

    [Fact]
    public void HandoffRevokesOnlyTheReleasedSlotsBirthPermission()
    {
        var slots = new ReplaySlotRegistry();
        foreach (var slot in new[] { 1, 2 })
        {
            slots.LoadAndClaim(slot);
            slots.MarkPlaying(slot);
        }
        var pending = new PendingProjectileBirths();
        pending.Track(123, Birth() with { PlaybackBoundary = slots.CapturePlaybackBoundary() });
        pending.CancelSlot(1);
        Assert.True(pending.TryPeek(123, out var birth));
        Assert.False(slots.IsCurrentPlaybackFromBoundary(1, birth.PlaybackBoundary));
        Assert.True(slots.IsCurrentPlaybackFromBoundary(2, birth.PlaybackBoundary));
        pending.CancelSlot(2);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void NativeHookIncludesTheFifthWin64StackArgument()
    {
        var field = typeof(ProjectilePhysicsHook).GetField("_function",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(Enumerable.Repeat(typeof(nint), 5), field.FieldType.GenericTypeArguments);
        Assert.StartsWith("MemoryFunctionVoid", field.FieldType.Name);
    }

    [Fact]
    public void ImageValidationIncludesTheLastCompleteVtablePointer()
    {
        var minimum = ProjectilePhysicsHook.VtableSlotRvas.ToArray().Max() + sizeof(long);
        Assert.True(ProjectilePhysicsHook.OffsetsFitImage(minimum));
        Assert.False(ProjectilePhysicsHook.OffsetsFitImage(minimum - 1));
        Assert.False(ProjectilePhysicsHook.OffsetsFitImage(0));
    }
}
