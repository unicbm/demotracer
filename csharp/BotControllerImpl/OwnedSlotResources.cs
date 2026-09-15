namespace BotControllerApi;

[Flags]
internal enum SlotResource
{
    None = 0, Recording = 1, Replay = 2, Input = 4,
    AllLock = 8, AimLock = 16, WeaponLock = 32, BuyPlan = 64,
    Control = Replay | Input | AllLock | AimLock | WeaponLock | BuyPlan,
}

// One ledger for API callers and chat commands. Slot numbers alone are not
// ownership: a successful mutation must be recorded before it can be cleaned up.
internal sealed class OwnedSlotResources
{
    private readonly Dictionary<int, SlotResource> _slots = new();
    private int[]? _slotSnapshot;
    public int[] Slots => _slotSnapshot ??= _slots.Keys.ToArray();
    public bool Has(int slot, SlotResource resource)
        => _slots.TryGetValue(slot, out var held) && (held & resource) != 0;
    public bool Track(int slot, SlotResource resource, bool success)
    {
        if (success)
        {
            if (!_slots.ContainsKey(slot)) _slotSnapshot = null;
            _slots[slot] = _slots.GetValueOrDefault(slot) | resource;
        }
        return success;
    }
    public long Track(int slot, SlotResource resource, long token)
    {
        Track(slot, resource, token > 0);
        return token;
    }
    public void Forget(int slot, SlotResource resource)
    {
        var remaining = _slots.GetValueOrDefault(slot) & ~resource;
        if (remaining == SlotResource.None)
        {
            if (_slots.Remove(slot)) _slotSnapshot = null;
        }
        else _slots[slot] = remaining;
    }
    public void Release(int slot, bool takenByDemoTracer, Action<int, SlotResource> release)
    {
        Release(slot, _slots.GetValueOrDefault(slot), takenByDemoTracer, release);
    }

    public void Release(int slot, SlotResource resource, bool takenByDemoTracer, Action<int, SlotResource> release)
    {
        var held = _slots.GetValueOrDefault(slot) & resource;
        if (takenByDemoTracer)
        {
            Forget(slot, held & SlotResource.Control);
            held &= ~SlotResource.Control;
        }
        List<Exception>? errors = null;
        for (int bit = 1; bit <= (int)SlotResource.BuyPlan; bit <<= 1)
        {
            var item = (SlotResource)bit;
            if ((held & item) == 0) continue;
            try
            {
                release(slot, item);
                Forget(slot, item);
            }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (errors != null) throw new AggregateException(errors);
    }

    public void ReleaseAll(Func<int, bool> takenByDemoTracer, Action<int, SlotResource> release)
    {
        List<Exception> errors = new();
        foreach (var slot in Slots)
        {
            try { Release(slot, takenByDemoTracer(slot), release); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
