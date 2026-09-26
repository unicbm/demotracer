/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal readonly record struct ReplayCosmeticPawnIdentity(
    int UserId,
    nint PawnHandle,
    long ReplayIdentityGeneration,
    ulong ReplaySteamId);

internal sealed class ReplayCosmeticAlignmentTracker
{
    private readonly Dictionary<int, ReplayCosmeticPawnIdentity> _alignedBySlot = [];
    private readonly Dictionary<int, long> _pendingBySlot = [];
    private long _nextToken;

    internal long Queue(int slot)
    {
        var token = ++_nextToken;
        _pendingBySlot[slot] = token;
        return token;
    }

    internal bool TryConsume(int slot, long token)
    {
        if (!_pendingBySlot.TryGetValue(slot, out var current) || current != token)
            return false;

        _pendingBySlot.Remove(slot);
        return true;
    }

    internal bool IsAligned(int slot, ReplayCosmeticPawnIdentity identity)
        => _alignedBySlot.TryGetValue(slot, out var current) && current == identity;

    internal bool HasReplayAlignment(
        int slot,
        long replayIdentityGeneration,
        ulong replaySteamId)
        => _alignedBySlot.TryGetValue(slot, out var current) &&
           current.ReplayIdentityGeneration == replayIdentityGeneration &&
           current.ReplaySteamId == replaySteamId;

    internal void CancelPending(int slot)
        => _pendingBySlot.Remove(slot);

    internal void MarkAligned(int slot, ReplayCosmeticPawnIdentity identity)
        => _alignedBySlot[slot] = identity;

    internal bool TryMarkAligned(
        int slot,
        ReplayCosmeticPawnIdentity identity,
        int failedWrites)
    {
        if (failedWrites != 0)
            return false;

        MarkAligned(slot, identity);
        return true;
    }

    internal void Invalidate(int slot)
    {
        _alignedBySlot.Remove(slot);
        _pendingBySlot.Remove(slot);
    }

    internal void Clear()
    {
        _alignedBySlot.Clear();
        _pendingBySlot.Clear();
    }
}
