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
    private void ApplyReplayLoadoutForSlot(
        int slot,
        LoadedReplay replay,
        int slotRetryFramesRemaining = ReplayLoadoutSlotRetryFrames)
    {
        if (!CanWriteReplaySlot(slot) ||
            !_weaponAlignEnabled ||
            !replay.HasLoadout)
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            !ReplayTeamAssignmentPolicy.LiveTeamMatches(replay.ManifestTeam, player.Team) ||
            player.UserId is not int playerUserId)
            return;

        var equipmentIdentity = new ReplayPawnEquipmentIdentity(
            playerUserId,
            pawn.EntityHandle.Raw,
            _session.ReplayIdentityGenerationBySlot.GetValueOrDefault(slot),
            replay.SteamId);
        var equipmentSynced =
            _session.PawnEquipmentSync.IsSynced(slot, equipmentIdentity) &&
            ReplayPawnEquipmentStateMatches(player, pawn, replay.Loadout);
        var loadoutRetryScheduled = false;
        if (!equipmentSynced &&
            TryApplyReplayArmorAndKit(slot, player, pawn, replay.Loadout) &&
            ReplayPawnEquipmentStateMatches(player, pawn, replay.Loadout))
        {
            _session.PawnEquipmentSync.MarkSynced(slot, equipmentIdentity);
            equipmentSynced = true;
        }
        if (!equipmentSynced && slotRetryFramesRemaining > 0)
        {
            ScheduleReplayLoadoutRetry(slot, slotRetryFramesRemaining - 1);
            loadoutRetryScheduled = true;
        }

        // Weapon inventory can survive a round spawn, while armor, helmet, and
        // defuser state belong to the newly-created pawn. Keep their completion
        // state independent so preserving an AWP never suppresses pawn gear.
        if (_session.WeaponLoadoutSyncedSlots.Contains(slot))
            return;
        if (pawn.WeaponServices == null)
        {
            if (slotRetryFramesRemaining > 0 && !loadoutRetryScheduled)
                ScheduleReplayLoadoutRetry(slot, slotRetryFramesRemaining - 1);
            return;
        }
        var replayWriteEpoch = CurrentReplayWriteEpoch(slot);

        var targetItems = BuildLoadoutItemCounts(replay.Loadout);
        var primarySync = SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Primary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Primary,
            playerUserId,
            replayWriteEpoch);
        var secondarySync = SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Secondary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Secondary,
            playerUserId,
            replayWriteEpoch);
        GiveMissingLoadoutItems(
            player,
            targetItems,
            itemName => GetReplayWeaponSlot(itemName) is not ReplayWeaponSlot.Primary
                and not ReplayWeaponSlot.Secondary
                and not ReplayWeaponSlot.Knife
                and not ReplayWeaponSlot.C4);

        var pendingWeaponSync = primarySync == ReplayWeaponSlotSyncStatus.Pending ||
                                secondarySync == ReplayWeaponSlotSyncStatus.Pending;
        var retryWeaponSync = primarySync == ReplayWeaponSlotSyncStatus.RetryRequired ||
                              secondarySync == ReplayWeaponSlotSyncStatus.RetryRequired;
        if (pendingWeaponSync)
        {
            Server.NextFrame(() => Server.NextFrame(() =>
                ApplyReplayWeaponPresetIfCurrent(slot, playerUserId, replayWriteEpoch)));
        }
        else if (!retryWeaponSync)
        {
            ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(replay), force: true);
        }

        if (retryWeaponSync && !pendingWeaponSync)
        {
            if (slotRetryFramesRemaining > 0)
            {
                ScheduleReplayLoadoutRetry(slot, slotRetryFramesRemaining - 1);
            }
            else
            {
                Server.PrintToConsole(
                    $"[DTR WARN] replay loadout slot grant did not settle slot={slot}");
            }
        }

        if (!pendingWeaponSync &&
            !retryWeaponSync &&
            !_session.PendingWeaponSlotReplacements.Keys.Any(key => key.PlayerSlot == slot))
        {
            _session.WeaponLoadoutSyncedSlots.Add(slot);
        }
    }

    private void ApplyReplayWeaponPresetIfCurrent(
        int slot,
        int playerUserId,
        long replayWriteEpoch)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (!IsReplayWriteEpochCurrent(slot, replayWriteEpoch) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != playerUserId ||
            !_session.LoadedReplays.TryGetValue(slot, out var currentReplay))
        {
            return;
        }

        ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(currentReplay), force: true);
    }

    private static bool TryApplyReplayArmorAndKit(
        int slot,
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayLoadoutSnapshot loadout)
    {
        // CSS owns the manifest-derived desired state. BotController owns the
        // live Pawn lifecycle and applies it immediately, at replay start, and
        // once from the first movement/usercmd hook for this registered Pawn.
        if (!BotControllerNative.SetReplayPawn(slot, pawn.Handle))
            return false;

        var expectedDefuser = player.Team == CsTeam.CounterTerrorist && loadout.HasDefuser;
        return BotControllerNative.SetReplayPawnEquipment(
            slot,
            pawn.Handle,
            player.Handle,
            (int)loadout.ArmorValue,
            loadout.HasHelmet,
            expectedDefuser);
    }

    private static bool ReplayPawnEquipmentStateMatches(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayLoadoutSnapshot loadout)
    {
        var expectedDefuser = player.Team == CsTeam.CounterTerrorist && loadout.HasDefuser;
        var itemServicesAvailable =
            pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero;
        var itemServices = itemServicesAvailable
            ? new CCSPlayer_ItemServices(pawn.ItemServices!.Handle)
            : null;
        return ReplayRuntimePolicy.PawnEquipmentStateMatches(
            (int)loadout.ArmorValue,
            loadout.HasHelmet,
            expectedDefuser,
            pawn.ArmorValue,
            itemServicesAvailable,
            itemServices?.HasHelmet ?? false,
            itemServices?.HasDefuser ?? false,
            player.PawnArmor,
            player.PawnHasHelmet,
            player.PawnHasDefuser);
    }

    private static bool ResetReplayPawnRoundStartHealth(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        var pawn = player.PlayerPawn.Value;
        pawn.Health = ReplayStartHealth;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        return true;
    }

    private void ApplyReplayRoundStartBalanceForSlot(int slot, LoadedReplay replay)
    {
        if (!CanWriteReplaySlot(slot) ||
            _session.BalanceSyncedSlots.Contains(slot) ||
            !ReplayRuntimePolicy.TryResolveRoundStartBalance(
                _balanceAlignEnabled,
                replay.RoundStartBalance,
                ReadServerMaxMoney(),
                out var balance))
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (!IsReplaySlotStillSafe(slot) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.InGameMoneyServices is not { } moneyServices ||
            moneyServices.Handle == IntPtr.Zero)
        {
            return;
        }

        // Account is engine-managed through the money service. The controller
        // service pointer itself is not a networked field.
        moneyServices.Account = balance;
        _session.BalanceSyncedSlots.Add(slot);
    }

    private static int? ReadServerMaxMoney()
    {
        var conVar = ConVar.Find(MaxMoneyConVarName);
        if (conVar == null)
            return null;

        try
        {
            var value = conVar.GetPrimitiveValue<int>();
            return value >= 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private ReplayWeaponSlotSyncStatus SyncTargetWeaponSlot(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        ReplayWeaponSlot slot,
        Func<string, bool> predicate,
        int playerUserId,
        long replayWriteEpoch)
    {
        var targetItem = BestTargetSlotItem(targetItems, predicate);
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            if (targetItem != null)
                TryGiveNamedItem(player, targetItem);
            return targetItem == null
                ? ReplayWeaponSlotSyncStatus.Complete
                : ReplayWeaponSlotSyncStatus.RetryRequired;
        }

        var targetPresent = targetItem != null && HasReplayWeapon(pawn, targetItem);

        var pendingKey = (player.Slot, slot);
        if (_session.PendingWeaponSlotReplacements.TryGetValue(pendingKey, out var existing))
        {
            if (existing.PlayerUserId == playerUserId &&
                existing.PawnEntityHandle == pawn.EntityHandle.Raw &&
                existing.ReplayWriteEpoch == replayWriteEpoch &&
                existing.TargetItem.Equals(targetItem, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayWeaponSlotSyncStatus.Pending;
            }

            CancelPendingWeaponSlotReplacement(existing, "replacement_superseded");
        }

        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, slot).ToList();
        var canReplaceOccupiedSlot =
            targetItem != null &&
            currentSlotWeapons.Count == 1 &&
            ReplayWeaponReplacementPolicy.CanReplaceOccupiedWeaponSlot(
                slot,
                ObservedReplayWeaponClassName(currentSlotWeapons[0]),
                targetItem);
        switch (ReplayWeaponReplacementPolicy.DecideSlotPlanAction(
                    targetItem != null,
                    targetPresent,
                    currentSlotWeapons.Count > 0,
                    canReplaceOccupiedSlot))
        {
            case ReplayWeaponSlotPlanAction.Complete:
                return ReplayWeaponSlotSyncStatus.Complete;

            case ReplayWeaponSlotPlanAction.GrantIntoEmptySlot:
                return BeginEmptyWeaponSlotGrant(
                    player,
                    pawn,
                    targetItem!,
                    slot,
                    playerUserId,
                    replayWriteEpoch)
                    ? ReplayWeaponSlotSyncStatus.Pending
                    : ReplayWeaponSlotSyncStatus.RetryRequired;

            case ReplayWeaponSlotPlanAction.ReplaceOccupiedSlot:
                return BeginOccupiedWeaponSlotReplacement(
                    player,
                    pawn,
                    currentSlotWeapons[0],
                    targetItem!,
                    slot,
                    playerUserId,
                    replayWriteEpoch)
                    ? ReplayWeaponSlotSyncStatus.Pending
                    : ReplayWeaponSlotSyncStatus.RetryRequired;

            case ReplayWeaponSlotPlanAction.PreserveExisting:
                // Ambiguous slot state (for example multiple weapons) is not
                // safe to mutate automatically. Leave it intact and continue.
                Server.PrintToConsole(
                    $"dtr: preserved occupied weapon slot={player.Slot}:{slot} " +
                    $"target={targetItem ?? "none"}");
                return ReplayWeaponSlotSyncStatus.Complete;

            default:
                return ReplayWeaponSlotSyncStatus.RetryRequired;
        }
    }

    private void GiveMissingLoadoutItems(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        var currentItems = CountCurrentLoadoutItems(player);
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            var missingCount = Math.Max(0, targetCount - currentItems.GetValueOrDefault(itemName));
            for (var i = 0; i < missingCount; i++)
                TryGiveNamedItem(player, itemName);
        }
    }

}
