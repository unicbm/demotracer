namespace DemoTracer;

internal enum WeaponSlotReplacementAction
{
    TargetReady,
    WaitForClear,
    GrantTarget,
    PreserveExisting
}

internal static class ReplayWeaponReplacementPolicy
{
    internal static WeaponSlotReplacementAction Decide(
        bool targetPresent,
        bool anySlotWeapon,
        int clearWaitFramesRemaining)
    {
        if (targetPresent)
            return WeaponSlotReplacementAction.TargetReady;
        if (!anySlotWeapon)
            return WeaponSlotReplacementAction.GrantTarget;
        return clearWaitFramesRemaining > 0
            ? WeaponSlotReplacementAction.WaitForClear
            : WeaponSlotReplacementAction.PreserveExisting;
    }

    internal static bool CanDropAndKill(string className)
    {
        var normalized = className.Trim();
        return !normalized.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase);
    }
}
