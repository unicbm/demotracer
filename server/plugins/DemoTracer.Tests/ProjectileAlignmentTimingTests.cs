/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ProjectileAlignmentTimingTests
{
    [Fact]
    public void PreservesRemainingLifetimeWhenBirthStateIsAppliedLater()
    {
        var translated = ProjectileAlignmentTiming.TranslateDeadline(
            deadline: 102.0f,
            sourceTime: 100.0f,
            targetTime: 100.015625f);

        Assert.Equal(102.015625f, translated);
        Assert.Equal(2.0f, translated - 100.015625f);
    }

    [Theory]
    [InlineData(0.0f, 100.0f, 100.015625f)]
    [InlineData(99.0f, 100.0f, 100.015625f)]
    [InlineData(102.0f, 100.0f, 100.0f)]
    [InlineData(102.0f, 100.0f, 99.0f)]
    public void LeavesInactiveOrNonDelayedDeadlinesAlone(
        float deadline,
        float sourceTime,
        float targetTime)
    {
        Assert.Equal(
            deadline,
            ProjectileAlignmentTiming.TranslateDeadline(deadline, sourceTime, targetTime));
    }
}
