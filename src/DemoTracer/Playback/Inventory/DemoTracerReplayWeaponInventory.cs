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
    private Dictionary<string, int> CountCurrentLoadoutItems(CCSPlayerController player)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
            return counts;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            var itemName = ObservedReplayWeaponClassName(weapon);
            var slot = GetReplayWeaponSlot(itemName);
            if (slot is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Other)
                continue;

            if (slot == ReplayWeaponSlot.Utility)
            {
                int? ammoCount = TryReadReplayUtilityAmmoCount(pawn, weapon, out var observedAmmo)
                    ? observedAmmo
                    : null;
                counts[itemName] = Math.Max(
                    counts.GetValueOrDefault(itemName),
                    ReplayUtilityGrantPolicy.ObservedUtilityCount(1, ammoCount));
                continue;
            }

            counts[itemName] = counts.GetValueOrDefault(itemName) + 1;
        }
        return counts;
    }

    private int CountCurrentReplayItems(CCSPlayerController player, string className)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
            return 0;

        var entityCount = 0;
        int? ammoCount = null;
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid ||
                !ReplayWeaponMatches(weapon, className))
            {
                continue;
            }

            entityCount++;
            if (!ammoCount.HasValue &&
                GetReplayWeaponSlot(className) == ReplayWeaponSlot.Utility &&
                TryReadReplayUtilityAmmoCount(pawn, weapon, out var observedAmmo))
            {
                ammoCount = observedAmmo;
            }
        }

        return GetReplayWeaponSlot(className) == ReplayWeaponSlot.Utility
            ? ReplayUtilityGrantPolicy.ObservedUtilityCount(entityCount, ammoCount)
            : entityCount;
    }

    private static bool TryReadReplayUtilityAmmoCount(
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        out int ammoCount)
    {
        ammoCount = 0;
        try
        {
            var weaponServices = pawn.WeaponServices?.As<CCSPlayer_WeaponServices>();
            var weaponData = weapon.VData;
            if (weaponServices == null || weaponData == null)
                return false;

            var ammo = weaponServices.Ammo;
            var ammoType = weaponData.PrimaryAmmoType;
            if (ammoType >= ammo.Length)
                return false;

            ammoCount = ammo[ammoType];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<CBasePlayerWeapon> GetWeaponsInReplaySlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            if (GetReplayWeaponSlot(ObservedReplayWeaponClassName(weapon)) == slot)
                yield return weapon;
        }
    }

    private bool HasReplayWeapon(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            return false;

        var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
        if (activeWeapon != null &&
            activeWeapon.IsValid &&
            ReplayWeaponMatches(activeWeapon, className))
        {
            return true;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (ReplayWeaponMatches(weapon, className))
                return true;
        }
        return false;
    }

    private bool HasConflictingWeaponInSlot(
        CCSPlayerPawn pawn,
        ReplayWeaponSlot slot,
        string expectedClassName)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary))
            return false;
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (ReplayWeaponMatches(weapon, expectedClassName))
                continue;
            if (GetReplayWeaponSlot(ObservedReplayWeaponClassName(weapon)) == slot)
                return true;
        }

        return false;
    }

    private string ObservedReplayWeaponClassName(CBasePlayerWeapon weapon)
    {
        var itemDefinitionIndex = (int)(weapon.AttributeManager?.Item?.ItemDefinitionIndex ?? 0);
        return NormalizeWeaponClassName(_replayEquipment.ResolveObservedClassName(
            weapon.DesignerName,
            itemDefinitionIndex));
    }

    private bool ReplayWeaponMatches(CBasePlayerWeapon weapon, string expectedClassName)
        => WeaponClassMatches(ObservedReplayWeaponClassName(weapon), expectedClassName);
}
