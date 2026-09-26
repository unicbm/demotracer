/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace BotHiderImpl;

// Coalesce event-driven work for one frame. A full lifecycle reconcile covers
// every pending field; slot reconciliation already includes that slot's ping.
internal sealed class PendingPresentationPublications
{
    private bool _all;
    private ulong _slots;
    private ulong _pingSlots;

    public void RequestAll() => _all = true;
    public void RequestSlot(int slot)
    {
        if (slot is >= 0 and < 64) _slots |= 1UL << slot;
    }
    public void RequestPing(int slot)
    {
        if (slot is >= 0 and < 64) _pingSlots |= 1UL << slot;
    }

    public (bool All, ulong Slots, ulong PingSlots) Drain()
    {
        var work = (_all, _all ? 0UL : _slots, _all ? 0UL : _pingSlots & ~_slots);
        Clear();
        return work;
    }

    public void Clear()
    {
        _all = false;
        _slots = 0;
        _pingSlots = 0;
    }
}
