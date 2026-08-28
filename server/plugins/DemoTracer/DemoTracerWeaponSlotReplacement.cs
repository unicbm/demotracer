/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool BeginOccupiedWeaponSlotReplacement(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon currentWeapon,
        string targetItem,
        ReplayWeaponSlot weaponSlot,
        int playerUserId,
        long replayWriteEpoch)
    {
        var currentItem = ObservedReplayWeaponClassName(currentWeapon);
        var currentSlotWeapons = GetWeaponsInReplaySlot(pawn, weaponSlot).ToList();
        if (player.UserId != playerUserId ||
            !IsReplayWriteEpochCurrent(player.Slot, replayWriteEpoch) ||
            currentSlotWeapons.Count != 1 ||
            currentSlotWeapons[0].EntityHandle.Raw != currentWeapon.EntityHandle.Raw ||
            !ReplayWeaponReplacementPolicy.CanReplaceOccupiedWeaponSlot(
                weaponSlot,
                currentItem,
                targetItem))
        {
            return false;
        }

        var key = (player.Slot, weaponSlot);
        if (_session.PendingWeaponSlotReplacements.ContainsKey(key))
            return false;
        if (!RemoveWeaponForReplacement(
                player,
                pawn,
                currentWeapon,
                targetItem,
                weaponSlot))
        {
            return false;
        }

        var pending = new PendingWeaponSlotReplacement(
            player.Slot,
            playerUserId,
            pawn.EntityHandle.Raw,
            replayWriteEpoch,
            targetItem,
            currentItem,
            weaponSlot);
        _session.PendingWeaponSlotReplacements[key] = pending;
        _session.LastEnsuredWeaponDef.Remove(player.Slot);
        _session.LastReplayWeaponDef.Remove(player.Slot);
        Server.NextFrame(() => CompleteOccupiedWeaponSlotReplacement(
            pending,
            WeaponSlotReplacementClearWaitFrames));
        return true;
    }

    private bool BeginEmptyWeaponSlotGrant(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        string targetItem,
        ReplayWeaponSlot weaponSlot,
        int playerUserId,
        long replayWriteEpoch)
    {
        if (player.UserId != playerUserId ||
            !IsReplayWriteEpochCurrent(player.Slot, replayWriteEpoch))
        {
            return false;
        }

        var fallbackItem = ReplayWeaponReplacementPolicy.EmptySlotFallbackItem(
            weaponSlot,
            player.Team == CsTeam.CounterTerrorist,
            targetItem);
        var pending = new PendingWeaponSlotReplacement(
            player.Slot,
            playerUserId,
            pawn.EntityHandle.Raw,
            replayWriteEpoch,
            targetItem,
            fallbackItem,
            weaponSlot);
        _session.PendingWeaponSlotReplacements[(player.Slot, weaponSlot)] = pending;
        _session.LastEnsuredWeaponDef.Remove(player.Slot);
        _session.LastReplayWeaponDef.Remove(player.Slot);

        _ = TryGiveNamedItem(player, targetItem);
        Server.NextFrame(() => VerifyTargetWeaponReplacement(
            pending,
            WeaponSlotReplacementGrantWaitFrames,
            WeaponSlotReplacementGrantRetryAttempts));
        return true;
    }

    private void CompleteOccupiedWeaponSlotReplacement(
        PendingWeaponSlotReplacement pending,
        int clearWaitFramesRemaining)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        var targetPresent = HasReplayWeapon(pawn, pending.TargetItem);
        var anySlotWeapon = GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any();
        switch (ReplayWeaponReplacementPolicy.DecideReplacementProgress(
                    targetPresent,
                    anySlotWeapon,
                    clearWaitFramesRemaining))
        {
            case WeaponSlotReplacementAction.TargetReady:
                FinishWeaponSlotReplacement(pending, success: true, "target_ready");
                return;

            case WeaponSlotReplacementAction.WaitForClear:
                Server.NextFrame(() => CompleteOccupiedWeaponSlotReplacement(
                    pending,
                    clearWaitFramesRemaining - 1));
                return;

            case WeaponSlotReplacementAction.PreserveExisting:
                Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                    pending,
                    WeaponSlotReplacementFallbackWaitFrames,
                    fallbackRetryAttemptsRemaining: 0,
                    failureReason: "occupied_slot_clear_timeout"));
                return;

            case WeaponSlotReplacementAction.GrantTarget:
                _ = TryGiveNamedItem(player, pending.TargetItem);
                Server.NextFrame(() => VerifyTargetWeaponReplacement(
                    pending,
                    WeaponSlotReplacementGrantWaitFrames,
                    WeaponSlotReplacementGrantRetryAttempts));
                return;
        }
    }

    private void VerifyTargetWeaponReplacement(
        PendingWeaponSlotReplacement pending,
        int grantWaitFramesRemaining,
        int grantRetryAttemptsRemaining)
    {
        if (!TryGetPendingWeaponSlotReplacementPawn(pending, out var player, out var pawn))
            return;

        var targetPresent = HasReplayWeapon(pawn, pending.TargetItem);
        var anySlotWeapon = GetWeaponsInReplaySlot(pawn, pending.WeaponSlot).Any();
        switch (ReplayWeaponReplacementPolicy.VerifyGrant(
                    targetPresent,
                    anySlotWeapon,
                    grantWaitFramesRemaining,
                    grantRetryAttemptsRemaining))
        {
            case WeaponGrantVerificationAction.TargetReady:
                FinishWeaponSlotReplacement(pending, success: true, "target_granted");
                return;

            case WeaponGrantVerificationAction.Conflict:
                FinishWeaponSlotReplacement(pending, success: false, "target_grant_conflict");
                return;

            case WeaponGrantVerificationAction.WaitForAttachment:
                Server.NextFrame(() => VerifyTargetWeaponReplacement(
                    pending,
                    grantWaitFramesRemaining - 1,
                    grantRetryAttemptsRemaining));
                return;

            case WeaponGrantVerificationAction.RetryGrant:
                // GiveNamedItem can return a non-null entity before CS2 has
                // actually attached it to the pawn's weapon slot. Observe a
                // full window first, then issue one bounded retry instead of
                // creating another pending entity every frame.
                _ = TryGiveNamedItem(player, pending.TargetItem);
                Server.NextFrame(() => VerifyTargetWeaponReplacement(
                    pending,
                    WeaponSlotReplacementGrantWaitFrames,
                    grantRetryAttemptsRemaining - 1));
                return;

            case WeaponGrantVerificationAction.UseFallback:
                _ = TryGiveNamedItem(player, pending.FallbackItem);
                Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                    pending,
                    WeaponSlotReplacementFallbackWaitFrames,
                    WeaponSlotReplacementFallbackRetryAttempts,
                    "target_grant_timeout"));
                return;
        }
    }

    private void VerifyFallbackWeaponIfNeeded(
        PendingWeaponSlotReplacement pending,
        int fallbackWaitFramesRemaining,
        int fallbackRetryAttemptsRemaining,
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
                    fallbackRetryAttemptsRemaining,
                    failureReason));
                return;
            }

            FinishWeaponSlotReplacement(pending, success: false, $"{failureReason}_weapon_preserved");
            return;
        }

        if (fallbackWaitFramesRemaining > 0)
        {
            Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                pending,
                fallbackWaitFramesRemaining - 1,
                fallbackRetryAttemptsRemaining,
                failureReason));
            return;
        }

        if (fallbackRetryAttemptsRemaining > 0)
        {
            _ = TryGiveNamedItem(player, pending.FallbackItem);
            Server.NextFrame(() => VerifyFallbackWeaponIfNeeded(
                pending,
                WeaponSlotReplacementFallbackWaitFrames,
                fallbackRetryAttemptsRemaining - 1,
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
            !IsReplayWriteEpochCurrent(
                pending.PlayerSlot,
                pending.ReplayWriteEpoch) ||
            currentPlayer is not { IsValid: true, PawnIsAlive: true } ||
            currentPlayer.UserId != pending.PlayerUserId ||
            currentPawn is not { IsValid: true } ||
            currentPawn.EntityHandle.Raw != pending.PawnEntityHandle ||
            currentPawn.WeaponServices == null)
        {
            if (_session.PendingWeaponSlotReplacements.TryGetValue(key, out current) && current == pending)
                CancelPendingWeaponSlotReplacement(pending, "stale_callback");
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
        _session.WeaponLoadoutSyncedSlots.Remove(pending.PlayerSlot);
        Server.PrintToConsole(
            $"[DTR WARN] weapon slot replacement incomplete slot={pending.PlayerSlot} " +
            $"target={pending.TargetItem} fallback={pending.FallbackItem} reason={reason}");
    }

    private void FinalizeReplayLoadoutSyncIfCurrent(PendingWeaponSlotReplacement pending)
    {
        var player = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        if (!IsReplayWriteEpochCurrent(
                pending.PlayerSlot,
                pending.ReplayWriteEpoch) ||
            player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != pending.PlayerUserId ||
            _session.PendingWeaponSlotReplacements.Keys.Any(
                key => key.PlayerSlot == pending.PlayerSlot) ||
            !_session.LoadedReplays.TryGetValue(pending.PlayerSlot, out var replay))
        {
            return;
        }

        _session.WeaponLoadoutSyncedSlots.Remove(pending.PlayerSlot);
        ApplyReplayLoadoutForSlot(pending.PlayerSlot, replay);
    }

    private void CancelPendingWeaponSlotReplacement(
        PendingWeaponSlotReplacement pending,
        string reason)
    {
        var key = (pending.PlayerSlot, pending.WeaponSlot);
        if (!_session.PendingWeaponSlotReplacements.TryGetValue(key, out var current) ||
            current != pending)
        {
            return;
        }

        _session.PendingWeaponSlotReplacements.Remove(key);
        var player = Utilities.GetPlayerFromSlot(pending.PlayerSlot);
        var pawn = player?.PlayerPawn.Value;
        var samePlayer = player is { IsValid: true, PawnIsAlive: true } &&
                         player.UserId == pending.PlayerUserId;
        var samePawn = pawn is { IsValid: true } &&
                       pawn.EntityHandle.Raw == pending.PawnEntityHandle &&
                       pawn.WeaponServices != null;
        var targetPresent = samePawn && HasReplayWeapon(pawn!, pending.TargetItem);
        var anySlotWeapon = samePawn && GetWeaponsInReplaySlot(pawn!, pending.WeaponSlot).Any();
        if (!ReplayWeaponReplacementPolicy.ShouldRestoreFallback(
                samePlayer,
                samePawn,
                targetPresent,
                anySlotWeapon))
        {
            return;
        }

        var restored = TryGiveNamedItem(player!, pending.FallbackItem);
        Server.PrintToConsole(
            restored
                ? $"dtr: restored cancelled weapon replacement slot={pending.PlayerSlot} item={pending.FallbackItem} reason={reason}"
                : $"[DTR WARN] failed to restore cancelled weapon replacement slot={pending.PlayerSlot} item={pending.FallbackItem} reason={reason}");
    }

    private void ClearPendingWeaponSlotReplacementsForSlot(
        int slot,
        string reason = "slot_cancelled")
    {
        foreach (var pending in _session.PendingWeaponSlotReplacements
                     .Where(pair => pair.Key.PlayerSlot == slot)
                     .Select(pair => pair.Value)
                     .ToArray())
        {
            CancelPendingWeaponSlotReplacement(pending, reason);
        }
    }

    private void ClearAllPendingWeaponSlotReplacements(string reason)
    {
        foreach (var pending in _session.PendingWeaponSlotReplacements.Values.ToArray())
            CancelPendingWeaponSlotReplacement(pending, reason);
    }
}
