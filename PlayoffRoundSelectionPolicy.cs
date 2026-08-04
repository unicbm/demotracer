/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct PlayoffRoundCandidate(
    int Round,
    bool PistolRound,
    string? EconomyClass,
    IReadOnlyList<ulong> ReplaySteamIds);

internal readonly record struct PlayoffCoverageCounts(
    int FirstRosterAsT,
    int FirstRosterAsCt,
    int SecondRosterAsT,
    int SecondRosterAsCt)
{
    public bool IsEligible(int minimumPerRosterSide)
        => minimumPerRosterSide > 0 &&
           FirstRosterAsT >= minimumPerRosterSide &&
           FirstRosterAsCt >= minimumPerRosterSide &&
           SecondRosterAsT >= minimumPerRosterSide &&
           SecondRosterAsCt >= minimumPerRosterSide;
}

internal static class PlayoffRoundSelectionPolicy
{
    public static int[] FindEligibleRounds(
        IEnumerable<PlayoffRoundCandidate> candidates,
        IReadOnlySet<ulong> requiredSteamIds)
    {
        if (requiredSteamIds.Count == 0)
            return [];

        return candidates
            .Where(candidate =>
                !candidate.PistolRound &&
                string.Equals(candidate.EconomyClass, "full", StringComparison.OrdinalIgnoreCase) &&
                CoversRosterExactlyOnce(candidate.ReplaySteamIds, requiredSteamIds))
            .Select(candidate => candidate.Round)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static bool CoversRosterExactlyOnce(
        IReadOnlyList<ulong> replaySteamIds,
        IReadOnlySet<ulong> requiredSteamIds)
    {
        var counts = replaySteamIds
            .Where(steamId => steamId != 0)
            .GroupBy(steamId => steamId)
            .ToDictionary(group => group.Key, group => group.Count());
        return requiredSteamIds.All(steamId =>
            counts.TryGetValue(steamId, out var count) && count == 1);
    }
}
