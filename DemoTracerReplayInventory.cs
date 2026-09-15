/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private void StartReplayInventory(int slot, uint cursor)
    {
        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) || replay.InventorySnapshots.Length == 0)
            return;
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        var timeline = new ReplayInventoryTimeline(replay.InventorySnapshots);
        timeline.Start(cursor);
        _session.ReplayInventoryBySlot[slot] = timeline;
        ApplyReplayLoadoutForSlot(slot, replay);
    }

    private void ProcessReplayInventory(int slot, LoadedReplay replay, int cursor)
    {
        if (!_weaponAlignEnabled || cursor < 0 ||
            !_session.ReplayInventoryBySlot.TryGetValue(slot, out var timeline) ||
            !timeline.Advance((uint)cursor))
            return;
        ApplyReplayLoadoutForSlot(slot, replay);
    }

    private void ApplyReplayInventoryPending(int slot, int retriesRemaining)
    {
        if (!_session.ReplayInventoryBySlot.TryGetValue(slot, out var timeline))
            return;
        var hasWeaponWork = timeline.PendingWeapons.Count > 0 ||
            _session.PendingWeaponSlotReplacements.Keys.Any(key => key.PlayerSlot == slot);
        if (!hasWeaponWork && !timeline.Armor.HasValue && !timeline.Helmet.HasValue && !timeline.Defuser.HasValue)
            return;
        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true } ||
            player.UserId is not int userId || pawn.WeaponServices == null ||
            !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !ReplayTeamAssignmentPolicy.LiveTeamMatches(replay.ManifestTeam, player.Team))
        {
            if (retriesRemaining > 0) ScheduleReplayLoadoutRetry(slot, retriesRemaining - 1);
            return;
        }

        if (timeline.Armor.HasValue || timeline.Helmet.HasValue || timeline.Defuser.HasValue)
        {
            var items = pawn.ItemServices is { Handle: var handle } && handle != IntPtr.Zero
                ? new CCSPlayer_ItemServices(handle) : null;
            if (items != null)
            {
                var gear = new ReplayLoadoutSnapshot
                {
                    ArmorValue = (uint)Math.Clamp(timeline.Armor ?? pawn.ArmorValue, 0, 100),
                    HasHelmet = timeline.Helmet ?? items.HasHelmet,
                    HasDefuser = timeline.Defuser ?? items.HasDefuser,
                };
                if (TryApplyReplayArmorAndKit(slot, player, pawn, gear) && ReplayPawnEquipmentStateMatches(player, pawn, gear))
                    timeline.ClearGear();
            }
        }

        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (def, count) in timeline.PendingWeapons.ToArray())
        {
            if (!TryGetWeaponClassByDefIndex(def, out var name) ||
                GetReplayWeaponSlot(name) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4)
            {
                timeline.PendingWeapons.Remove(def);
                continue;
            }
            targets[name] = count;
        }
        var epoch = CurrentReplayWriteEpoch(slot);
        foreach (var weaponSlot in new[] { ReplayWeaponSlot.Primary, ReplayWeaponSlot.Secondary })
        {
            if (!targets.Keys.Any(name => GetReplayWeaponSlot(name) == weaponSlot) &&
                !_session.PendingWeaponSlotReplacements.ContainsKey((slot, weaponSlot)))
                continue;
            var status = SyncTargetWeaponSlot(player, targets, weaponSlot,
                name => GetReplayWeaponSlot(name) == weaponSlot, userId, epoch);
            if (status != ReplayWeaponSlotSyncStatus.Complete) continue;
            foreach (var def in timeline.PendingWeapons.Keys.ToArray())
                if (TryGetWeaponClassByDefIndex(def, out var name) && GetReplayWeaponSlot(name) == weaponSlot)
                    timeline.PendingWeapons.Remove(def);
        }
        foreach (var (def, count) in timeline.PendingWeapons.ToArray())
        {
            if (!TryGetWeaponClassByDefIndex(def, out var name) ||
                GetReplayWeaponSlot(name) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary)
                continue;
            var missing = Math.Max(0, count - CountCurrentReplayItems(player, name));
            while (missing > 0 && TryGiveNamedItem(player, name)) missing--;
            if (missing == 0) timeline.PendingWeapons.Remove(def);
        }
        if (hasWeaponWork)
        {
            _session.LastEnsuredWeaponDef.Remove(slot);
            _session.LastReplayWeaponDef.Remove(slot);
        }
        // Retry only unfinished acquisition work, using the latest timeline.
        // Completed grants are removed immediately, so a later purchase cannot
        // refill unrelated utility that the server has already consumed.
        if (retriesRemaining > 0 && (timeline.PendingWeapons.Count > 0 ||
            timeline.Armor.HasValue || timeline.Helmet.HasValue || timeline.Defuser.HasValue))
            ScheduleReplayLoadoutRetry(slot, retriesRemaining - 1);
    }
}
