/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DemoTracerBotHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderCrosshairPresentationTests
{
    [Fact]
    public void ContractNormalizesWithinUtf8Limit()
    {
        Assert.True(DemoTracerBotHiderContract.TryNormalizeCrosshairCode(
            "  CSGO-test  ", out var normalized));
        Assert.Equal("CSGO-test", normalized);
    }

    [Fact]
    public void ContractRejectsCrosshairPastUtf8Limit()
    {
        var source = new string('x', DemoTracerBotHiderContract.MaxCrosshairCodeUtf8Bytes + 1);
        Assert.False(DemoTracerBotHiderContract.TryNormalizeCrosshairCode(source, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void ContractRejectsEmbeddedNull()
    {
        Assert.False(DemoTracerBotHiderContract.TryNormalizeCrosshairCode("CSGO-x\0y", out var normalized));
        Assert.Null(normalized);
    }
}
