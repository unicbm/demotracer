/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

// Only acquisitions enqueue work. Consumption, damage and unrelated live items
// never cause a snapshot to be imposed on the pawn again.
internal sealed class ReplayInventoryTimeline(ReplayInventorySnapshot[] snapshots)
{
    private int _next;
    public Dictionary<int, int> PendingWeapons { get; } = [];
    public int? Armor { get; private set; }
    public bool? Helmet { get; private set; }
    public bool? Defuser { get; private set; }

    public void Start(uint cursor)
    {
        _next = 0;
        ReplayInventorySnapshot? initial = null;
        PendingWeapons.Clear();
        ClearGear();
        while (_next < snapshots.Length && snapshots[_next].TickIndex <= cursor)
            initial = snapshots[_next++];
        if (initial == null)
            return;
        foreach (var item in initial.WeaponDefCounts)
            if (item.Count > 0) PendingWeapons[item.WeaponDefIndex] = item.Count;
        Armor = initial.ArmorValue;
        Helmet = initial.HasHelmet;
        Defuser = initial.HasDefuser;
    }

    public bool Advance(uint cursor)
    {
        var changed = false;
        while (_next < snapshots.Length && snapshots[_next].TickIndex <= cursor)
        {
            var snapshot = snapshots[_next++];
            var counts = snapshot.WeaponDefCounts.ToDictionary(item => item.WeaponDefIndex, item => item.Count);
            // Invalidate a deferred grant if the demo no longer holds that item.
            foreach (var def in PendingWeapons.Keys.ToArray())
            {
                var remaining = counts.GetValueOrDefault(def);
                if (remaining <= 0) PendingWeapons.Remove(def);
                else PendingWeapons[def] = Math.Min(PendingWeapons[def], remaining);
            }
            foreach (var item in snapshot.WeaponDefCounts)
                if (item.Acquired) PendingWeapons[item.WeaponDefIndex] = item.Count;

            if ((snapshot.GearAcquired & 1) != 0) Armor = snapshot.ArmorValue;
            else if (Armor.HasValue) Armor = Math.Min(Armor.Value, snapshot.ArmorValue);
            if ((snapshot.GearAcquired & 2) != 0) Helmet = snapshot.HasHelmet;
            else if (!snapshot.HasHelmet) Helmet = null;
            if ((snapshot.GearAcquired & 4) != 0) Defuser = snapshot.HasDefuser;
            else if (!snapshot.HasDefuser) Defuser = null;
            changed = true;
        }
        return changed;
    }

    public void ClearGear() => (Armor, Helmet, Defuser) = (null, null, null);
}
