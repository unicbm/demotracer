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
        var weaponDefIndices = new int[replay.Ticks.Length];
        for (var i = 0; i < replay.Ticks.Length; i++)
            weaponDefIndices[i] = replay.Ticks[i].WeaponDefIndex;
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
            weaponDefIndices,
            roundStartOrigin);
    }
}
