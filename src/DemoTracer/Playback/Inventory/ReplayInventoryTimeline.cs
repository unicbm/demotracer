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
    private ReplayInventorySnapshot? _previous;
    public Dictionary<int, int> PendingWeapons { get; } = [];
    public int? Armor { get; private set; }
    public bool? Helmet { get; private set; }
    public bool? Defuser { get; private set; }

    public void Start(uint cursor)
    {
        _next = 0;
        _previous = null;
        PendingWeapons.Clear();
        ClearGear();
        while (_next < snapshots.Length && snapshots[_next].TickIndex <= cursor)
            _previous = snapshots[_next++];
        if (_previous is not { } initial)
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
            var previousCounts = _previous?.WeaponDefCounts.ToDictionary(item => item.WeaponDefIndex, item => item.Count);
            // Invalidate a deferred grant if the demo no longer holds that item.
            foreach (var def in PendingWeapons.Keys.ToArray())
            {
                var remaining = counts.GetValueOrDefault(def);
                if (remaining <= 0) PendingWeapons.Remove(def);
                else PendingWeapons[def] = Math.Min(PendingWeapons[def], remaining);
            }
            foreach (var (def, count) in counts)
                if (count > (previousCounts?.GetValueOrDefault(def) ?? 0))
                    PendingWeapons[def] = count;

            if (_previous == null || snapshot.ArmorValue > _previous.ArmorValue) Armor = snapshot.ArmorValue;
            else if (Armor.HasValue) Armor = Math.Min(Armor.Value, snapshot.ArmorValue);
            if (_previous == null || (!_previous.HasHelmet && snapshot.HasHelmet)) Helmet = snapshot.HasHelmet;
            else if (!snapshot.HasHelmet) Helmet = null;
            if (_previous == null || (!_previous.HasDefuser && snapshot.HasDefuser)) Defuser = snapshot.HasDefuser;
            else if (!snapshot.HasDefuser) Defuser = null;
            _previous = snapshot;
            changed = true;
        }
        return changed;
    }

    public void ClearGear() => (Armor, Helmet, Defuser) = (null, null, null);
}
