/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer;

internal sealed class EpochWorkCoalescer<TKey, TEpoch>
    where TKey : notnull
    where TEpoch : notnull
{
    private readonly Dictionary<TKey, TEpoch> _pending = [];

    public int Count => _pending.Count;

    public bool TrySchedule(TKey key, TEpoch epoch)
    {
        if (_pending.TryGetValue(key, out var pendingEpoch) &&
            EqualityComparer<TEpoch>.Default.Equals(pendingEpoch, epoch))
        {
            return false;
        }

        _pending[key] = epoch;
        return true;
    }

    public bool TryConsume(TKey key, TEpoch epoch)
    {
        if (!_pending.TryGetValue(key, out var pendingEpoch) ||
            !EqualityComparer<TEpoch>.Default.Equals(pendingEpoch, epoch))
        {
            return false;
        }

        _pending.Remove(key);
        return true;
    }

    public bool Cancel(TKey key) => _pending.Remove(key);

    public int CancelWhere(Func<TKey, bool> predicate)
    {
        var removed = 0;
        foreach (var key in _pending.Keys.Where(predicate).ToArray())
        {
            if (_pending.Remove(key))
                removed++;
        }
        return removed;
    }

    public void Clear() => _pending.Clear();
}
