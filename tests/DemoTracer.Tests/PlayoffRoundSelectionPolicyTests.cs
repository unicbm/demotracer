/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer.Tests;

public sealed class PlayoffRoundSelectionPolicyTests
{
    private static readonly HashSet<ulong> Roster = [101, 102];

    [Fact]
    public void SelectsOnlyLongGunNonPistolRoundsCoveringTheRosterExactlyOnce()
    {
        var candidates = new[]
        {
            Candidate(8, pistol: false, primary: 7, 101, 102),
            Candidate(9, pistol: true, primary: 7, 101, 102),
            Candidate(10, pistol: false, primary: 34, 101, 102),
            Candidate(11, pistol: false, primary: 7, 101),
            Candidate(12, pistol: false, primary: 7, 101, 102, 102),
            Candidate(8, pistol: false, primary: 7, 101, 102)
        };

        Assert.Equal([8], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
    }

    [Fact]
    public void ExtraReplayPlayersDoNotInvalidateCompleteRosterCoverage()
    {
        var candidates = new[]
        {
            Candidate(18, pistol: false, primary: 16, 101, 102, 999)
        };

        Assert.Equal([18], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
    }

    [Fact]
    public void EmptyRosterHasNoEligibleRound()
    {
        var candidates = new[]
        {
            Candidate(8, pistol: false, primary: 7, 101, 102)
        };

        Assert.Empty(PlayoffRoundSelectionPolicy.FindEligibleRounds(
            candidates,
            new HashSet<ulong>()));
    }

    [Theory]
    [InlineData(2, 2, 2, 2, true)]
    [InlineData(3, 4, 2, 5, true)]
    [InlineData(1, 2, 2, 2, false)]
    [InlineData(2, 0, 2, 2, false)]
    public void RequiresTwoLongGunSamplesForBothRostersOnBothSides(
        int firstAsT,
        int firstAsCt,
        int secondAsT,
        int secondAsCt,
        bool expected)
    {
        Assert.Equal(
            expected,
            new PlayoffCoverageCounts(firstAsT, firstAsCt, secondAsT, secondAsCt)
                .IsEligible(minimumPerRosterSide: 2));
    }

    private static PlayoffRoundCandidate Candidate(
        int round,
        bool pistol,
        int primary,
        params ulong[] steamIds)
        => new(round, pistol, steamIds.Select(id => new PlayoffPlayerLoadout(id, [primary, 4, 42])).ToArray());

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(60)]
    public void RifleAndSniperInventoriesBelongToLongGunPool(int primary)
        => Assert.Equal(PlayoffWeaponPool.LongGuns,
            PlayoffRoundSelectionPolicy.ClassifyLoadout([42, 4, primary, 44, 45]));

    [Theory]
    [InlineData(34, "Smgs")]
    [InlineData(35, "Shotguns")]
    [InlineData(28, "MachineGuns")]
    [InlineData(1, "Pistols")]
    public void OtherPrimaryTypesStayInSeparatePools(int weapon, string pool)
        => Assert.Equal(pool, PlayoffRoundSelectionPolicy.ClassifyLoadout([42, 4, weapon]).ToString());

    [Fact]
    public void OvertimeRequiresLongGunsAcrossTheRecordedSideIncludingUnselectedPlayers()
    {
        PlayoffRoundCandidate[] candidates =
        [
            new(5, false, [new(101, [7]), new(102, [9]), new(999, [16])]),
            new(6, false, [new(101, [7]), new(102, [9]), new(999, [34])]),
            new(7, false, [new(101, [7]), new(102, null)]),
            new(8, false, [new(101, [7]), new(102, [])])
        ];

        Assert.Equal([5], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
        Assert.Equal(PlayoffWeaponPool.Mixed, PlayoffRoundSelectionPolicy.ClassifyRound(candidates[1].Players));
    }

    [Fact]
    public void ManifestSelectionIgnoresEconomyLabelsActiveKnifeAndLaterWeaponPickups()
    {
        var manifestType = typeof(DemoTracerPlugin).GetNestedType("ConversionManifest", BindingFlags.NonPublic)!;
        var manifest = JsonSerializer.Deserialize("""
            {
              "rounds": [
                { "round": 8, "t_economy": { "class": "eco" } },
                { "round": 9, "t_economy": { "class": "full" } },
                { "round": 10, "t_economy": { "class": "full" } }
              ],
              "files": [
                { "round": 8, "side": "t", "steam_id": 101, "first_weapon_def_index": 42,
                  "loadout": { "weapon_def_indices": [42, 7, 4] } },
                { "round": 9, "side": "t", "steam_id": 101, "preload_weapon_def_indices": [7],
                  "loadout": { "weapon_def_indices": [42, 34, 4] } },
                { "round": 10, "side": "t", "steam_id": 101, "first_weapon_def_index": 7 }
              ]
            }
            """, manifestType)!;
        var method = typeof(DemoTracerPlugin).GetMethod("FindEligiblePlayoffRounds", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal([8], (int[])method.Invoke(null, [manifest, "t", new HashSet<ulong> { 101 }])!);
    }
}

public sealed class PlayoffSourceSelectionTests
{
    [Theory]
    [InlineData(false, 8, 9)]
    [InlineData(true, 18, 19)]
    public void LivePrefetchResolvesTheUpcomingRosterAtOvertimeSwap(bool swap, int tRound, int ctRound)
    {
        var first = new HashSet<ulong> { 101, 102 };
        var second = new HashSet<ulong> { 201, 202 };
        var selection = new PlayoffSourceSelection(first, second, new(8, 9, "same"), new(18, 19, "swapped"));
        var players = first.Select(id => (Id: id, Team: CsTeam.Terrorist))
            .Concat(second.Select(id => (Id: id, Team: CsTeam.CounterTerrorist)))
            .ToArray();
        var upcomingT = players.Where(player => ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(player.Team, swap) ==
            CsTeam.Terrorist).Select(player => player.Id).ToHashSet();
        var upcomingCt = players.Where(player => ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(player.Team, swap) ==
            CsTeam.CounterTerrorist).Select(player => player.Id).ToHashSet();

        Assert.True(selection.TryResolve(upcomingT, upcomingCt, out var sources));
        Assert.Equal(tRound, sources.TRound);
        Assert.Equal(ctRound, sources.CtRound);

        // A fresh prefetch on the new sides must keep working next round and
        // at the next overtime swap; never reuse a previous round's flip flag.
        var next = new PlayoffSourceSelection(upcomingT, upcomingCt, sources, new(28, 29, "next swap"));
        Assert.True(next.TryResolve(upcomingT, upcomingCt, out var same));
        Assert.Equal(sources, same);
        Assert.True(next.TryResolve(upcomingCt, upcomingT, out var swapped));
        Assert.Equal(28, swapped.TRound);
    }

    [Fact]
    public void ChangedRosterCannotConsumeAnUnrelatedPrefetchedSelection()
    {
        var selection = new PlayoffSourceSelection(new HashSet<ulong> { 101 }, new HashSet<ulong> { 201 },
            new(8, 9, "same"), new(18, 19, "swapped"));
        Assert.False(selection.TryResolve(new HashSet<ulong> { 301 }, new HashSet<ulong> { 201 }, out _));
    }
}

public sealed class PlayoffReplayFallbackPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainsLoadedReplayUntilPendingPlayoffPrefetchIsReady(
        bool preparationPending,
        bool prefetchReady)
    {
        Assert.Equal(
            !preparationPending || !prefetchReady,
            PlayoffReplayFallbackPolicy.ShouldRetainLoadedReplay(
                planReady: true,
                prepared: false,
                preparationPending,
                prefetchReady,
                hasLoadedReplay: true));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void DoesNotRetainFallbackOutsideAnUnpreparedPlayoffPlan(
        bool planReady,
        bool prepared,
        bool hasLoadedReplay)
    {
        Assert.False(PlayoffReplayFallbackPolicy.ShouldRetainLoadedReplay(
            planReady,
            prepared,
            preparationPending: true,
            prefetchReady: false,
            hasLoadedReplay));
    }
}
