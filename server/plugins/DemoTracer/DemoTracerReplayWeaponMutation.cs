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
    private bool RemoveWeaponForReplacement(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        string targetItem,
        ReplayWeaponSlot weaponSlot)
    {
        var weaponName = ObservedReplayWeaponClassName(weapon);
        var weaponEntityHandle = weapon.EntityHandle.Raw;
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex ||
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
            pawn.RemovePlayerItem(weapon);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to detach occupied weapon slot={player.Slot}:{weaponSlot} item={weaponName}: {ex.Message}");
            return false;
        }

        if (weapon is { IsValid: true } && PawnOwnsWeapon(pawn, weapon))
        {
            Server.PrintToConsole(
                $"dtr: occupied weapon detach is pending slot={player.Slot}:{weaponSlot} item={weaponName}");
        }

        if (weapon is not { IsValid: true })
            return true;
        if (weapon.EntityHandle.Raw != weaponEntityHandle ||
            !ReplayWeaponMatches(weapon, weaponName))
        {
            Server.PrintToConsole(
                $"[DTR WARN] detached weapon identity changed slot={player.Slot}:{weaponSlot} item={weaponName}");
            // The exact old entity can no longer be cleaned safely, but the
            // inventory mutation already succeeded. Keep the replacement
            // transaction alive so an empty slot still receives either the
            // target or the original weapon fallback.
            return true;
        }

        ScheduleRemovedWeaponCleanup(
            player.Slot,
            weaponEntityHandle,
            weaponName);
        return true;
    }

    private void ScheduleRemovedWeaponCleanup(
        int slot,
        uint weaponEntityHandle,
        string weaponName)
    {
        var roundEpoch = _replayRoundWorkEpoch;
        Server.NextFrame(() => CleanupRemovedWeapon(
            slot,
            weaponEntityHandle,
            weaponName,
            roundEpoch,
            framesSinceDetach: 1,
            retriesRemaining: DetachedWeaponCleanupRetryFrames));
    }

    private void CleanupRemovedWeapon(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        long roundEpoch,
        int framesSinceDetach,
        int retriesRemaining)
    {
        if (!IsReplayRoundWorkEpochCurrent(roundEpoch))
            return;

        try
        {
            var weapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
            var identityMatches = weapon is { IsValid: true } &&
                                  weapon.EntityHandle.Raw == weaponEntityHandle &&
                                  ReplayWeaponMatches(weapon, weaponName);
            if (!identityMatches)
                return;

            var ownedByPawn = false;
            var activeWeaponReference = false;
            foreach (var candidate in Utilities.GetPlayers())
            {
                var candidatePawn = candidate?.PlayerPawn.Value;
                var weaponServices = candidatePawn?.WeaponServices;
                if (candidate is not { IsValid: true } ||
                    candidatePawn is not { IsValid: true } ||
                    weaponServices == null)
                {
                    continue;
                }

                ownedByPawn |= PawnOwnsWeapon(candidatePawn, weapon!);
                activeWeaponReference |= weaponServices.ActiveWeapon.Raw == weaponEntityHandle;
                if (ownedByPawn && activeWeaponReference)
                    break;
            }

            switch (ReplayWeaponReplacementPolicy.DecideDetachedWeaponCleanup(
                        identityMatches,
                        ownedByPawn,
                        activeWeaponReference,
                        framesSinceDetach,
                        retriesRemaining))
            {
                case DetachedWeaponCleanupAction.Destroy:
                    weapon!.AcceptInput("Kill");
                    return;

                case DetachedWeaponCleanupAction.Retry:
                    Server.NextFrame(() => CleanupRemovedWeapon(
                        slot,
                        weaponEntityHandle,
                        weaponName,
                        roundEpoch,
                        framesSinceDetach + 1,
                        retriesRemaining - 1));
                    return;

                case DetachedWeaponCleanupAction.Abandon:
                    Server.PrintToConsole(
                        $"[DTR WARN] detached weapon remains engine-referenced slot={slot} item={weaponName}");
                    return;
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to clean detached weapon slot={slot} item={weaponName}: {ex.Message}");
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
