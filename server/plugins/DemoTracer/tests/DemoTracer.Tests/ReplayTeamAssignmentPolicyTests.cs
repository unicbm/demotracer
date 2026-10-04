/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Modules.Utils;
using DemoTracer;

namespace DemoTracer.Tests;

public sealed class ReplayTeamAssignmentPolicyTests
{
    [Theory]
    [InlineData(CsTeam.Terrorist)]
    [InlineData(CsTeam.CounterTerrorist)]
    public void NormalRoundKeepsCurrentTeam(CsTeam currentTeam)
    {
        Assert.Equal(
            currentTeam,
            ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(
                currentTeam,
                switchingTeamsAtRoundReset: false));
    }

    [Theory]
    [InlineData(CsTeam.Terrorist, CsTeam.CounterTerrorist)]
    [InlineData(CsTeam.CounterTerrorist, CsTeam.Terrorist)]
    public void TeamResetTargetsUpcomingSpawnSide(CsTeam currentTeam, CsTeam expectedTeam)
    {
        Assert.Equal(
            expectedTeam,
            ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(
                currentTeam,
                switchingTeamsAtRoundReset: true));
    }

    [Fact]
    public void NonPlayingTeamIsNeverInventedDuringReset()
    {
        Assert.Equal(
            CsTeam.Spectator,
            ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(
                CsTeam.Spectator,
                switchingTeamsAtRoundReset: true));
    }

    [Theory]
    [InlineData(null, CsTeam.Terrorist, true)]
    [InlineData(CsTeam.Terrorist, CsTeam.Terrorist, true)]
    [InlineData(CsTeam.CounterTerrorist, CsTeam.CounterTerrorist, true)]
    [InlineData(CsTeam.Terrorist, CsTeam.CounterTerrorist, false)]
    public void SpawnWritesRequireManifestAndLiveTeamsToAgree(
        CsTeam? manifestTeam,
        CsTeam actualTeam,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayTeamAssignmentPolicy.LiveTeamMatches(manifestTeam, actualTeam));
    }

    [Theory]
    [InlineData(CsTeam.Terrorist, CsTeam.Terrorist, true)]
    [InlineData(CsTeam.Terrorist, CsTeam.CounterTerrorist, false)]
    [InlineData(CsTeam.CounterTerrorist, CsTeam.Terrorist, false)]
    [InlineData(null, CsTeam.Terrorist, false)]
    public void C4AlignmentRequiresReplayAndControllerToBothBeTerrorist(
        CsTeam? manifestTeam,
        CsTeam actualTeam,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayTeamAssignmentPolicy.CanAlignC4(manifestTeam, actualTeam));
    }

}
