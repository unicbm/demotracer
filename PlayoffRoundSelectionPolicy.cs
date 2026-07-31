namespace DemoTracer;

internal readonly record struct PlayoffRoundCandidate(
    int Round,
    bool PistolRound,
    string? EconomyClass,
    IReadOnlyList<ulong> ReplaySteamIds);

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
