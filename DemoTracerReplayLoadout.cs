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
            !replay.HasLoadout ||
            _session.LoadoutSyncedSlots.Contains(slot))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            pawn.WeaponServices == null ||
            player.UserId is not int playerUserId)
            return;
        var replayWriteEpoch = CurrentReplayWriteEpoch(slot);

        ApplyReplayArmorAndKit(player, pawn, replay.Loadout);

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
            _session.LoadoutSyncedSlots.Add(slot);
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

    private static void ApplyReplayArmorAndKit(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        ReplayLoadoutSnapshot loadout)
    {
        if (!ManagedSchemaWritesAllowed())
            return;

        pawn.ArmorValue = (int)loadout.ArmorValue;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");

        if (pawn.ItemServices == null || pawn.ItemServices.Handle == IntPtr.Zero)
            return;

        var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
        itemServices.HasHelmet = loadout.HasHelmet;
        itemServices.HasDefuser = player.Team == CsTeam.CounterTerrorist && loadout.HasDefuser;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_pItemServices");
    }

    private static bool ResetReplayPawnRoundStartHealth(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        if (!ManagedSchemaWritesAllowed())
            return true;

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
                ManagedSchemaWritesAllowed(),
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

        moneyServices.Account = balance;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
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
        switch (ReplayWeaponReplacementPolicy.DecideSlotPlanAction(
                    targetItem != null,
                    targetPresent,
                    currentSlotWeapons.Count > 0))
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

            case ReplayWeaponSlotPlanAction.PreserveExisting:
                // Phase-one safety invariant: DTR evidence may select a
                // different gun, but preparation must not detach an already
                // usable primary/secondary before a transactional replacement
                // implementation can prove that the target is attached.
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
