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
    private void StopOneSlot(CommandInfo command, int slot, string reason)
    {
        StopVoiceTestPlayback(reason, printSummary: false);
        var ok = BotControllerNative.StopReplay(slot);
        ReleaseReplaySlot(slot, reason);
        command.ReplyToCommand(ok
            ? $"[DTR OK] stopped slot {slot}"
            : $"[DTR ERR] failed to stop slot {slot}");
    }

    private void RemoveReplaySlot(
        int slot,
        string reason,
        out bool stopped,
        out bool unloaded)
    {
        stopped = BotControllerNative.StopReplay(slot);
        unloaded = BotControllerNative.UnloadReplay(slot);
        CommitReplaySlotRemoval(slot, reason);
    }

    private void CommitReplaySlotRemoval(int slot, string reason)
    {
        ReleaseReplaySlot(slot, reason);
        _session.ReplaySlots.Unload(slot);
        _session.WarmReplayBufferSlots.Remove(slot);
        ForgetRetainedBotHiderPresentation(slot);
        ForgetLoadedReplayMetadata(slot);
    }

    private static void IssueRestartIfRequested(CommandInfo command, bool restart)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        command.ReplyToCommand("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_prestart.");
    }

    private static void IssueRestartIfRequested(bool restart, Action<string> reply)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        reply("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_prestart.");
    }

    private void MarkReplayStarted(int slot)
    {
        _retainedReplayViewmodelSlots.Remove(slot);
        _session.ReplaySlots.MarkPlaying(slot);
        _session.ReplayStartedAt[slot] = Server.CurrentTime;
        _session.ReplayPerceptionBaselineSerial[slot] =
            BotControllerNative.TryGetNativePerceptionState(slot, out var perception)
                ? perception.UpdateSerial
                : 0u;
        _session.ProjectileAlignNextBySlot[slot] = 0;
        _session.ReplayHifiEventNextBySlot[slot] = 0;
    }

    private bool CanWriteReplaySlot(int slot)
        => _session.ReplaySlots.IsOwned(slot) && IsReplaySlotStillSafe(slot);

    private void ReleaseReplaySlot(
        int slot,
        string reason,
        ReplayReleaseKind releaseKind = ReplayReleaseKind.Immediate)
    {
        CancelReplaySlotDeferredWork(slot);
        _session.ReplaySlots.Release(slot);
        CancelSafeC4MutationWithoutTarget();
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        _cosmeticAlignmentTracker.CancelPending(slot);
        _session.FreezePrerollSlots.Remove(slot);
        _session.ResumedFreezePrerollSlots.Remove(slot);
        var retainedViewmodel = (releaseKind is ReplayReleaseKind.Handoff or ReplayReleaseKind.Finished) &&
                                RetainReplayBotViewmodelForRound(slot);
        if (!retainedViewmodel)
            RestoreReplayBotViewmodel(slot);
        _session.ReplayStartedAt.Remove(slot);
        _session.ReplayPerceptionBaselineSerial.Remove(slot);
        _session.LastEnsuredWeaponDef.Remove(slot);
        _session.LastReplayWeaponDef.Remove(slot);
        _session.LastLockedWeaponTarget.Remove(slot);
        _session.ProjectileAlignNextBySlot.Remove(slot);
        _session.ReplayHifiEventNextBySlot.Remove(slot);
        if (releaseKind == ReplayReleaseKind.Immediate)
        {
            _session.RebuiltInventorySlots.Remove(slot);
            _session.WeaponLoadoutSyncedSlots.Remove(slot);
            _session.PawnEquipmentSync.Invalidate(slot);
            _session.BalanceSyncedSlots.Remove(slot);
        }
        BotControllerNative.ClearReplayPawnEquipment(slot);
        _session.PendingBulletHits.Remove(slot);
        _session.PendingBulletDamages.Remove(slot);
        _session.PendingThreat360.Remove(slot);
        // Native projectile birth align is a global queue without per-slot
        // cancellation. Handoff prioritizes the ownership boundary over a
        // possible in-flight alignment on another surviving replay slot.
        if (releaseKind == ReplayReleaseKind.Handoff || !HasActiveReplaySlots())
            BotControllerNative.ClearProjectileBirthAlign();
        BotControllerNative.ClearBuyPlan(slot);
        BotControllerNative.UnlockReplayControl(slot);
        BotControllerNative.UnlockWeaponSlot(slot);
        ClearReplayPovSlot(slot);
        if (releaseKind == ReplayReleaseKind.Handoff &&
            IsReplaySlotStillSafe(slot) &&
            HasLivePawn(Utilities.GetPlayerFromSlot(slot)) &&
            !BotControllerNative.RequestEquipBestWeapon(slot))
        {
            Server.PrintToConsole(
                $"dtr: handoff best-weapon request unavailable slot={slot}");
        }
        _ = SyncBotRandomizerCosmeticLease(announce: false);
        Server.PrintToConsole(
            $"dtr: released slot={slot} reason={reason} viewmodel={(retainedViewmodel ? "retained_round" : "released")}");
    }

    private bool HasActiveReplaySlots()
    {
        foreach (var slot in _session.LoadedSlots)
        {
            if (BotControllerNative.GetReplayState(slot).Playing)
                return true;
        }
        return false;
    }

    private bool HasAnyNativeActiveReplaySlot()
    {
        if (HasActiveReplaySlots())
            return true;

        foreach (var slot in NativeReplaySlots())
        {
            var state = BotControllerNative.GetReplayState(slot);
            if (state.Playing || state.Total > 0)
                return true;
        }
        return false;
    }

    private bool CheckReplayStartGates(
        Action<string> reply,
        bool stopCurrentForOverride,
        bool deferStopUntilRoundStart = false)
    {
        if (IsWarmupPeriod())
        {
            reply("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

        if (!stopCurrentForOverride || !HasAnyNativeActiveReplaySlot())
            return true;

        if (deferStopUntilRoundStart)
        {
            // dtr_go immediately follows this plan update with mp_restartgame.
            // Keep the current identity/cosmetic ownership intact until the
            // round_prestart transition can replace all leases atomically before
            // the next pawn inventory is constructed.
            reply("[DTR WARN] 当前DTR将在round_prestart被原子替换");
            return true;
        }

        reply("[DTR WARN] 会STOP当前所有DTR并override");
        StopAndUnloadLoaded();
        StopSequenceState();
        return true;
    }

    private bool IsReplaySlotBusy(int slot)
    {
        if (slot < 0)
            return false;
        if (_session.LoadedSlots.Contains(slot) ||
            _session.LoadedReplays.ContainsKey(slot))
        {
            return true;
        }

        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing || state.Total > 0;
    }

    private bool IsDemoTracerBot(int slot)
    {
        if (slot < 0)
            return false;

        if (_session.ReplaySlots.IsOwned(slot))
        {
            return true;
        }

        if (_session.Plan.Armed || _session.Plan.ArmedPrepared || _session.Plan.SequenceActive)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true } && IsReplayTargetBot(player))
                return true;
        }

        var state = BotControllerNative.GetReplayState(slot);
        return state.Playing;
    }

    private bool TryGetBotCosmeticState(int slot, out DemoTracerBotCosmeticState state)
    {
        state = new DemoTracerBotCosmeticState();
        if (slot < 0)
            return false;

        state.IsDemoTracerBot = IsDemoTracerBot(slot);
        state.IsSlotBusy = IsReplaySlotBusy(slot);
        state.CosmeticWriterEnabled = AnyCosmeticFeatureEnabled();
        state.HasCosmeticEvidence =
            _session.LoadedReplays.TryGetValue(slot, out var replay) &&
            HasCosmeticEvidence(replay.Cosmetics) &&
            IsReplaySlotStillSafe(slot);
        state.ShouldDeferInventoryWrites =
            state.IsDemoTracerBot &&
            state.HasCosmeticEvidence &&
            state.CosmeticWriterEnabled;
        return state.IsDemoTracerBot || state.IsSlotBusy || state.HasCosmeticEvidence;
    }

}
