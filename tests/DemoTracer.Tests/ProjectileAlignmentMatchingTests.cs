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
        ReplayProjectileEvent[] events = [Fire(100, 46), Fire(105, 48)];
        Assert.Equal(1, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 0, 100, ReplayProjectileKind.Molotov, 48));
        Assert.Equal(0, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 0, 105, ReplayProjectileKind.Molotov, 46));
        Assert.Equal(-1, DemoTracerPlugin.FindProjectileAlignEvent(
            events, 1, 105, ReplayProjectileKind.Molotov, 46));
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
}
