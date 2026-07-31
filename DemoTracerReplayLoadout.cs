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
    private void ApplyReplayLoadoutForSlot(int slot, LoadedReplay replay)
    {
        if (!_weaponAlignEnabled || !replay.HasLoadout || _session.LoadoutSyncedSlots.Contains(slot))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true } ||
            pawn.WeaponServices == null ||
            player.UserId is not int playerUserId)
            return;
        var replayMutationGeneration = CurrentReplayMutationGeneration(slot);

        ApplyReplayArmorAndKit(player, pawn, replay.Loadout);

        var targetItems = BuildLoadoutItemCounts(replay.Loadout);
        var deferredWeaponSync = false;
        deferredWeaponSync |= SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Primary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Primary,
            playerUserId,
            replayMutationGeneration);
        deferredWeaponSync |= SyncTargetWeaponSlot(
            player,
            targetItems,
            ReplayWeaponSlot.Secondary,
            itemName => GetReplayWeaponSlot(itemName) == ReplayWeaponSlot.Secondary,
            playerUserId,
            replayMutationGeneration);
        GiveMissingLoadoutItems(
            player,
            targetItems,
            itemName => GetReplayWeaponSlot(itemName) is not ReplayWeaponSlot.Primary
                and not ReplayWeaponSlot.Secondary
                and not ReplayWeaponSlot.Knife
                and not ReplayWeaponSlot.C4);

        if (deferredWeaponSync)
        {
            Server.NextFrame(() => Server.NextFrame(() =>
                ApplyReplayWeaponPresetIfCurrent(slot, playerUserId, replayMutationGeneration)));
        }
        else
        {
            ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(replay), true, true);
        }

        if (!_session.PendingWeaponSlotReplacements.Keys.Any(key => key.PlayerSlot == slot))
            _session.LoadoutSyncedSlots.Add(slot);
    }

    private void ApplyReplayWeaponPresetIfCurrent(
        int slot,
        int playerUserId,
        long replayMutationGeneration)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (!IsReplayMutationGenerationCurrent(slot, replayMutationGeneration) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != playerUserId ||
            !_session.LoadedReplays.TryGetValue(slot, out var currentReplay))
        {
            return;
        }

        ApplyReplayWeaponPreset(slot, ChooseStartWeaponDef(currentReplay), true, true);
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
        if (_session.BalanceSyncedSlots.Contains(slot) ||
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

    private bool SyncTargetWeaponSlot(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        ReplayWeaponSlot slot,
        Func<string, bool> predicate,
        int playerUserId,
        long replayMutationGeneration)
    {
        var targetItem = BestTargetSlotItem(targetItems, predicate);
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null)
        {
            if (targetItem != null)
                TryGiveNamedItem(player, targetItem);
            return false;
        }

        if (targetItem != null && HasReplayWeapon(pawn, targetItem))
            return false;

        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, slot).ToList();
        if (targetItem == null)
        {
            var extraWeapon = currentSlotWeapons.FirstOrDefault();
            return extraWeapon != null &&
                   DropAndKillReplayWeapon(player, pawn, extraWeapon, "extra_loadout_slot");
        }

        if (currentSlotWeapons.Count == 0)
        {
            TryGiveNamedItem(player, targetItem);
            return false;
        }

        var fallbackItem = currentSlotWeapons
            .Select(weapon => NormalizeWeaponClassName(weapon.DesignerName))
            .FirstOrDefault(itemName => !WeaponClassMatches(itemName, targetItem));
        var weaponToDrop = currentSlotWeapons
            .FirstOrDefault(weapon => !WeaponClassMatches(
                NormalizeWeaponClassName(weapon.DesignerName),
                targetItem));
        if (fallbackItem == null || weaponToDrop == null)
            return false;
        if (player.UserId != playerUserId ||
            !IsReplayMutationGenerationCurrent(player.Slot, replayMutationGeneration))
            return false;

        return BeginWeaponSlotReplacement(
            player,
            pawn,
            weaponToDrop,
            targetItem,
            fallbackItem,
            slot,
            playerUserId,
            replayMutationGeneration,
            "replace_loadout_slot");
    }

    private bool BeginWeaponSlotReplacement(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weaponToDrop,
        string targetItem,
        string fallbackItem,
        ReplayWeaponSlot weaponSlot,
        int playerUserId,
        long replayMutationGeneration,
        string reason)
    {
        var key = (player.Slot, weaponSlot);
        if (_session.PendingWeaponSlotReplacements.TryGetValue(key, out var existing) &&
            existing.PlayerUserId == playerUserId &&
            existing.ReplayMutationGeneration == replayMutationGeneration &&
            existing.TargetItem.Equals(targetItem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!DropAndKillReplayWeapon(player, pawn, weaponToDrop, reason))
            return false;

        var pending = new PendingWeaponSlotReplacement(
            player.Slot,
            playerUserId,
            replayMutationGeneration,
            targetItem,
            fallbackItem,
            weaponSlot);
        _session.PendingWeaponSlotReplacements[key] = pending;
        _session.LastEnsuredWeaponDef.Remove(player.Slot);
        _session.LastReplayWeaponDef.Remove(player.Slot);
        Server.NextFrame(() => CompleteWeaponSlotReplacement(
            pending,
            WeaponSlotReplacementClearWaitFrames));
        return true;
    }

    private void CompleteWeaponSlotReplacement(
        PendingWeaponSlotReplacement pending,
        int clearWaitFramesRemaining)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        var targetPresent = HasReplayWeapon(pawn, pending.TargetItem);
        var anySlotWeapon = GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any();
        switch (ReplayWeaponReplacementPolicy.Decide(
                    targetPresent,
                    anySlotWeapon,
                    clearWaitFramesRemaining))
        {
            case WeaponSlotReplacementAction.TargetReady:
                FinishWeaponSlotReplacement(pending, success: true, "target_ready");
                return;

            case WeaponSlotReplacementAction.WaitForClear:
                Server.NextFrame(() => CompleteWeaponSlotReplacement(
                    pending,
                    clearWaitFramesRemaining - 1));
                return;

            case WeaponSlotReplacementAction.PreserveExisting:
                Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                    pending,
                    WeaponSlotReplacementFallbackWaitFrames,
                    fallbackGrantIssued: false,
                    "slot_clear_timeout"));
                return;

            case WeaponSlotReplacementAction.GrantTarget:
                var targetGrantIssued = TryGiveNamedItem(player, pending.TargetItem);
                Server.NextFrame(() => VerifyTargetWeaponReplacement(
                    pending,
                    WeaponSlotReplacementGrantWaitFrames,
                    targetGrantIssued));
                return;
        }
    }

    private void VerifyTargetWeaponReplacement(
        PendingWeaponSlotReplacement pending,
        int grantWaitFramesRemaining,
        bool targetGrantIssued)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        if (HasReplayWeapon(pawn, pending.TargetItem))
        {
            FinishWeaponSlotReplacement(pending, success: true, "target_granted");
            return;
        }

        if (GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any())
        {
            FinishWeaponSlotReplacement(pending, success: false, "target_grant_conflict");
            return;
        }

        if (grantWaitFramesRemaining > 0)
        {
            if (!targetGrantIssued)
                targetGrantIssued = TryGiveNamedItem(player, pending.TargetItem);
            Server.NextFrame(() => VerifyTargetWeaponReplacement(
                pending,
                grantWaitFramesRemaining - 1,
                targetGrantIssued));
            return;
        }

        var fallbackGrantIssued = TryGiveNamedItem(player, pending.FallbackItem);
        Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
            pending,
            WeaponSlotReplacementFallbackWaitFrames,
            fallbackGrantIssued,
            "target_grant_timeout"));
    }

    private void VerifyFallbackWeaponIfNeeded(
        PendingWeaponSlotReplacement pending,
        int fallbackWaitFramesRemaining,
        bool fallbackGrantIssued,
        string failureReason)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        if (HasReplayWeapon(pawn, pending.TargetItem))
        {
            FinishWeaponSlotReplacement(pending, success: true, "target_granted_late");
            return;
        }

        if (GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any())
        {
            if (fallbackWaitFramesRemaining > 0)
            {
                Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                    pending,
                    fallbackWaitFramesRemaining - 1,
                    fallbackGrantIssued,
                    failureReason));
                return;
            }

            FinishWeaponSlotReplacement(pending, success: false, $"{failureReason}_weapon_preserved");
            return;
        }

        if (!fallbackGrantIssued)
            fallbackGrantIssued = TryGiveNamedItem(player, pending.FallbackItem);
        if (fallbackWaitFramesRemaining > 0)
        {
            Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                pending,
                fallbackWaitFramesRemaining - 1,
                fallbackGrantIssued,
                failureReason));
            return;
        }

        FinishWeaponSlotReplacement(pending, success: false, $"{failureReason}_fallback_failed");
    }

    private bool TryGetPendingWeaponSlotReplacementPawn(
        PendingWeaponSlotReplacement pending,
        out CCSPlayerController player,
        out CCSPlayerPawn pawn)
    {
        var key = (pending.PlayerSlot, pending.WeaponSlot);
        var currentPlayer = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        var currentPawn = currentPlayer?.PlayerPawn.Value;
        if (!_session.PendingWeaponSlotReplacements.TryGetValue(key, out var current) ||
            current != pending ||
            !IsReplayMutationGenerationCurrent(
                pending.PlayerSlot,
                pending.ReplayMutationGeneration) ||
            currentPlayer is not { IsValid: true, PawnIsAlive: true } ||
            currentPlayer.UserId != pending.PlayerUserId ||
            currentPawn is not { IsValid: true } ||
            currentPawn.WeaponServices == null)
        {
            if (_session.PendingWeaponSlotReplacements.TryGetValue(key, out current) && current == pending)
                _session.PendingWeaponSlotReplacements.Remove(key);
            player = null!;
            pawn = null!;
            return false;
        }

        player = currentPlayer;
        pawn = currentPawn;
        return true;
    }

    private void FinishWeaponSlotReplacement(
        PendingWeaponSlotReplacement pending,
        bool success,
        string reason)
    {
        var key = (pending.PlayerSlot, pending.WeaponSlot);
        if (_session.PendingWeaponSlotReplacements.TryGetValue(key, out var current) && current == pending)
            _session.PendingWeaponSlotReplacements.Remove(key);

        _session.LastEnsuredWeaponDef.Remove(pending.PlayerSlot);
        _session.LastReplayWeaponDef.Remove(pending.PlayerSlot);
        if (success)
        {
            Server.PrintToConsole(
                $"dtr: replaced slot={pending.PlayerSlot} item={pending.TargetItem} reason={reason}");
            if (!_session.PendingWeaponSlotReplacements.Keys.Any(
                    key => key.PlayerSlot == pending.PlayerSlot))
            {
                Server.NextFrame(() => FinalizeReplayLoadoutSyncIfCurrent(pending));
            }
            return;
        }

        _session.RebuiltInventorySlots.Remove(pending.PlayerSlot);
        _session.LoadoutSyncedSlots.Remove(pending.PlayerSlot);
        Server.PrintToConsole(
            $"[DTR WARN] weapon slot replacement incomplete slot={pending.PlayerSlot} " +
            $"target={pending.TargetItem} fallback={pending.FallbackItem} reason={reason}");
    }

    private void FinalizeReplayLoadoutSyncIfCurrent(PendingWeaponSlotReplacement pending)
    {
        var player = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        if (!IsReplayMutationGenerationCurrent(
                pending.PlayerSlot,
                pending.ReplayMutationGeneration) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != pending.PlayerUserId ||
            _session.PendingWeaponSlotReplacements.Keys.Any(
                key => key.PlayerSlot == pending.PlayerSlot) ||
            !_session.LoadedReplays.TryGetValue(pending.PlayerSlot, out var replay))
        {
            return;
        }

        _session.LoadoutSyncedSlots.Remove(pending.PlayerSlot);
        ApplyReplayLoadoutForSlot(pending.PlayerSlot, replay);
    }

    private void ClearPendingWeaponSlotReplacementsForSlot(int slot)
    {
        foreach (var key in _session.PendingWeaponSlotReplacements.Keys
                     .Where(key => key.PlayerSlot == slot)
                     .ToArray())
        {
            _session.PendingWeaponSlotReplacements.Remove(key);
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
