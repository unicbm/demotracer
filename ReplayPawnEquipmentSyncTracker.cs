/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct ReplayPawnEquipmentIdentity(
    int UserId,
    uint PawnEntityHandle,
    long ReplayIdentityGeneration,
    ulong ReplaySteamId);

internal sealed class ReplayPawnEquipmentSyncTracker
{
    private readonly Dictionary<int, ReplayPawnEquipmentIdentity> _syncedBySlot = [];

    internal bool IsSynced(int slot, ReplayPawnEquipmentIdentity identity)
        => _syncedBySlot.TryGetValue(slot, out var current) && current == identity;

    internal void MarkSynced(int slot, ReplayPawnEquipmentIdentity identity)
        => _syncedBySlot[slot] = identity;

    internal void Invalidate(int slot)
        => _syncedBySlot.Remove(slot);

    internal void Clear()
        => _syncedBySlot.Clear();
}
