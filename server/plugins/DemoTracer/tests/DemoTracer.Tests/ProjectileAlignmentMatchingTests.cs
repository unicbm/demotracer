/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ProjectileAlignmentMatchingTests
{
    private static ReplayProjectileEvent Fire(uint tick, int weapon)
        => new(tick, ReplayProjectileKind.Molotov, weapon,
            default, default, default, default, -1, "unknown", 0);

    [Fact]
    public void SharedNativeClassDoesNotAllowTheOtherFireWeaponToConsumeAnEvent()
    {
        ReplayProjectileEvent[] events = [Fire(100, 46), Fire(101, 48)];
        Assert.Equal(1, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 0, 101, ReplayProjectileKind.Molotov, 48));
        Assert.Equal(0, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 0, 101, ReplayProjectileKind.Molotov, 46));
        Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 1, 101, ReplayProjectileKind.Molotov, 46));
    }

    [Fact]
    public void UnknownRecordedVariantCanStillAlignButKnownMismatchCannot()
    {
        Assert.Equal(0, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(100, -1)], 0, 100, ReplayProjectileKind.Molotov, 48));
        Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(100, 46)], 0, 100, ReplayProjectileKind.Molotov, 48));
    }

    [Fact]
    public void MatchingStillRespectsConsumedEventsAndReplayTimeWindow()
    {
        ReplayProjectileEvent[] events = [Fire(100, 48), Fire(500, 48)];
        Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 1, 100, ReplayProjectileKind.Molotov, 48));
        Assert.Equal(1, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 1, 500, ReplayProjectileKind.Molotov, 48));
    }

    [Theory]
    [InlineData(98)]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(196)]
    public void NearbyDifferentThrowCannotSupplyBirthState(uint recordedTick)
        => Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(recordedTick, 46)], 0, 100, ReplayProjectileKind.Molotov, 46));

    [Fact]
    public void AmbiguousBirthsAreNeverResolvedByListOrderOrNearestTick()
    {
        Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(99, 46), Fire(100, 46)], 0, 100, ReplayProjectileKind.Molotov, 46));
        Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(100, 46), Fire(100, 46)], 0, 100, ReplayProjectileKind.Molotov, 46));
        Assert.Equal(1, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(99, 46), Fire(100, 46)], 1, 100, ReplayProjectileKind.Molotov, 46));
    }

    [Fact]
    public void UnsignedRecordedTickCannotWrapIntoTheCurrentCommand()
        => Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            [Fire(uint.MaxValue, 46)], 0, 0, ReplayProjectileKind.Molotov, 46));

    [Fact]
    public void MovementWithoutACollisionAlreadyClosesTheBirthWriteBoundary()
    {
        var position = new ReplayVector3(10, 20, 30);
        var velocity = new ReplayVector3(100, 200, 300);
        Assert.True(DemoTracerPlugin.IsUnsimulatedProjectile(position, velocity, position, velocity));
        Assert.False(DemoTracerPlugin.IsUnsimulatedProjectile(
            new(10, 20, 30.01f), velocity, position, velocity));
        Assert.False(DemoTracerPlugin.IsUnsimulatedProjectile(
            position, new(100, 200, 299), position, velocity));
        Assert.False(DemoTracerPlugin.IsUnsimulatedProjectile(null, velocity, position, velocity));
        var invalid = new ReplayVector3(float.PositiveInfinity, 0, 0);
        Assert.False(DemoTracerPlugin.IsUnsimulatedProjectile(invalid, velocity, invalid, velocity));
    }
}
