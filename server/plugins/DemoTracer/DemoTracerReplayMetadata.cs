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
    private void RememberLoadedSlot(int slot)
    {
        CancelReplaySlotDeferredWork(slot);
        _session.ReplaySlots.LoadAndClaim(slot);
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

    private long CurrentReplayWriteEpoch(int slot)
        => _session.ReplaySlots.CurrentEpoch(slot);

    private bool IsReplayWriteEpochCurrent(int slot, long epoch)
        => _session.ReplaySlots.IsCurrentEpoch(slot, epoch);

    private void InvalidateReplayWriteEpoch(int slot)
    {
        _session.ReplaySlots.InvalidateWrites(slot);
        ClearPendingWeaponSlotReplacementsForSlot(slot);
    }

    private void ForgetLoadedReplayMetadata(int slot)
    {
        InvalidateInitialSpawnAssignment();
        RestoreReplayMusicKitForSlot(slot, "forget_replay");
        InvalidateReplayIdentityGeneration(slot);
        InvalidateReplayWriteEpoch(slot);
        _session.LoadedReplays.Remove(slot);
        _session.LastEnsuredWeaponDef.Remove(slot);
        _session.LastReplayWeaponDef.Remove(slot);
        _session.LastLockedWeaponTarget.Remove(slot);
        _session.ReplayHifiEventNextBySlot.Remove(slot);
        _session.RebuiltInventorySlots.Remove(slot);
        _session.BalanceSyncedSlots.Remove(slot);
        InvalidateReplayMusicKitRepair(slot);
        InvalidateLoadedReplayCosmeticAlignmentForSlot(slot);
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
        _session.ProjectileAlignNextBySlot[slot] = 0;
        _session.ReplayHifiEventNextBySlot[slot] = 0;
        _session.RebuiltInventorySlots.Remove(slot);
        _session.WeaponLoadoutSyncedSlots.Remove(slot);
        _session.PawnEquipmentSync.Invalidate(slot);
        _session.BalanceSyncedSlots.Remove(slot);
        InvalidateLoadedReplayCosmeticAlignmentForSlot(slot);
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
