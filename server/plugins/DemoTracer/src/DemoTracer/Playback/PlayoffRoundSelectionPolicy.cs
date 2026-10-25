/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct PlayoffRoundSources(int TRound, int CtRound, string Reason);

internal sealed record PlayoffSourceSelection(
    IReadOnlySet<ulong> TRoster,
    IReadOnlySet<ulong> CtRoster,
    PlayoffRoundSources? SameSides,
    PlayoffRoundSources? SwappedSides)
{
    public bool TryResolve(
        IReadOnlySet<ulong> upcomingTRoster,
        IReadOnlySet<ulong> upcomingCtRoster,
        out PlayoffRoundSources sources)
        => TryResolve(upcomingTRoster, upcomingCtRoster, out sources, out _);

    public bool TryResolve(
        IReadOnlySet<ulong> upcomingTRoster,
        IReadOnlySet<ulong> upcomingCtRoster,
        out PlayoffRoundSources sources,
        out bool swapped)
    {
        swapped = false;
        if (TRoster.SetEquals(upcomingTRoster) && CtRoster.SetEquals(upcomingCtRoster) &&
            SameSides is { } same)
        {
            sources = same;
            return true;
        }
        if (TRoster.SetEquals(upcomingCtRoster) && CtRoster.SetEquals(upcomingTRoster) &&
            SwappedSides is { } swappedSources)
        {
            sources = swappedSources;
            // The resolved source and its prefetch cache must use the same orientation.
            swapped = true;
            return true;
        }

        sources = default;
        return false;
    }
}

internal readonly record struct PlayoffRoundCandidate(
    int Round,
    bool PistolRound,
    IReadOnlyList<PlayoffPlayerLoadout> Players);

internal readonly record struct PlayoffPlayerLoadout(ulong SteamId, IReadOnlyList<int>? Weapons);

internal enum PlayoffWeaponPool
{
    Unknown,
    Pistols,
    Smgs,
    Shotguns,
    MachineGuns,
    LongGuns,
    Mixed
}

internal sealed class PlayoffRoundHistory
{
    private readonly Dictionary<(string Side, string Roster, int Round), int> _uses = [];
    private readonly Dictionary<(string Side, string Roster), int> _previous = [];

    public int Choose(string side, IReadOnlySet<ulong> roster, IReadOnlyList<int> candidates)
    {
        var key = string.Join(',', roster.Order());
        var previous = _previous.GetValueOrDefault((side, key), -1);
        var available = candidates.Where(round => candidates.Count == 1 || round != previous).ToArray();
        var minimumUses = available.Min(round => _uses.GetValueOrDefault((side, key, round)));
        var choices = available.Where(round => _uses.GetValueOrDefault((side, key, round)) == minimumUses).ToArray();
        return choices[Random.Shared.Next(choices.Length)];
    }

    public void Record(string side, IReadOnlySet<ulong> roster, int round)
    {
        if (roster.Count > 0 && round >= 0)
        {
            var key = string.Join(',', roster.Order());
            _uses[(side, key, round)] = _uses.GetValueOrDefault((side, key, round)) + 1;
            _previous[(side, key)] = round;
        }
    }

    public void Clear()
    {
        _uses.Clear();
        _previous.Clear();
    }
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
                CoversRosterExactlyOnce(candidate.Players.Select(player => player.SteamId).ToArray(), requiredSteamIds) &&
                candidate.Players.Where(player => requiredSteamIds.Contains(player.SteamId))
                    .All(player => ClassifyLoadout(player.Weapons) != PlayoffWeaponPool.Unknown))
            .Select(candidate => candidate.Round)
            .Distinct()
            .Order()
            .ToArray();
    }

    internal static PlayoffWeaponPool ClassifyRound(IReadOnlyList<PlayoffPlayerLoadout> players)
    {
        var pools = players.Select(player => ClassifyLoadout(player.Weapons)).Distinct().ToArray();
        if (pools.Length == 0 || pools.Contains(PlayoffWeaponPool.Unknown))
            return PlayoffWeaponPool.Unknown;
        return pools.Length == 1 ? pools[0] : PlayoffWeaponPool.Mixed;
    }

    internal static PlayoffWeaponPool ClassifyLoadout(IReadOnlyList<int>? weapons)
    {
        if (weapons == null || weapons.Count == 0)
            return PlayoffWeaponPool.Unknown;

        // Only the manifest's live-start inventory is evidence here. The
        // active weapon may be a knife, and preload lists include later pickups.
        var primaries = weapons.Select(def => def switch
        {
            7 or 8 or 10 or 13 or 16 or 39 or 60 => PlayoffWeaponPool.LongGuns, // rifles
            9 or 11 or 38 or 40 => PlayoffWeaponPool.LongGuns, // sniper rifles
            17 or 19 or 23 or 24 or 26 or 33 or 34 => PlayoffWeaponPool.Smgs,
            25 or 27 or 29 or 35 => PlayoffWeaponPool.Shotguns,
            14 or 28 => PlayoffWeaponPool.MachineGuns,
            _ => PlayoffWeaponPool.Unknown
        }).Where(pool => pool != PlayoffWeaponPool.Unknown).Distinct().ToArray();
        if (primaries.Length > 1)
            return PlayoffWeaponPool.Mixed;
        if (primaries.Length == 1)
            return primaries[0];

        return weapons.Any(def => def is 1 or 2 or 3 or 4 or 30 or 32 or 36 or 61 or 63 or 64)
            ? PlayoffWeaponPool.Pistols
            : PlayoffWeaponPool.Unknown;
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
