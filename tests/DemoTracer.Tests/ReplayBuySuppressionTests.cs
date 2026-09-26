/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayBuySuppressionTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    public void OnlyOwnedSafeLoadedReplaySlotsHaveClientBuyingSuppressed(
        bool replayLoaded,
        bool replayOwned,
        bool replaySlotSafe,
        bool expected)
    {
        Assert.Equal(
            expected,
            DemoTracerPlugin.ShouldSuppressReplayBuyCommand(
                replayLoaded,
                replayOwned,
                replaySlotSafe));
    }
}
