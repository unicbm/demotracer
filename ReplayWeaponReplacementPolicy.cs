/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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

    internal static bool CanRemoveAndKill(string className)
    {
        var normalized = className.Trim();
        return !normalized.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase);
    }
}
