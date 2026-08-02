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
using DemoTracerBotHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool RemoveReplayWeaponForReplacement(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string reason)
    {
        var weaponName = weapon.DesignerName;
        var weaponEntityHandle = weapon.EntityHandle.Raw;
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex)
            return false;
        if (!ReplayWeaponReplacementPolicy.CanRemoveForReplacement(weaponName))
        {
            Server.PrintToConsole(
                $"dtr: refused destructive replay knife removal slot={player.Slot} item={weaponName} reason={reason}");
            return false;
        }
        if (!PawnOwnsWeapon(pawn, weapon))
            return false;

        try
        {
            // Remove the exact inventory entity instead of selecting it and
            // dropping whatever the engine currently considers active. The
            // latter races native/external buy and weapon-selection writers
            // during freeze time and can drop an unrelated gun or knife.
            pawn.RemovePlayerItem(weapon);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to remove slot={player.Slot} item={weaponName} reason={reason}: {ex.Message}");
            return false;
        }

        if (weapon is { IsValid: true } && PawnOwnsWeapon(pawn, weapon))
        {
            Server.PrintToConsole(
                $"[DTR WARN] exact replay weapon removal was not observed slot={player.Slot} " +
                $"item={weaponName} reason={reason}");
            return false;
        }

        if (weapon is not { IsValid: true })
            return true;
        if (weapon.EntityHandle.Raw != weaponEntityHandle ||
            !WeaponClassMatches(weapon.DesignerName, weaponName))
        {
            Server.PrintToConsole(
                $"[DTR WARN] removed replay weapon identity changed slot={player.Slot} " +
                $"item={weaponName} reason={reason}");
            return false;
        }

        // Destroy the detached entity in the same server mutation. Deferring
        // Kill across network frames can leave a removed weapon published in
        // PVS while another writer still holds its old viewmodel/entity handle.
        // The replacement state machine grants the target on a later frame, so
        // same-frame destruction no longer races GiveNamedItem slot reuse.
        try
        {
            weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to kill removed weapon slot={player.Slot} item={weaponName} reason={reason}: {ex.Message}");
            return false;
        }

        return true;
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
