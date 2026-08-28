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
    private void PreloadReplayWeaponsForSlot(int slot, LoadedReplay replay)
    {
        if (!CanWriteReplaySlot(slot) || _session.RebuiltInventorySlots.Contains(slot))
            return;

        var rebuilt = true;
        foreach (var def in replay.PreloadWeaponDefIndices)
            rebuilt &= EnsureReplayWeaponForSlot(
                slot,
                def,
                forceSwitch: false,
                allowGive: true);
        if (!rebuilt)
            return;

        _session.RebuiltInventorySlots.Add(slot);

        ApplyReplayWeaponPreset(
            slot,
            ChooseStartWeaponDef(replay),
            force: true);
    }

    private void ApplyReplayWeaponPreset(
        int slot,
        int weaponDefIndex,
        bool force)
    {
        if (!CanWriteReplaySlot(slot))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsKnownWeaponDefIndex(normalized))
            return;

        if (!force &&
            _session.LastReplayWeaponDef.TryGetValue(slot, out var lastDef) &&
            lastDef == normalized)
            return;

        var target = GetReplayLockTarget(normalized);
        if (target <= 0)
        {
            if (_session.LastLockedWeaponTarget.Remove(slot))
                BotControllerNative.UnlockWeaponSlot(slot);
        }
        else if (force ||
                 !_session.LastLockedWeaponTarget.TryGetValue(slot, out var lastTarget) ||
                 lastTarget != target)
        {
            if (BotControllerNative.LockWeaponSlot(slot, target))
                _session.LastLockedWeaponTarget[slot] = target;
        }

        if (BotControllerNative.SwitchBotWeapon(slot, normalized))
        {
            _session.LastReplayWeaponDef[slot] = normalized;
        }
        else if (TryGetWeaponClassByDefIndex(normalized, out var expectedClassName) &&
                 player.PlayerPawn.Value is { IsValid: true } pawn &&
                 ReplayWeaponReplacementPolicy.ShouldCacheFailedSwitch(
                     HasReplayWeapon(pawn, expectedClassName)))
        {
            // A native switch can be rejected transiently, but only cache the
            // replay def when the weapon really exists. Caching a missing gun
            // permanently suppresses recovery after an asynchronous grant.
            _session.LastReplayWeaponDef[slot] = normalized;
        }
        else
        {
            _session.LastReplayWeaponDef.Remove(slot);
        }
    }

    private int ChooseStartWeaponDef(LoadedReplay replay)
    {
        var first = NormalizeWeaponDefIndex(replay.FirstWeaponDefIndex);
        if (IsKnownWeaponDefIndex(first) && GetReplayLockTarget(first) != 5)
            return first;

        foreach (var def in replay.PreloadWeaponDefIndices)
        {
            var normalized = NormalizeWeaponDefIndex(def);
            if (IsKnownWeaponDefIndex(normalized))
                return normalized;
        }

        return first;
    }

    private bool EnsureReplayWeaponForSlot(
        int slot,
        int weaponDefIndex,
        bool forceSwitch,
        bool allowGive)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
            return false;
        if (_session.LastEnsuredWeaponDef.TryGetValue(slot, out var last) && last == normalized && !forceSwitch)
            return true;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                out var className))
            return false;

        _session.LastEnsuredWeaponDef[slot] = normalized;
        if (forceSwitch)
        {
            if (!BotControllerNative.SwitchBotWeapon(slot, normalized))
            {
                _session.LastEnsuredWeaponDef.Remove(slot);
                return false;
            }
        }

        Server.PrintToConsole($"dtr: aligned slot={slot} def={normalized} item={className}");
        return true;
    }

    private bool TryEnsureReplayWeapon(
        CCSPlayerController player,
        int weaponDefIndex,
        bool allowGive,
        out string className)
    {
        className = string.Empty;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out className))
            return false;

        var pawn = player.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true })
            return false;

        if (HasReplayWeapon(pawn, className))
            return true;

        var slot = GetReplayWeaponSlot(className);
        if (!allowGive)
            return false;
        if (slot is ReplayWeaponSlot.Other or ReplayWeaponSlot.Knife or
            ReplayWeaponSlot.C4 or ReplayWeaponSlot.Taser)
            return false;

        if (HasConflictingWeaponInSlot(pawn, slot, className))
            return false;

        if (HasReplayWeapon(pawn, className))
            return true;

        if (!TryGiveNamedItem(player, className))
            return false;

        return HasReplayWeapon(pawn, className) || slot == ReplayWeaponSlot.Utility;
    }

}
