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
    private bool DropAndKillReplayWeapon(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string reason)
    {
        var weaponName = weapon.DesignerName;
        var weaponEntityHandle = weapon.EntityHandle.Raw;
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex)
            return false;
        if (!ReplayWeaponReplacementPolicy.CanDropAndKill(weaponName))
        {
            Server.PrintToConsole(
                $"dtr: refused destructive replay knife removal slot={player.Slot} item={weaponName} reason={reason}");
            return false;
        }
        if (!TrySelectWeapon(player, pawn, weapon))
            return false;

        try
        {
            player.DropActiveWeapon();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to drop slot={player.Slot} item={weaponName}: {ex.Message}");
            return false;
        }

        ScheduleDroppedWeaponKill(player.Slot, weaponEntityHandle, weaponName, reason);
        return true;
    }

    private static void KillDroppedWeapon(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        string reason)
    {
        try
        {
            var weapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
            if (weapon is not { IsValid: true } ||
                weapon.EntityHandle.Raw != weaponEntityHandle ||
                !WeaponClassMatches(weapon.DesignerName, weaponName))
            {
                return;
            }

            var currentlyOwned = Utilities.GetPlayers().Any(candidate =>
            {
                if (candidate is not { IsValid: true } ||
                    candidate.PlayerPawn is not { IsValid: true } pawnHandle)
                    return false;
                var candidatePawn = pawnHandle.Value;
                return candidatePawn is { IsValid: true } &&
                       PawnOwnsWeapon(candidatePawn, weapon);
            });
            if (!currentlyOwned)
                weapon.AcceptInput("Kill");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: failed to kill dropped weapon slot={slot} item={weaponName} reason={reason}: {ex.Message}");
        }
    }

    private static void ScheduleDroppedWeaponKill(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        string reason)
    {
        Server.NextFrame(() => Server.NextFrame(() =>
            KillDroppedWeapon(slot, weaponEntityHandle, weaponName, reason)));
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

    private bool TrySelectWeapon(CCSPlayerController player, CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        var defIndex = WeaponDefIndex(weapon.DesignerName);
        if (player.Slot >= 0 && defIndex >= 0)
            BotControllerNative.SwitchBotWeapon(player.Slot, defIndex);

        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null)
            return false;

        if (ManagedSchemaWritesAllowed())
        {
            weaponServices.ActiveWeapon.Raw = weapon.EntityHandle.Raw;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");
        }

        if (player.UserId != null)
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {weapon.DesignerName}");

        return true;
    }

}
