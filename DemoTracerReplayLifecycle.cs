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
        CancelDtrRoundBanner(resetRound: false);
        InvalidateInitialSpawnAssignment();
        var trackedSlots = _session.LoadedSlots.ToHashSet();
        StopVoiceTestPlayback("unload_all", printSummary: false);
        ClearLoadedAutoVoiceClip();
        ClearLoadedAutoChat();
        RestoreAllReplayMusicKits("unload_all");
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
        StopUntrackedNativeReplaySlots(trackedSlots, "unload_all");
        _session.LoadedSlots.Clear();
        _session.DemoTracerOwnedSlots.Clear();
        _session.LoadedReplays.Clear();
        _session.LastEnsuredWeaponDef.Clear();
        _session.LastReplayWeaponDef.Clear();
        _session.LastLockedWeaponTarget.Clear();
        _session.PendingWeaponSlotReplacements.Clear();
        _session.ActiveWeaponCosmetics.Clear();
        _session.ProjectileAlignNextBySlot.Clear();
        _session.ReplayIdentityGenerationBySlot.Clear();
        _session.ReplayMutationGenerationBySlot.Clear();
        _session.PendingProjectileAlign.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
        _session.RebuiltInventorySlots.Clear();
        _session.LoadoutSyncedSlots.Clear();
        _session.BalanceSyncedSlots.Clear();
        ResetCosmeticAlignState(resetCounters: true);
        ResetStickerAlignState(resetCounters: true);
        ResetCharmAlignState(resetCounters: true);
        ResetCrosshairAlignState(resetCounters: true);
        ResetViewmodelAlignState(resetCounters: true);
        ResetScoreboardAlignState(resetCounters: true);
        _session.LoadedRoundScoreboard = null;
        _session.LastPlayingSlots.Clear();
        _session.ReplayStartedAt.Clear();
        _session.ReplayPerceptionBaselineSerial.Clear();
        _session.PendingBulletHits.Clear();
        _session.PendingBulletDamages.Clear();
        _session.PendingThreat360.Clear();
        _session.SafeC4Aligned = false;
        if (clearArmedPlan)
        {
            _session.Armed = false;
            _session.ArmedPrepared = false;
            _session.ArmedManifestPath = string.Empty;
            _session.ArmedSourceRound = -1;
        }
        else
        {
            _session.ArmedPrepared = false;
        }
        _session.SequencePrepared = false;
        _session.SequencePreparedRound = -1;
        SetReplayPovMask(0);
    }

    private void ClearReplayStateForLifecycle(string reason)
    {
        if (_lifecycleResetInProgress)
            return;

        _lifecycleResetInProgress = true;
        try
        {
            CancelReplayPrefetch();
            InvalidateInitialSpawnAssignment();
            ClearFreezePrerollReplayState();
            ClearReplayLeftHandDesiredLatches(forceNative: true);
            var hadReplayState = _session.LoadedSlots.Count > 0 ||
                                 _session.DemoTracerOwnedSlots.Count > 0 ||
                                 _session.LoadedReplays.Count > 0 ||
                                 _session.LastPlayingSlots.Count > 0 ||
                                 _retainedReplayViewmodelSlots.Count > 0 ||
                                 _session.PendingProjectileAlign.Count > 0 ||
                                 _roundBannerPlayback != null ||
                                 _voiceTestPlayback != null ||
                                 _chatPlayback != null ||
                                 _session.Armed ||
                                 _session.SequenceActive ||
                                 HasPlayoffSchedulingState();

            StopVoiceTestPlayback(reason, printSummary: false);
            CancelDtrRoundBanner(resetRound: true);
            InvalidateFreezePreroll();
            if (reason.StartsWith("map_start:", StringComparison.OrdinalIgnoreCase))
            {
                _session.ReplayMusicKitBaselines.Clear();
                _session.ReplayMusicKitRepairTokens.Clear();
            }
            else
            {
                RestoreAllReplayMusicKits(reason);
            }
            ReleaseBotRandomizerCosmeticLease(reason);

            if (BotControllerNative.IsCompatible)
            {
                foreach (var slot in NativeReplaySlots())
                    ClearNativeSlotForLifecycle(slot);
                _ = BotControllerNative.ClearAllBuyPlans();
                _ = BotControllerNative.SetReplayPovMask(0);
            }
            _session.LastReplayPovMask = 0;
            ClearLoadedAutoVoiceClip();
            ClearVoiceClipCache();
            ClearLoadedAutoChat();

            _session.LoadedSlots.Clear();
            _session.WarmReplayBufferSlots.Clear();
            _session.DemoTracerOwnedSlots.Clear();
            _session.LoadedReplays.Clear();
            ClearReplayRetentionPriority(clearPending: true);
            ClearRetainedBotHiderPresentation();
            _session.LastEnsuredWeaponDef.Clear();
            _session.LastReplayWeaponDef.Clear();
            _session.LastLockedWeaponTarget.Clear();
            _session.PendingWeaponSlotReplacements.Clear();
            _session.ProjectileAlignNextBySlot.Clear();
            _session.ReplayHifiEventNextBySlot.Clear();
            _session.ReplayIdentityGenerationBySlot.Clear();
            _session.ReplayMutationGenerationBySlot.Clear();
            _session.PendingProjectileAlign.Clear();
            BotControllerNative.ClearProjectileBirthAlign();
            _session.RebuiltInventorySlots.Clear();
            _session.LoadoutSyncedSlots.Clear();
            _session.BalanceSyncedSlots.Clear();
            ResetCosmeticAlignState(resetCounters: true);
            ResetStickerAlignState(resetCounters: true);
            ResetCharmAlignState(resetCounters: true);
            ResetCrosshairAlignState(resetCounters: true);
            ResetViewmodelAlignState(resetCounters: true);
            ResetScoreboardAlignState(resetCounters: true);
            _session.LoadedRoundScoreboard = null;
            _session.LastPlayingSlots.Clear();
            _session.ReplayStartedAt.Clear();
            _session.ReplayPerceptionBaselineSerial.Clear();
            _session.PendingBulletHits.Clear();
            _session.PendingBulletDamages.Clear();
            _session.PendingThreat360.Clear();
            _session.SafeC4Aligned = false;

            _session.Armed = false;
            _session.ArmedLoop = false;
            _session.ArmedPrepared = false;
            _session.ArmedLabel = string.Empty;
            _session.ArmedManifestPath = string.Empty;
            _session.ArmedSourceRound = -1;
            StopSequenceState();

            if (hadReplayState)
                Server.PrintToConsole($"dtr: cleared replay lifecycle state reason={reason}");
        }
        finally
        {
            _lifecycleResetInProgress = false;
        }
    }

    private bool HasReplayLifecycleState(bool includeNative = false)
    {
        if (_session.LoadedSlots.Count > 0 ||
            _session.DemoTracerOwnedSlots.Count > 0 ||
            _session.LoadedReplays.Count > 0 ||
            _session.LastPlayingSlots.Count > 0 ||
            _retainedReplayViewmodelSlots.Count > 0 ||
            _session.PendingProjectileAlign.Count > 0 ||
            _session.Armed ||
            _session.SequenceActive ||
            HasPlayoffSchedulingState())
        {
            return true;
        }

        return includeNative && BotControllerNative.IsCompatible && HasAnyNativeActiveReplaySlot();
    }

    private static void ClearNativeSlotForLifecycle(int slot)
    {
        try
        {
            BotControllerNative.UnloadReplay(slot);
            BotControllerNative.ClearBuyPlan(slot);
            BotControllerNative.UnlockReplayControl(slot);
            BotControllerNative.UnlockWeaponSlot(slot);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: lifecycle native clear failed slot={slot}: {ex.Message}");
        }
    }

    private void StopLoadedReplaySlots(string reason)
    {
        CancelDtrRoundBanner(resetRound: false);
        StopVoiceTestPlayback(reason, printSummary: false);
        StopChatPlayback(reason);
        foreach (var slot in _session.LoadedSlots.ToArray())
        {
            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason);
        }
        _session.LastEnsuredWeaponDef.Clear();
        _session.LastReplayWeaponDef.Clear();
        _session.LastLockedWeaponTarget.Clear();
        _session.PendingWeaponSlotReplacements.Clear();
        _session.ActiveWeaponCosmetics.Clear();
        _session.ProjectileAlignNextBySlot.Clear();
        _session.PendingProjectileAlign.Clear();
        BotControllerNative.ClearProjectileBirthAlign();
        _session.RebuiltInventorySlots.Clear();
        _session.CosmeticSyncedSlots.Clear();
        _cosmeticHeartbeatTokens.Clear();
        ClearReplayCrosshairPresentation();
        RestoreAllReplayBotViewmodels();
        _session.LastPlayingSlots.Clear();
        _session.ReplayStartedAt.Clear();
        _session.ReplayPerceptionBaselineSerial.Clear();
        _session.PendingBulletHits.Clear();
        _session.PendingBulletDamages.Clear();
        _session.PendingThreat360.Clear();
        _session.SafeC4Aligned = false;
        SetReplayPovMask(0);
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
        _session.Armed = false;
        _session.ArmedPrepared = false;
        _session.ArmedManifestPath = string.Empty;
        _session.ArmedSourceRound = -1;
        StopSequenceState();
        ReleaseUnusedWarmReplayBuffers();
    }

    private bool StopReplayStateForRoundBoundary(string reason)
    {
        if (_session.LoadedSlots.Count == 0 &&
            _session.LastPlayingSlots.Count == 0 &&
            _retainedReplayViewmodelSlots.Count == 0 &&
            !HasAnyNativeActiveReplaySlot())
            return false;

        var keepWarmBuffers = _session.SequenceActive || _session.Armed;
        StopAndUnloadLoaded(
            clearArmedPlan: false,
            releaseBuffers: !keepWarmBuffers);
        return true;
    }

    private static IEnumerable<int> NativeReplaySlots()
    {
        for (var slot = 0; slot < MaxPlayerSlots; slot++)
            yield return slot;
    }

    private void StopUntrackedNativeReplaySlots(IReadOnlySet<int> trackedSlots, string reason)
    {
        foreach (var slot in NativeReplaySlots())
        {
            if (trackedSlots.Contains(slot) || _session.WarmReplayBufferSlots.Contains(slot))
                continue;

            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing && state.Total <= 0)
                continue;

            BotControllerNative.UnloadReplay(slot);
            BotControllerNative.ClearBuyPlan(slot);
            BotControllerNative.UnlockWeaponSlot(slot);
            ClearReplayPovSlot(slot);
            Server.PrintToConsole($"dtr: stopped native replay slot={slot} reason={reason}");
        }
    }

    private void StopOneSlot(CommandInfo command, int slot, string reason)
    {
        StopVoiceTestPlayback(reason, printSummary: false);
        var ok = BotControllerNative.StopReplay(slot);
        ReleaseReplaySlot(slot, reason);
        command.ReplyToCommand(ok
            ? $"[DTR OK] stopped slot {slot}"
            : $"[DTR ERR] failed to stop slot {slot}");
    }

    private static void IssueRestartIfRequested(CommandInfo command, bool restart)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        command.ReplyToCommand("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_start.");
    }

    private static void IssueRestartIfRequested(bool restart, Action<string> reply)
    {
        if (!restart)
            return;

        Server.ExecuteCommand("mp_restartgame 1");
        reply("[DTR OK] Issued \"mp_restartgame 1\". Waiting for next round_start.");
    }

    private void MarkReplayStarted(int slot)
    {
        _retainedReplayViewmodelSlots.Remove(slot);
        _session.LastPlayingSlots.Add(slot);
        _session.ReplayStartedAt[slot] = Server.CurrentTime;
        _session.ReplayPerceptionBaselineSerial[slot] =
            BotControllerNative.TryGetNativePerceptionState(slot, out var perception)
                ? perception.UpdateSerial
                : 0u;
        _session.ProjectileAlignNextBySlot[slot] = 0;
        _session.ReplayHifiEventNextBySlot[slot] = 0;
    }

    private void ReleaseReplaySlot(
        int slot,
        string reason,
        ReplayReleaseKind releaseKind = ReplayReleaseKind.Immediate)
    {
        InvalidateReplayMusicKitRepair(slot);
        InvalidateReplayMutationGeneration(slot);
        _session.FreezePrerollSlots.Remove(slot);
        _session.ResumedFreezePrerollSlots.Remove(slot);
        var retainedViewmodel = (releaseKind is ReplayReleaseKind.Handoff or ReplayReleaseKind.Finished) &&
                                RetainReplayBotViewmodelForRound(slot);
        if (!retainedViewmodel)
            RestoreReplayBotViewmodel(slot);
        _session.LastPlayingSlots.Remove(slot);
        _session.ReplayStartedAt.Remove(slot);
        _session.ReplayPerceptionBaselineSerial.Remove(slot);
        _session.LastEnsuredWeaponDef.Remove(slot);
        _session.LastReplayWeaponDef.Remove(slot);
        _session.LastLockedWeaponTarget.Remove(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        _session.ActiveWeaponCosmetics.Remove(slot);
        _session.ProjectileAlignNextBySlot.Remove(slot);
        _session.ReplayHifiEventNextBySlot.Remove(slot);
        _session.DemoTracerOwnedSlots.Remove(slot);
        _session.RebuiltInventorySlots.Remove(slot);
        _session.LoadoutSyncedSlots.Remove(slot);
        _session.BalanceSyncedSlots.Remove(slot);
        _session.PendingBulletHits.Remove(slot);
        _session.PendingBulletDamages.Remove(slot);
        _session.PendingThreat360.Remove(slot);
        _session.CosmeticSyncedSlots.Remove(slot);
        _cosmeticHeartbeatTokens.Remove(slot);
        BotControllerNative.ClearBuyPlan(slot);
        BotControllerNative.UnlockReplayControl(slot);
        BotControllerNative.UnlockWeaponSlot(slot);
        ClearReplayPovSlot(slot);
        ScheduleLoadedReplayCosmeticRepairForSlot(slot);
        if (releaseKind == ReplayReleaseKind.Handoff &&
            IsReplaySlotStillSafe(slot) &&
            HasLivePawn(Utilities.GetPlayerFromSlot(slot)) &&
            !BotControllerNative.RequestEquipBestWeapon(slot))
        {
            Server.PrintToConsole(
                $"dtr: handoff best-weapon request unavailable slot={slot}");
        }
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

    private bool CheckReplayStartGates(Action<string> reply, bool stopCurrentForOverride)
    {
        if (IsWarmupPeriod())
        {
            reply("[DTR ERR] 热身阶段无法进行回放");
            return false;
        }

        if (!stopCurrentForOverride || !HasAnyNativeActiveReplaySlot())
            return true;

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

        if (_session.DemoTracerOwnedSlots.Contains(slot))
        {
            return true;
        }

        if (_session.Armed || _session.ArmedPrepared || _session.SequenceActive)
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

    private void RememberLoadedSlot(int slot)
    {
        if (!_session.LoadedSlots.Contains(slot))
            _session.LoadedSlots.Add(slot);
        _session.DemoTracerOwnedSlots.Add(slot);
    }

    private long BeginReplayIdentityGeneration(int slot)
    {
        var generation = ++_session.NextReplayIdentityGeneration;
        _session.ReplayIdentityGenerationBySlot[slot] = generation;
        return generation;
    }

    private long CurrentReplayIdentityGeneration(int slot)
    {
        if (_session.ReplayIdentityGenerationBySlot.TryGetValue(slot, out var generation))
            return generation;

        return BeginReplayIdentityGeneration(slot);
    }

    private bool IsReplayIdentityGenerationCurrent(int slot, long generation)
        => _session.ReplayIdentityGenerationBySlot.TryGetValue(slot, out var current) &&
           current == generation;

    private void InvalidateReplayIdentityGeneration(int slot)
        => _session.ReplayIdentityGenerationBySlot.Remove(slot);

    private long CurrentReplayMutationGeneration(int slot)
    {
        if (_session.ReplayMutationGenerationBySlot.TryGetValue(slot, out var generation))
            return generation;

        generation = ++_session.NextReplayMutationGeneration;
        _session.ReplayMutationGenerationBySlot[slot] = generation;
        return generation;
    }

    private bool IsReplayMutationGenerationCurrent(int slot, long generation)
        => _session.ReplayMutationGenerationBySlot.TryGetValue(slot, out var current) &&
           current == generation;

    private void InvalidateReplayMutationGeneration(int slot)
    {
        _session.ReplayMutationGenerationBySlot.Remove(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
    }

    private void ForgetLoadedReplayMetadata(int slot)
    {
        InvalidateInitialSpawnAssignment();
        RestoreReplayMusicKitForSlot(slot, "forget_replay");
        InvalidateReplayIdentityGeneration(slot);
        InvalidateReplayMutationGeneration(slot);
        _session.LoadedReplays.Remove(slot);
        _session.LastEnsuredWeaponDef.Remove(slot);
        _session.LastReplayWeaponDef.Remove(slot);
        _session.LastLockedWeaponTarget.Remove(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        _session.ReplayHifiEventNextBySlot.Remove(slot);
        _session.RebuiltInventorySlots.Remove(slot);
        _session.BalanceSyncedSlots.Remove(slot);
        InvalidateReplayMusicKitRepair(slot);
        _session.CosmeticSyncedSlots.Remove(slot);
        _cosmeticHeartbeatTokens.Remove(slot);
        _session.ActiveWeaponCosmetics.Remove(slot);
        _appliedGloveCosmetics.Remove(slot);
        _gloveCosmeticTokens.Remove(slot);
        _session.ScoreboardSyncedSlots.Remove(slot);
        _ = SyncBotHiderPresentationLease(announce: false);
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private void TrackLoadedReplay(
        int slot,
        string path,
        string playerName,
        ulong steamId = 0,
        int manifestFirstWeaponDefIndex = -1,
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices = null,
        ReplayLoadoutSnapshot? loadout = null,
        int musicKitId = 0,
        ReplayScoreboardFlair? scoreboardFlair = null,
        ReplayCosmetics? cosmetics = null,
        ReplayView? view = null,
        ReplayPlayerScoreboard? scoreboard = null,
        CsTeam? manifestTeam = null,
        ReplayFileMetadata? replayMetadata = null,
        int retentionRank = ReplayRetentionPriorityParser.MaxPlayersPerTeam)
    {
        InvalidateInitialSpawnAssignment();
        RestoreReplayBotViewmodel(slot);
        var hadPreviousGeneration = _session.ReplayIdentityGenerationBySlot.TryGetValue(
            slot,
            out var previousGeneration);
        var metadata = replayMetadata ?? ReadReplayMetadataOrEmpty(path);
        TryBuildWeaponPlan(metadata.WeaponDefIndices ?? [], out var scannedFirstDef, out var scannedPreloadDefs);
        var firstDef = NormalizeWeaponDefIndex(manifestFirstWeaponDefIndex);
        if (!IsKnownWeaponDefIndex(firstDef))
            firstDef = scannedFirstDef;

        var hasLoadout = loadout != null;
        var normalizedLoadout = NormalizeReplayLoadout(loadout ?? new ReplayLoadoutSnapshot());
        var preloadDefs = BuildReplayPreloadWeaponDefs(
            manifestPreloadWeaponDefIndices,
            scannedPreloadDefs,
            normalizedLoadout,
            hasLoadout);
        var hifiEvents = (metadata.HighFidelity?.Events ?? [])
            .OrderBy(replayEvent => replayEvent.TickIndex)
            .ThenBy(replayEvent => replayEvent.Tick)
            .ToArray();
        var inventorySnapshots = (metadata.HighFidelity?.InventorySnapshots ?? [])
            .OrderBy(snapshot => snapshot.TickIndex)
            .ThenBy(snapshot => snapshot.Tick)
            .ToArray();
        var normalizedCosmetics = NormalizeReplayCosmetics(cosmetics);
        var normalizedView = NormalizeReplayView(view);
        var normalizedScoreboard = NormalizeReplayScoreboard(scoreboard);
        var normalizedMusicKitId = NormalizeMusicKitId(musicKitId);
        _session.LoadedReplays[slot] = new LoadedReplay(
            path,
            playerName,
            steamId,
            manifestTeam,
            firstDef,
            preloadDefs,
            hasLoadout,
            normalizedLoadout,
            normalizedMusicKitId,
            NormalizeReplayScoreboardFlair(scoreboardFlair),
            normalizedCosmetics,
            normalizedView,
            normalizedScoreboard,
            metadata.Projectiles ?? [],
            hifiEvents,
            inventorySnapshots,
            metadata.HighFidelity?.RoundStartBalance,
            metadata.TickCount,
            metadata.TickRate,
            metadata.PlayStartTickIndex,
            metadata.RoundStartOrigin,
            retentionRank);
        InvalidateReplayMusicKitRepair(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
        InvalidateReplayMutationGeneration(slot);
        var generation = BeginReplayIdentityGeneration(slot);
        if (_session.ReplayMusicKitBaselines.TryGetValue(slot, out var musicKitBaseline))
        {
            if (hadPreviousGeneration && musicKitBaseline.Generation == previousGeneration)
                _session.ReplayMusicKitBaselines[slot] = musicKitBaseline with { Generation = generation };
            else
                _session.ReplayMusicKitBaselines.Remove(slot);
        }
        _session.LastEnsuredWeaponDef.Remove(slot);
        _session.LastReplayWeaponDef.Remove(slot);
        _session.LastLockedWeaponTarget.Remove(slot);
        _session.ActiveWeaponCosmetics.Remove(slot);
        _session.ProjectileAlignNextBySlot[slot] = 0;
        _session.ReplayHifiEventNextBySlot[slot] = 0;
        _session.RebuiltInventorySlots.Remove(slot);
        _session.LoadoutSyncedSlots.Remove(slot);
        _session.BalanceSyncedSlots.Remove(slot);
        _session.CosmeticSyncedSlots.Remove(slot);
        _cosmeticHeartbeatTokens.Remove(slot);
        _session.ScoreboardSyncedSlots.Remove(slot);
        _session.SafeC4Aligned = false;
        if (normalizedMusicKitId <= 0)
            RestoreReplayMusicKitForSlot(slot, "manifest_without_music_kit");
        _ = SyncBotHiderPresentationLease(announce: false);
        _ = SyncBotRandomizerCosmeticLease(announce: false);
    }

    private static ReplayFileMetadata ReadReplayMetadataOrEmpty(string path)
        => BotControllerNative.TryReadReplayMetadata(path, out var metadata)
            ? metadata
            : ReplayFileMetadata.Empty;

}
