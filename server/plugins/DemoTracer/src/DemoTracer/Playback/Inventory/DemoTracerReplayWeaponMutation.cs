/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using DtrHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool RemoveWeaponForReplacement(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string targetItem,
        ReplayWeaponSlot weaponSlot)
    {
        var weaponName = ObservedReplayWeaponClassName(weapon);
        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null ||
            weapon.EntityHandle.Raw == Utilities.InvalidEHandleIndex ||
            !ReplayWeaponReplacementPolicy.CanReplaceOccupiedWeaponSlot(
                weaponSlot,
                weaponName,
                targetItem) ||
            GetReplayWeaponSlot(weaponName) != weaponSlot ||
            !PawnOwnsWeapon(pawn, weapon))
        {
            return false;
        }

        try
        {
            // CSS's legacy RemovePlayerItem signature resolves to the weapon
            // services method. Its this pointer must not be the pawn: the
            // method clears active/inventory references before UTIL_Remove.
            // It owns destruction; do not enqueue a second Kill afterward.
            VirtualFunctions.RemovePlayerItemVirtual(weaponServices.Handle, weapon.Handle);
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to remove occupied weapon slot={player.Slot}:{weaponSlot} item={weaponName}: {ex.Message}");
            return false;
        }
    }

    private static bool TryGiveNamedItem(CCSPlayerController player, string itemName)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
            return false;

        try
        {
            return player.GiveNamedItem(itemName) != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to give slot={player.Slot} item={itemName}: {ex.Message}");
            return false;
        }
    }

}
