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

            var itemName = NormalizeWeaponClassName(weapon.DesignerName);
            if (GetReplayWeaponSlot(itemName) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Other)
                continue;
            counts[itemName] = counts.GetValueOrDefault(itemName) + 1;
        }
        return counts;
    }

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

    private IEnumerable<CBasePlayerWeapon> GetWeaponsInReplaySlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            if (GetReplayWeaponSlot(NormalizeWeaponClassName(weapon.DesignerName)) == slot)
                yield return weapon;
        }
    }

    private void PreloadReplayWeaponsForSlot(int slot, LoadedReplay replay)
    {
        if (!_session.RebuiltInventorySlots.Contains(slot))
        {
            var rebuilt = true;
            foreach (var def in replay.PreloadWeaponDefIndices)
                rebuilt &= EnsureReplayWeaponForSlot(
                    slot,
                    def,
                    forceSwitch: false,
                    allowGive: true,
                    replaceConflictingSlot: false);
            if (rebuilt)
                _session.RebuiltInventorySlots.Add(slot);
        }

        ApplyReplayWeaponPreset(
            slot,
            ChooseStartWeaponDef(replay),
            allowSlotReplacement: true,
            force: true);
    }

    private void ApplyReplayWeaponPreset(
        int slot,
        int weaponDefIndex,
        bool allowSlotReplacement,
        bool force)
    {
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

        if (allowSlotReplacement && IsSlotReplaceableWeaponDef(normalized))
        {
            var ensured = EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: true);
            if (!ensured)
            {
                _session.LastReplayWeaponDef.Remove(slot);
                return;
            }
        }

        if (BotControllerNative.SwitchBotWeapon(slot, normalized))
        {
            _session.LastReplayWeaponDef[slot] = normalized;
            ApplyReplayWeaponCosmeticForSlot(slot, normalized);
            ScheduleActiveReplayWeaponCosmeticNextFrame(slot, normalized);
        }
        else if (!allowSlotReplacement)
        {
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
        bool allowGive,
        bool replaceConflictingSlot)
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

        if (allowGive &&
            replaceConflictingSlot &&
            player.UserId is int playerUserId &&
            TryGetWeaponClassByDefIndex(normalized, out var replacementClassName))
        {
            var pawn = player.PlayerPawn.Value;
            var weaponSlot = GetReplayWeaponSlot(replacementClassName);
            var conflictingWeapon = GetWeaponsInReplaySlot(pawn, weaponSlot)
                .FirstOrDefault(weapon => !WeaponClassMatches(
                    weapon.DesignerName,
                    replacementClassName));
            if (conflictingWeapon != null)
            {
                var fallbackItem = NormalizeWeaponClassName(conflictingWeapon.DesignerName);
                _ = BeginWeaponSlotReplacement(
                    player,
                    pawn,
                    conflictingWeapon,
                    replacementClassName,
                    fallbackItem,
                    weaponSlot,
                    playerUserId,
                    CurrentReplayMutationGeneration(slot),
                    "replace_replay_slot");
                return false;
            }
        }

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                out var className))
            return false;

        _session.LastEnsuredWeaponDef[slot] = normalized;
        ApplyReplayWeaponCosmeticForSlot(slot, normalized);
        if (forceSwitch)
        {
            if (!BotControllerNative.SwitchBotWeapon(slot, normalized))
            {
                _session.LastEnsuredWeaponDef.Remove(slot);
                return false;
            }
            ScheduleActiveReplayWeaponCosmeticNextFrame(slot, normalized);
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

    private static bool HasReplayWeapon(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            return false;

        var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
        if (activeWeapon != null &&
            activeWeapon.IsValid &&
            WeaponClassMatches(activeWeapon.DesignerName, className))
        {
            return true;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (WeaponClassMatches(weapon.DesignerName, className))
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
            if (WeaponClassMatches(weapon.DesignerName, expectedClassName))
                continue;
            if (GetReplayWeaponSlot(weapon.DesignerName) == slot)
                return true;
        }

        return false;
    }
}
