/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct ReplayTeamAvatarEvidence(
    ulong SteamId,
    string ContentKey);

internal static class ReplayTeamAvatarPolicy
{
    internal static string? FindSharedContentKey(
        IReadOnlyCollection<ulong> teamSteamIds,
        IEnumerable<ReplayTeamAvatarEvidence> avatarEvidence)
    {
        var roster = teamSteamIds
            .Where(steamId => steamId != 0)
            .ToHashSet();
        if (roster.Count == 0)
            return null;

        var candidates = avatarEvidence
            .Where(item => roster.Contains(item.SteamId) && !string.IsNullOrWhiteSpace(item.ContentKey))
            .GroupBy(item => item.SteamId)
            .Select(group => group.First())
            .ToArray();
        var minimumEvidence = Math.Min(2, roster.Count);
        if (candidates.Length < minimumEvidence)
            return null;

        var unique = candidates
            .Select(item => item.ContentKey.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }
}
