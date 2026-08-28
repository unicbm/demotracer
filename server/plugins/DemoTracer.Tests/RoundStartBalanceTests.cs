/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class RoundStartBalanceTests
{
    [Theory]
    [InlineData(false, 5_250U)]
    [InlineData(true, null)]
    public void RequiresOptInAndDemoEvidence(
        bool enabled,
        uint? evidence)
    {
        var resolved = ReplayRuntimePolicy.TryResolveRoundStartBalance(
            enabled,
            evidence,
            16_000,
            out var balance);

        Assert.False(resolved);
        Assert.Equal(0, balance);
    }

    [Fact]
    public void PreservesZeroAsPositiveEvidence()
    {
        var resolved = ReplayRuntimePolicy.TryResolveRoundStartBalance(
            enabled: true,
            evidence: 0,
            serverMaxMoney: 16_000,
            out var balance);

        Assert.True(resolved);
        Assert.Equal(0, balance);
    }

    [Fact]
    public void ClampsDemoEvidenceToCurrentServerMaximum()
    {
        var resolved = ReplayRuntimePolicy.TryResolveRoundStartBalance(
            enabled: true,
            evidence: 20_000,
            serverMaxMoney: 16_000,
            out var balance);

        Assert.True(resolved);
        Assert.Equal(16_000, balance);
    }
}
