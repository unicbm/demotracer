/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayTeamAvatarPolicyTests
{
    private static readonly ulong[] Team =
    [
        76561198000000001UL,
        76561198000000002UL,
        76561198000000003UL,
    ];

    [Fact]
    public void SharedAvatarFromMultipleTeamMembersIsTeamEvidence()
    {
        var key = ReplayTeamAvatarPolicy.FindSharedContentKey(
            Team,
            [
                new(Team[0], "team-avatar"),
                new(Team[1], "TEAM-AVATAR"),
                new(76561198000000009UL, "other-team"),
            ]);

        Assert.Equal("team-avatar", key);
    }

    [Fact]
    public void SinglePlayerAvatarDoesNotBecomeTeamIdentity()
    {
        var key = ReplayTeamAvatarPolicy.FindSharedContentKey(
            Team,
            [new(Team[0], "player-avatar")]);

        Assert.Null(key);
    }

    [Fact]
    public void ConflictingTeamAvatarsAreRejected()
    {
        var key = ReplayTeamAvatarPolicy.FindSharedContentKey(
            Team,
            [
                new(Team[0], "first-avatar"),
                new(Team[1], "second-avatar"),
            ]);

        Assert.Null(key);
    }

    [Fact]
    public void OnePlayerReplayCanStillCarryItsOnlyTeamAvatar()
    {
        var key = ReplayTeamAvatarPolicy.FindSharedContentKey(
            [Team[0]],
            [new(Team[0], "solo-team-avatar")]);

        Assert.Equal("solo-team-avatar", key);
    }

    [Fact]
    public void AvatarOverrideCommandCarriesTheExactSlotForHudUserInfoRefresh()
    {
        var command = DemoTracerPlugin.BuildAvatarOverrideCommand(
            Team[0],
            "C:/avatar-cache/team.png",
            slot: 7);

        Assert.Equal(
            $"bc_avatar_override_probe {Team[0]} \"C:/avatar-cache/team.png\" 7",
            command);
    }
}
