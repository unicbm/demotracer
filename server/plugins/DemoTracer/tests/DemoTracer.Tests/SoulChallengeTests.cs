/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DemoTracer;

namespace DemoTracer.Tests;

public sealed class SoulChallengeTests
{
    [Fact]
    public void DefeatRetriesSameRoundAndVictoryCompletesWithStatistics()
    {
        var soul = new SoulChallengeState("manifest.json", 7, 2);
        Assert.False(soul.FinishRound(3, 1)); // Preparation/restart is not an attempt.
        Assert.True(soul.BeginAttempt(10));
        soul.Kills = 2;
        soul.Deaths = 1;
        soul.Damage = 180;
        Assert.True(soul.FinishRound(3, 40));
        Assert.True(soul.Active);
        Assert.Equal(SoulChallengePhase.Retrying, soul.Phase);
        Assert.False(soul.FinishRound(3, 41)); // Duplicate round_end must not add a loss.
        Assert.True(soul.BeginAttempt(50));
        Assert.Equal(0, soul.Kills);
        Assert.Equal(7, soul.SourceRound);
        Assert.True(soul.FinishRound(2, 70));
        Assert.False(soul.Active);
        Assert.False(soul.BeginAttempt(80)); // Single challenge; no next level.
        Assert.Equal(2, soul.AttemptNumber);
        Assert.Equal(new SoulAttemptResult(1, SoulAttemptOutcome.Defeat, 30, 2, 1, 180), soul.Results[0]);
        Assert.Equal(SoulAttemptOutcome.Victory, soul.Results[1].Outcome);
    }

    [Fact]
    public void PlayerDeathDoesNotFailAWinningTeam()
    {
        var soul = new SoulChallengeState("manifest.json", 0, 3);
        soul.BeginAttempt(0);
        soul.Deaths++;
        Assert.True(soul.Fighting);
        Assert.True(soul.FinishRound(3, 20));
        Assert.Equal(SoulAttemptOutcome.Victory, Assert.Single(soul.Results).Outcome);
    }

    [Fact]
    public void ManualStopRecordsInterruptionWithoutAFalseDefeat()
    {
        var soul = new SoulChallengeState("manifest.json", 0, 2);
        soul.BeginAttempt(10);
        Assert.False(soul.FinishRound(0, 15));
        soul.Stop(20);
        soul.Stop(21);
        Assert.False(soul.Active);
        Assert.Equal(SoulAttemptOutcome.Interrupted, Assert.Single(soul.Results).Outcome);
        Assert.False(soul.FinishRound(3, 22));
    }
}
