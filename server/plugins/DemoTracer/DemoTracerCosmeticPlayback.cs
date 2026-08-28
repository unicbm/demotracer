/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    // DemoTracer tracks whether a validated replay plan was accepted, but it
    // never writes cosmetic entity state. BotRandomizer consumes the plan from
    // its spawn and GiveNamedItem lifecycle.
    private void QueueLoadedReplayCosmeticAlignmentForSlot(int slot)
    {
        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !_botRandomizerLease.TryGet(slot, replay.SteamId, out _))
        {
            return;
        }

        _session.CosmeticSyncedSlots.Add(slot);
    }

    private void InvalidateLoadedReplayCosmeticAlignmentForSlot(int slot)
    {
        _cosmeticAlignmentTracker.Invalidate(slot);
        _session.CosmeticSyncedSlots.Remove(slot);
    }

    private bool HasCurrentLoadedReplayCosmeticAlignment(int slot, LoadedReplay replay)
        => _session.CosmeticSyncedSlots.Contains(slot) &&
           _botRandomizerLease.TryGet(slot, replay.SteamId, out _);

    private static bool PawnOwnsWeapon(CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate is { IsValid: true } && candidate.Handle == weapon.Handle)
                return true;
        }
        return false;
    }
}
