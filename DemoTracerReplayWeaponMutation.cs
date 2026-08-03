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
                $"dtr: refused destructive replay weapon removal slot={player.Slot} item={weaponName} reason={reason}");
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
                $"dtr: exact replay weapon removal is pending slot={player.Slot} " +
                $"item={weaponName} reason={reason}");
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

        // RemovePlayerItem owns the inventory mutation, but deletion must cross
        // a network-frame boundary. BotRandomizer supplies CEconItemView state in
        // GiveNamedItem's pre-hook; killing that entity in the same frame can
        // publish its state after its entity index has already disappeared.
        ScheduleRemovedReplayWeaponCleanup(
            player.Slot,
            weaponEntityHandle,
            weaponName,
            reason);
        return true;
    }

    private void ScheduleRemovedReplayWeaponCleanup(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        string reason)
    {
        var roundEpoch = _replayRoundWorkEpoch;
        Server.NextFrame(() => CleanupRemovedReplayWeapon(
            slot,
            weaponEntityHandle,
            weaponName,
            reason,
            roundEpoch,
            framesSinceDetach: 1,
            retriesRemaining: DetachedWeaponCleanupRetryFrames));
    }

    private void CleanupRemovedReplayWeapon(
        int slot,
        uint weaponEntityHandle,
        string weaponName,
        string reason,
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
                                  WeaponClassMatches(weapon.DesignerName, weaponName);
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
                    Server.NextFrame(() => CleanupRemovedReplayWeapon(
                        slot,
                        weaponEntityHandle,
                        weaponName,
                        reason,
                        roundEpoch,
                        framesSinceDetach + 1,
                        retriesRemaining - 1));
                    return;

                case DetachedWeaponCleanupAction.Abandon:
                    Server.PrintToConsole(
                        $"[DTR WARN] detached replay weapon remains engine-referenced slot={slot} " +
                        $"item={weaponName} reason={reason}");
                    return;
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: failed to clean removed weapon slot={slot} item={weaponName} reason={reason}: {ex.Message}");
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
