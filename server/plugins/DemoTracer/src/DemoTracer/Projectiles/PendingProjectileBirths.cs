/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct PendingProjectileBirth(
    uint EntityIndex,
    uint EntityHandle,
    ReplayProjectileKind Kind,
    ReplayPlaybackBoundary PlaybackBoundary);

internal sealed class PendingProjectileBirths
{
    private readonly Dictionary<nint, PendingProjectileBirth> _births = [];
    public int Count => _births.Values.Count(birth => birth.PlaybackBoundary.PlayingSlots != 0);

    public void Track(nint pointer, PendingProjectileBirth birth)
    {
        if (_births.TryGetValue(pointer, out var previous) && previous.EntityHandle == birth.EntityHandle)
            return;
        _births[pointer] = birth;
    }
    public bool TryPeek(nint pointer, out PendingProjectileBirth birth)
        => _births.TryGetValue(pointer, out birth) && birth.PlaybackBoundary.PlayingSlots != 0;

    // A first-physics attempt is single-use, including invalidated/reused
    // entities. Never retry on a later simulation after the engine has moved it.
    public bool TryConsume(nint pointer, uint entityHandle, out PendingProjectileBirth birth)
    {
        if (!TryPeek(pointer, out birth))
            return false;
        Discard(pointer);
        return birth.EntityHandle == entityHandle;
    }

    // Revoke writes, retaining the serial until entity deletion.
    public void Discard(nint pointer)
    {
        if (_births.TryGetValue(pointer, out var birth))
            _births[pointer] = birth with { PlaybackBoundary = default };
    }
    public void CancelAll()
    {
        foreach (var pointer in _births.Keys.ToArray())
            Discard(pointer);
    }
    public void Remove(nint pointer) => _births.Remove(pointer);
    public void Clear() => _births.Clear();

    public void CancelSlot(int slot)
    {
        if ((uint)slot >= 64)
            return;
        var bit = 1UL << slot;
        foreach (var pointer in _births.Keys.ToArray())
        {
            var birth = _births[pointer];
            var boundary = birth.PlaybackBoundary;
            if ((boundary.PlayingSlots & bit) == 0)
                continue;
            boundary = boundary with { PlayingSlots = boundary.PlayingSlots & ~bit };
            _births[pointer] = birth with { PlaybackBoundary = boundary };
        }
    }
}
