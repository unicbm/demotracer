/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayCommunicationTests
{
    [Theory]
    [InlineData(89, false)] // Before the retained freeze preroll.
    [InlineData(90, true)]
    [InlineData(100, true)] // Live round start.
    [InlineData(150, true)] // Final living post snapshot.
    [InlineData(151, false)] // Original post-death comms remain available.
    public void MuteWindowUsesTheRecordedLivingInterval(int demoTick, bool expected)
    {
        Assert.Equal(expected, DemoTracerPlugin.IsWithinRecordedLife(
            demoTick, liveStartTick: 100, tickCount: 60, playStartTickIndex: 10));
    }

    [Fact]
    public void MissingRoundTimelineDoesNotInventADeathWindow()
    {
        Assert.False(DemoTracerPlugin.IsWithinRecordedLife(
            100, liveStartTick: 0, tickCount: 60, playStartTickIndex: 10));
    }
}
