/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal static class ReplayNativeMapper
{
    public static ReplayFileMetadata BuildMetadata(DtrReplayFile replay)
    {
        if (replay.PreparedMetadata is { } prepared)
            return prepared;
        // Consumers need the first weapon and the inventory set, not a second
        // per-tick array that they immediately scan and deduplicate again.
        var seenWeapons = new HashSet<int>();
        var weaponDefIndices = new List<int>();
        for (var i = 0; i < replay.Ticks.Length; i++)
            if (seenWeapons.Add(replay.Ticks[i].WeaponDefIndex))
                weaponDefIndices.Add(replay.Ticks[i].WeaponDefIndex);
        ReplayVector3? roundStartOrigin = null;
        if (replay.Ticks.Length > 0)
        {
            var snapshot = replay.Ticks[0].Pre;
            roundStartOrigin = new ReplayVector3(
                snapshot.OriginX,
                snapshot.OriginY,
                snapshot.OriginZ);
        }
        return new ReplayFileMetadata(
            replay.TickRate,
            replay.PlayStartTickIndex,
            replay.Ticks.Length,
            replay.Projectiles,
            replay.HighFidelity,
            weaponDefIndices.ToArray(),
            roundStartOrigin);
    }
}
