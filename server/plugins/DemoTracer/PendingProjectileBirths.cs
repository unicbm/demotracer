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
    float ObservedSpawnTime,
    int ObservedSpawnTick,
    bool AlignAtSpawn,
    ReplayPlaybackBoundary PlaybackBoundary);

internal sealed class PendingProjectileBirths
{
    private readonly Dictionary<nint, PendingProjectileBirth> _pending = [];
    public int Count => _pending.Count;

    public void Track(nint pointer, PendingProjectileBirth birth) => _pending[pointer] = birth;
    public bool TryPeek(nint pointer, out PendingProjectileBirth birth) => _pending.TryGetValue(pointer, out birth);

    // A first-physics attempt is single-use, including invalidated/reused
    // entities. Never retry on a later simulation after the engine has moved it.
    public bool TryConsume(nint pointer, uint entityHandle, out PendingProjectileBirth birth)
        => _pending.Remove(pointer, out birth) && birth.EntityHandle == entityHandle;

    public void Remove(nint pointer) => _pending.Remove(pointer);
    public void Clear() => _pending.Clear();

    public void CancelSlot(int slot)
    {
        if ((uint)slot >= 64)
            return;
        var bit = 1UL << slot;
        foreach (var pointer in _pending.Keys.ToArray())
        {
            var birth = _pending[pointer];
            var boundary = birth.PlaybackBoundary;
            if (!birth.AlignAtSpawn || (boundary.PlayingSlots & bit) == 0)
                continue;
            boundary = boundary with { PlayingSlots = boundary.PlayingSlots & ~bit };
            if (boundary.PlayingSlots == 0)
                _pending.Remove(pointer);
            else
                _pending[pointer] = birth with { PlaybackBoundary = boundary };
        }
    }
}
