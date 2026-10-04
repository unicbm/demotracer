/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplaySequenceContinuityPolicyTests
{
    [Fact]
    public void FindsTheFirstMissingRoundAfterTheRequestedStart()
    {
        Assert.Equal(
            11,
            ReplaySequenceContinuityPolicy.FindFirstMissingRound(
                [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13],
                startRound: 0));
    }

    [Fact]
    public void AllowsAContiguousSuffixAfterAnEarlierGap()
    {
        Assert.Null(ReplaySequenceContinuityPolicy.FindFirstMissingRound(
            [0, 1, 3, 4, 5],
            startRound: 3));
    }

    [Fact]
    public void AllowsAContiguousSequence()
    {
        Assert.Null(ReplaySequenceContinuityPolicy.FindFirstMissingRound(
            [4, 5, 6],
            startRound: 4));
    }
}
