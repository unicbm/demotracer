/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal enum ReplaySlotPhase
{
    Loaded,
    Claimed,
    Playing,
}

internal readonly record struct ReplaySlotRuntimeState(
    int Slot,
    ReplaySlotPhase Phase,
    long Epoch)
{
    public bool OwnsWrites => Phase is ReplaySlotPhase.Claimed or ReplaySlotPhase.Playing;
    public bool IsPlaying => Phase == ReplaySlotPhase.Playing;
}

internal sealed class ReplaySlotRegistry
{
    private readonly Dictionary<int, ReplaySlotRuntimeState> _bySlot = [];
    private readonly List<int> _loadedSlots = [];
    private readonly HashSet<int> _ownedSlots = [];
    private readonly HashSet<int> _playingSlots = [];
    private long _nextEpoch;

    public IReadOnlyList<int> LoadedSlots => _loadedSlots;
    public IReadOnlySet<int> OwnedSlots => _ownedSlots;
    public IReadOnlySet<int> PlayingSlots => _playingSlots;

    public int LoadedCount => _loadedSlots.Count;
    public int OwnedCount => _ownedSlots.Count;
    public int PlayingCount => _playingSlots.Count;
    public bool HasAnyState => _bySlot.Count > 0;

    public bool IsLoaded(int slot) => _bySlot.ContainsKey(slot);
    public bool IsOwned(int slot) => _ownedSlots.Contains(slot);
    public bool IsPlaying(int slot) => _playingSlots.Contains(slot);

    public bool TryGet(int slot, out ReplaySlotRuntimeState state)
        => _bySlot.TryGetValue(slot, out state);

    public long CurrentEpoch(int slot) => GetRequired(slot).Epoch;

    public bool IsCurrentEpoch(int slot, long epoch)
        => _bySlot.TryGetValue(slot, out var state) && state.Epoch == epoch;

    public ReplaySlotRuntimeState LoadAndClaim(int slot)
    {
        ValidateSlot(slot);
        if (!_bySlot.ContainsKey(slot))
            _loadedSlots.Add(slot);

        return SetPhase(slot, ReplaySlotPhase.Claimed, renewEpoch: true);
    }

    public ReplaySlotRuntimeState Claim(int slot)
    {
        var current = GetRequired(slot);
        if (current.OwnsWrites)
            return current;

        return SetPhase(slot, ReplaySlotPhase.Claimed, renewEpoch: true);
    }

    public ReplaySlotRuntimeState MarkPlaying(int slot)
    {
        var current = GetRequired(slot);
        if (current.IsPlaying)
            return current;

        return SetPhase(
            slot,
            ReplaySlotPhase.Playing,
            renewEpoch: current.Phase == ReplaySlotPhase.Loaded);
    }

    public bool Release(int slot)
    {
        if (!_bySlot.TryGetValue(slot, out var current) ||
            current.Phase == ReplaySlotPhase.Loaded)
        {
            return false;
        }

        SetPhase(slot, ReplaySlotPhase.Loaded, renewEpoch: true);
        return true;
    }

    public bool InvalidateWrites(int slot)
    {
        if (!_bySlot.TryGetValue(slot, out var current))
            return false;

        SetPhase(slot, current.Phase, renewEpoch: true);
        return true;
    }

    public bool Unload(int slot)
    {
        if (!_bySlot.Remove(slot))
            return false;

        _loadedSlots.Remove(slot);
        _ownedSlots.Remove(slot);
        _playingSlots.Remove(slot);
        return true;
    }

    public void Clear()
    {
        _bySlot.Clear();
        _loadedSlots.Clear();
        _ownedSlots.Clear();
        _playingSlots.Clear();
    }

    private ReplaySlotRuntimeState SetPhase(
        int slot,
        ReplaySlotPhase phase,
        bool renewEpoch)
    {
        var epoch = !renewEpoch && _bySlot.TryGetValue(slot, out var current)
            ? current.Epoch
            : ++_nextEpoch;
        var next = new ReplaySlotRuntimeState(slot, phase, epoch);
        _bySlot[slot] = next;

        if (next.OwnsWrites)
            _ownedSlots.Add(slot);
        else
            _ownedSlots.Remove(slot);

        if (next.IsPlaying)
            _playingSlots.Add(slot);
        else
            _playingSlots.Remove(slot);

        return next;
    }

    private ReplaySlotRuntimeState GetRequired(int slot)
    {
        ValidateSlot(slot);
        if (_bySlot.TryGetValue(slot, out var state))
            return state;

        throw new InvalidOperationException($"Replay slot {slot} is not loaded.");
    }

    private static void ValidateSlot(int slot)
    {
        if (slot < 0)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Replay slot must be non-negative.");
    }
}
