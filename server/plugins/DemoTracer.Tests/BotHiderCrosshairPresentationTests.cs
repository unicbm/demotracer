/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotHiderImpl;
using DemoTracerBotHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderCrosshairPresentationTests
{
    [Fact]
    public void ContractNormalizesWithinUtf8Limit()
    {
        Assert.True(DemoTracerBotHiderContract.TryNormalizeCrosshairCode(
            "  CSGO-test  ",
            out var normalized));
        Assert.Equal("CSGO-test", normalized);
    }

    [Fact]
    public void ContractRejectsCrosshairPastUtf8Limit()
    {
        var source = new string('x', DemoTracerBotHiderContract.MaxCrosshairCodeUtf8Bytes + 1);

        Assert.False(DemoTracerBotHiderContract.TryNormalizeCrosshairCode(source, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData(null, "server-value", true)]
    [InlineData("", "", true)]
    [InlineData("CSGO-test", "CSGO-test", true)]
    [InlineData("", "server-value", false)]
    [InlineData("CSGO-test", "CSGO-other", false)]
    public void CrosshairReadbackComparisonRemainsExact(
        string? requested,
        string? actual,
        bool expected)
    {
        Assert.Equal(expected, BotHiderPresentationService.RequestedCrosshairMatches(requested, actual));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    public void EngineOwnedPingAndOptionalCrosshairNeverRollBackCoreIdentityLease(
        bool playerNameMatches,
        bool steamIdMatches,
        bool scoreboardFlairMatches,
        bool crosshairMatches,
        bool expected)
    {
        Assert.Equal(
            expected,
            BotHiderPresentationService.CanCommitSynchronousPresentationLease(
                playerNameMatches,
                steamIdMatches,
                scoreboardFlairMatches,
                crosshairMatches));
    }
}
