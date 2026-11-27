/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class DtrRoundBannerPlaybackTests
{
    [Fact]
    public void OnlyPreparedRoundsGetAnOpeningBanner()
    {
        var plan = new ReplayPlanState { Armed = true, ArmedLabel = "single" };
        Assert.Null(plan.PreparedRoundStartLabel(false));
        plan.ArmedPrepared = true;
        Assert.Equal("single", plan.PreparedRoundStartLabel(false));
        plan.ClearArmed();

        plan.PlayoffPrepared = true;
        Assert.Null(plan.PreparedRoundStartLabel(false));
        Assert.Equal("playoff extra round 1", plan.PreparedRoundStartLabel(true));

        plan.SequenceActive = true;
        plan.SequencePrepared = true;
        plan.SequencePreparedRound = 15;
        Assert.Equal("sequence round 15", plan.PreparedRoundStartLabel(true));
        plan.SequenceNativeOnly = true;
        plan.PlayoffPrepared = false;
        Assert.Null(plan.PreparedRoundStartLabel(false));
    }
}
