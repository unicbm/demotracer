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
    public void SelectsNonPistolRoundsWithKnownLoadoutsCoveringTheRosterExactlyOnce()
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

        Assert.Equal([8, 10], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
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

    [Fact]
    public void HistoryCyclesThroughCandidatesWithoutRepeatingAtCycleBoundary()
    {
        var history = new PlayoffRoundHistory();
        int[] candidates = [8, 10, 12];
        var played = new List<int>();
        for (var i = 0; i < 9; i++)
        {
            var round = history.Choose("t", Roster, candidates);
            history.Record("t", Roster, round);
            played.Add(round);
        }
        Assert.Equal(3, played.Take(3).Distinct().Count());
        Assert.Equal(3, played.Skip(3).Take(3).Distinct().Count());
        Assert.Equal(3, played.Skip(6).Distinct().Count());
        Assert.All(played.Zip(played.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void HistoryFollowsRosterAndSideAndOnlyRecordsPlayedSources()
    {
        var history = new PlayoffRoundHistory();
        history.Record("t", Roster, 8); // Last sequence round is already used.
        history.Record("ct", Roster, 10);
        Assert.Equal(10, history.Choose("t", new HashSet<ulong> { 102, 101 }, [8, 10]));
        Assert.Equal(10, history.Choose("t", Roster, [8, 10])); // Prefetch is not consumption.
        Assert.Equal(8, history.Choose("ct", Roster, [8, 10]));
        Assert.Equal(8, history.Choose("t", Roster, [8])); // A single candidate can repeat.
        Assert.Equal(12, history.Choose("t", Roster, [8, 12]));
        history.Clear();
        history.Record("t", Roster, 10);
        Assert.Equal(8, history.Choose("t", Roster, [8, 10]));
    }

    private static PlayoffRoundCandidate Candidate(
        int round,
        bool pistol,
        int primary,
        params ulong[] steamIds)
        => new(round, pistol, steamIds.Select(id => new PlayoffPlayerLoadout(id, [primary, 4, 42])).ToArray());

    [Fact]
    public void EligibilityRequiresAKnownFirearmInTheLiveStartInventory()
    {
        int[] firearms = [1, 2, 3, 4, 7, 8, 9, 10, 11, 13, 14, 16, 17, 19, 23, 24,
            25, 26, 27, 28, 29, 30, 32, 33, 34, 35, 36, 38, 39, 40, 60, 61, 63, 64];
        foreach (var weapon in firearms.Concat([-1, 0, 42, 43, 44, 45, 46, 47, 48, 49, 59, 999]))
        {
            PlayoffRoundCandidate[] candidates = [new(8, false,
                [new(101, [weapon]), new(102, [weapon])])];
            var selected = PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster);
            Assert.Equal(firearms.Contains(weapon), selected.SequenceEqual([8]));
        }
    }

    [Fact]
    public void MixedEquipmentAndUnselectedPlayersDoNotExcludeUsableRoutes()
    {
        PlayoffRoundCandidate[] candidates =
        [
            new(5, false, [new(101, [7]), new(102, [9]), new(999, [16])]),
            new(6, false, [new(101, [7]), new(102, [9]), new(999, [34])]),
            new(7, false, [new(101, [7]), new(102, null)]),
            new(8, false, [new(101, [7]), new(102, [])]),
            new(9, false, [new(101, [7]), new(102, [34]), new(999, null)]),
            new(10, false, [new(101, [4]), new(102, [1])])
        ];

        Assert.Equal([5, 6, 9, 10], PlayoffRoundSelectionPolicy.FindEligibleRounds(candidates, Roster));
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
            """, manifestType, DemoTracerPlugin.ManifestJsonOptions)!;
        var method = typeof(DemoTracerPlugin).GetMethod("FindEligiblePlayoffRounds", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal([8, 9], (int[])method.Invoke(null, [manifest, "t", new HashSet<ulong> { 101 }])!);
    }
}

public sealed class PlayoffSourceSelectionTests
{
    [Fact]
    public void MissingOppositeSideDoesNotInvalidateAnAvailableOrientation()
    {
        var first = new HashSet<ulong> { 101 };
        var second = new HashSet<ulong> { 201 };
        var selection = new PlayoffSourceSelection(first, second, new(8, 9, "same"), null);
        Assert.True(selection.TryResolve(first, second, out var sources, out _));
        Assert.Equal(8, sources.TRound);
        Assert.False(selection.TryResolve(second, first, out _, out _));
        var onlySwapped = selection with { SameSides = null, SwappedSides = new(18, 19, "swapped") };
        Assert.True(onlySwapped.TryResolve(second, first, out sources, out _));
        Assert.Equal(18, sources.TRound);
    }
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

        Assert.True(selection.TryResolve(upcomingT, upcomingCt, out var sources, out _));
        Assert.Equal(tRound, sources.TRound);
        Assert.Equal(ctRound, sources.CtRound);

        // A fresh prefetch on the new sides must keep working next round and
        // at the next overtime swap; never reuse a previous round's flip flag.
        var next = new PlayoffSourceSelection(upcomingT, upcomingCt, sources, new(28, 29, "next swap"));
        Assert.True(next.TryResolve(upcomingT, upcomingCt, out var same, out _));
        Assert.Equal(sources, same);
        Assert.True(next.TryResolve(upcomingCt, upcomingT, out var swapped, out _));
        Assert.Equal(28, swapped.TRound);
    }

    [Fact]
    public void ChangedRosterCannotConsumeAnUnrelatedPrefetchedSelection()
    {
        var selection = new PlayoffSourceSelection(new HashSet<ulong> { 101 }, new HashSet<ulong> { 201 },
            new(8, 9, "same"), new(18, 19, "swapped"));
        Assert.False(selection.TryResolve(new HashSet<ulong> { 301 }, new HashSet<ulong> { 201 }, out _, out _));
    }
}

public sealed class PlayoffReplayFallbackPolicyTests
{
    [Fact]
    public void SideSwapCannotReuseThePreviousSidesRoutesWhileDecoding()
        => Assert.False(PlayoffReplayFallbackPolicy.ShouldRetainLoadedReplay(
            planReady: true, prepared: false, preparationPending: true,
            prefetchReady: false, hasLoadedReplay: true, switchingTeamsAtRoundReset: true));

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
