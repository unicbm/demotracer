/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal static partial class DtrReplayReader
{
    private static void ValidateInventorySnapshots(ReplayInventorySnapshot[] snapshots, int tickCount)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot == null || snapshot.SteamId == 0 || snapshot.TickIndex >= tickCount ||
                snapshot.ArmorValue is < 0 or > 100 || snapshot.WeaponDefCounts == null ||
                snapshot.WeaponDefCounts.Length > 64 || (snapshot.GearAcquired & ~7) != 0)
                throw new InvalidDataException("invalid inventory snapshot");
            var seen = new HashSet<int>();
            foreach (var item in snapshot.WeaponDefCounts)
                if (item == null || item.WeaponDefIndex is <= 0 or > ushort.MaxValue ||
                    item.Count is <= 0 or > 64 || !seen.Add(item.WeaponDefIndex))
                    throw new InvalidDataException("invalid inventory item count");
        }
    }

    private static void CompileInventoryAcquisitions(ReplayInventorySnapshot[] snapshots)
    {
        var previous = new Dictionary<ulong, ReplayInventorySnapshot>();
        foreach (var snapshot in snapshots)
        {
            previous.TryGetValue(snapshot.SteamId, out var before);
            var counts = before?.WeaponDefCounts.ToDictionary(item => item.WeaponDefIndex, item => item.Count);
            foreach (var item in snapshot.WeaponDefCounts)
                item.Acquired = item.Count > (counts?.GetValueOrDefault(item.WeaponDefIndex) ?? 0);
            snapshot.GearAcquired = (byte)((snapshot.ArmorValue > (before?.ArmorValue ?? 0) ? 1 : 0) |
                (snapshot.HasHelmet && before?.HasHelmet != true ? 2 : 0) |
                (snapshot.HasDefuser && before?.HasDefuser != true ? 4 : 0));
            previous[snapshot.SteamId] = snapshot;
        }
    }
}
