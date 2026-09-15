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
    private void StopAndUnloadLoaded()
        => StopAndUnloadLoaded(clearArmedPlan: true, releaseBuffers: true);

    private void StopAndUnloadLoaded(bool clearArmedPlan)
        => StopAndUnloadLoaded(clearArmedPlan, releaseBuffers: true);

    private void StopAndUnloadLoaded(bool clearArmedPlan, bool releaseBuffers)
    {
        CancelAllReplayDeferredWork();
        CancelDtrRoundBanner(resetRound: false);
        InvalidateInitialSpawnAssignment();
        ClearLoadedTeamAvatarOverrides("unload_all");
        StopVoiceTestPlayback("unload_all", printSummary: false);
        ClearLoadedAutoVoiceClip();
        ClearLoadedAutoChat();
        ReleaseBotRandomizerCosmeticLease("unload_all");
        foreach (var slot in _session.LoadedSlots.ToArray())
        {
            if (releaseBuffers)
            {
                BotControllerNative.UnloadReplay(slot);
                _session.WarmReplayBufferSlots.Remove(slot);
            }
            else
            {
                BotControllerNative.StopReplay(slot);
                _session.WarmReplayBufferSlots.Add(slot);
            }
            ReleaseReplaySlot(slot, "unload_all");
        }
        if (releaseBuffers)
            ReleaseUnusedWarmReplayBuffers();
        _session.ReplaySlots.Clear();
        _session.LoadedReplays.Clear();
        _session.ReplayIdentityGenerationBySlot.Clear();
        ClearStoppedReplayExecutionState();
        _session.WeaponLoadoutSyncedSlots.Clear();
        _session.PawnEquipmentSync.Clear();
        _session.BalanceSyncedSlots.Clear();
        ResetCosmeticAlignState(resetCounters: true);
        ResetStickerAlignState(resetCounters: true);
        ResetCharmAlignState(resetCounters: true);
        ResetCrosshairAlignState(resetCounters: true);
        ResetViewmodelAlignState(resetCounters: true);
        ResetScoreboardAlignState(resetCounters: true);
        _session.LoadedRoundScoreboard = null;
        if (clearArmedPlan)
        {
            _session.Plan.ClearArmed();
        }
        else
        {
            _session.Plan.ClearArmedPreparation();
        }
        _session.Plan.ClearSequencePreparation();
    }

    private void ClearReplayStateForLifecycle(string reason)
    {
        if (_lifecycleResetInProgress)
            return;

        _lifecycleResetInProgress = true;
        try
        {
            CancelAllReplayDeferredWork();
            CancelReplayPrefetch();
            InvalidateInitialSpawnAssignment();
            ClearLoadedTeamAvatarOverrides(reason);
            ClearFreezePrerollReplayState();
            ClearReplayLeftHandDesiredLatches();
            var hadReplayState = _session.ReplaySlots.HasAnyState ||
                                 _session.LoadedReplays.Count > 0 ||
                                 _retainedReplayViewmodelSlots.Count > 0 ||
                                 _roundBannerPlayback != null ||
                                 _voiceTestPlayback != null ||
                                 _chatPlayback != null ||
                                 _session.Plan.Armed ||
                                 _session.Plan.SequenceActive ||
                                 HasPlayoffSchedulingState();

            StopVoiceTestPlayback(reason, printSummary: false);
            CancelDtrRoundBanner(resetRound: true);
            InvalidateFreezePreroll();
            ReleaseBotRandomizerCosmeticLease(reason);

            if (BotControllerNative.IsCompatible)
            {
                foreach (var slot in ReplayBufferSlots())
                    ClearNativeSlotForLifecycle(slot, reason);
            }
            ClearLoadedAutoVoiceClip();
            ClearVoiceClipCache();
            ClearLoadedAutoChat();

            _session.ReplaySlots.Clear();
            _session.WarmReplayBufferSlots.Clear();
            _session.LoadedReplays.Clear();
            ClearReplayRetentionPriority(clearPending: true);
            ClearRetainedBotHiderPresentation();
            _session.ReplayHifiEventNextBySlot.Clear();
            _session.ReplayInventoryBySlot.Clear();
            _session.ReplayIdentityGenerationBySlot.Clear();
            ClearStoppedReplayExecutionState();
            _session.WeaponLoadoutSyncedSlots.Clear();
            _session.PawnEquipmentSync.Clear();
            _session.BalanceSyncedSlots.Clear();
            ResetCosmeticAlignState(resetCounters: true);
            ResetStickerAlignState(resetCounters: true);
            ResetCharmAlignState(resetCounters: true);
            ResetCrosshairAlignState(resetCounters: true);
            ResetViewmodelAlignState(resetCounters: true);
            ResetScoreboardAlignState(resetCounters: true);
            _session.LoadedRoundScoreboard = null;

            _session.Plan.ClearArmed();
            StopSequenceState();

            if (hadReplayState)
                Server.PrintToConsole($"dtr: cleared replay lifecycle state reason={reason}");
        }
        finally
        {
            _lifecycleResetInProgress = false;
        }
    }

    private bool HasReplayLifecycleState()
    {
        if (_session.ReplaySlots.HasAnyState ||
            _session.LoadedReplays.Count > 0 ||
            _session.WarmReplayBufferSlots.Count > 0 ||
            _retainedReplayViewmodelSlots.Count > 0 ||
            _session.Plan.Armed ||
            _session.Plan.SequenceActive ||
            HasPlayoffSchedulingState())
        {
            return true;
        }

        return false;
    }

    private void ClearNativeSlotForLifecycle(int slot, string reason)
    {
        try
        {
            BotControllerNative.UnloadReplay(slot);
            ReleaseReplaySlot(slot, reason);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: lifecycle native clear failed slot={slot}: {ex.Message}");
        }
    }

    private void StopLoadedReplaySlots(string reason)
    {
        CancelAllReplayDeferredWork();
        InvalidateFreezePreroll();
        InvalidateInitialSpawnAssignment();
        CancelDtrRoundBanner(resetRound: false);
        StopVoiceTestPlayback(reason, printSummary: false);
        StopChatPlayback(reason);
        foreach (var slot in _session.LoadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason);
        }
        ClearStoppedReplayExecutionState();
        ClearReplayCrosshairPresentation();
        RestoreAllReplayBotViewmodels();
    }

    private void ClearStoppedReplayExecutionState()
    {
        _session.LastEnsuredWeaponDef.Clear();
        _session.LastReplayWeaponDef.Clear();
        _session.LastLockedWeaponTarget.Clear();
        ClearAllPendingWeaponSlotReplacements("replay_execution_stopped");
        _session.ProjectileAlignNextBySlot.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
        _session.RebuiltInventorySlots.Clear();
        _session.ReplayStartedAt.Clear();
        _session.ReplayPerceptionBaselineSerial.Clear();
        _session.PendingBulletHits.Clear();
        _session.PendingBulletDamages.Clear();
        _session.PendingThreat360.Clear();
        _session.SafeC4Aligned = false;
    }

    private void ReleaseUnusedWarmReplayBuffers()
    {
        foreach (var slot in _session.WarmReplayBufferSlots)
            BotControllerNative.UnloadReplay(slot);
        _session.WarmReplayBufferSlots.Clear();
    }

    private void StopAllState(string reason)
    {
        CancelReplayPrefetch();
        StopLoadedReplaySlots(reason);
        ClearReplayRetentionPriority(clearPending: true);
        ClearLoadedAutoVoiceClip();
        ClearLoadedAutoChat();
        _session.Plan.ClearArmed();
        StopSequenceState();
        ReleaseUnusedWarmReplayBuffers();
    }

    private bool StopReplayStateForRoundBoundary(string reason)
    {
        if (!_session.ReplaySlots.HasAnyState &&
            _retainedReplayViewmodelSlots.Count == 0 &&
            _session.WarmReplayBufferSlots.Count == 0)
            return false;

        var keepWarmBuffers = _session.Plan.SequenceActive || _session.Plan.Armed;
        StopAndUnloadLoaded(
            clearArmedPlan: false,
            releaseBuffers: !keepWarmBuffers);
        return true;
    }

    private int[] ReplayBufferSlots()
        => _session.LoadedSlots
            .Concat(_session.WarmReplayBufferSlots)
            .Concat(_session.LoadedReplays.Keys)
            .Distinct()
            .ToArray();

}
