using CounterStrikeSharp.API;
using DemoTracerApi;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool TrySetBotHudCrosshairOverride(
        int slot,
        string crosshairCode,
        out DemoTracerCrosshairOverrideResult result)
    {
        var normalized = DemoTracerCrosshairCode.Normalize(crosshairCode);
        if (slot is < 0 or >= MaxPlayerSlots)
        {
            result = CrosshairApiResult(false, slot, normalized, "invalid_slot", -2);
            return false;
        }
        if (normalized == null)
        {
            result = CrosshairApiResult(false, slot, null, "invalid_crosshair_code", -3);
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
        {
            result = CrosshairApiResult(false, slot, normalized, "slot_not_found", -4);
            return false;
        }
        if (!IsReplaySlotStillSafe(slot) || !_botHiderBridge.IsManagedBot(slot))
        {
            result = CrosshairApiResult(false, slot, normalized, "unsafe_or_unmanaged_slot", -5);
            return false;
        }

        var hadPrevious = _companionCrosshairOverrides.TryGetValue(slot, out var previous);
        _companionCrosshairOverrides[slot] = normalized;
        if (!SyncBotHiderPresentationLease(announce: true))
        {
            if (hadPrevious)
                _companionCrosshairOverrides[slot] = previous!;
            else
                _companionCrosshairOverrides.Remove(slot);
            result = CrosshairApiResult(false, slot, normalized, "bothider_lease_failed", -6);
            return false;
        }

        result = CrosshairApiResult(true, slot, normalized, "server_published", 0);
        return true;
    }

    private bool ClearBotHudCrosshairOverride(
        int slot,
        out DemoTracerCrosshairOverrideResult result)
    {
        if (slot is < 0 or >= MaxPlayerSlots)
        {
            result = CrosshairApiResult(false, slot, null, "invalid_slot", -2);
            return false;
        }

        _companionCrosshairOverrides.Remove(slot);
        _ = SyncBotHiderPresentationLease(announce: false);
        result = CrosshairApiResult(true, slot, null, "cleared", 0);
        return true;
    }

    private void ClearBotHudCrosshairOverrides()
    {
        _companionCrosshairOverrides.Clear();
        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private DemoTracerCrosshairOverrideStatus GetCrosshairOverrideStatus()
    {
        return new DemoTracerCrosshairOverrideStatus
        {
            MapCount = _companionCrosshairOverrides.Count,
            LastNativeResult = string.IsNullOrWhiteSpace(_botHiderPresentationLeaseToken) ? -1 : 0,
            DecodeFailures = 0,
            PatchConfigured = false
        };
    }

    private static DemoTracerCrosshairOverrideResult CrosshairApiResult(
        bool ok,
        int slot,
        string? code,
        string reason,
        int rc)
    {
        return new DemoTracerCrosshairOverrideResult
        {
            Ok = ok,
            Slot = slot,
            CrosshairCode = code ?? string.Empty,
            NativeResult = rc,
            Reason = reason
        };
    }
}
