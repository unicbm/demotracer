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
    private string PlayLoaded(bool loop)
    {
        PreloadLoadedReplays();
        return StartLoaded(loop);
    }

    private void PreloadLoadedReplays()
    {
        foreach (var slot in _session.LoadedSlots)
        {
            if (IsReplaySlotStillSafe(slot))
                _session.ReplaySlots.Claim(slot);
        }

        // Establish round-start positions before any later freeze-time replay
        // scheduling can leave partial-roster bots at native spawn points.
        ScheduleInitialRoundSpawnAssignment();
        _ = SyncBotHiderPresentationLease(announce: false);
        _ = SyncBotRandomizerCosmeticLease(announce: false);
        ApplyLoadedReplayMusicKits();
        ScheduleLoadedReplayMusicKitRepairs();

        if (_weaponAlignEnabled)
        {
            foreach (var slot in _session.LoadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_session.LoadedReplays.TryGetValue(slot, out var replay))
                {
                    ApplyReplayLoadoutForSlot(slot, replay);
                    PreloadReplayWeaponsForSlot(slot, replay);
                }
            }
        }

        // Replay identity cosmetics are mandatory even when every optional
        // positive-evidence component is disabled: missing agent/knife/glove
        // evidence means native/default, not Randomizer ownership.
        if (_session.LoadedReplays.Count > 0)
        {
            foreach (var slot in _session.LoadedSlots)
            {
                if (!IsReplaySlotStillSafe(slot))
                    continue;
                if (_session.LoadedReplays.TryGetValue(slot, out var replay))
                {
                    if (!TryAlignLoadedReplayCosmeticsForSlot(slot, replay))
                        QueueLoadedReplayCosmeticAlignmentForSlot(slot);
                }
            }
        }

        ApplyLoadedReplayScoreboards();
        AlignSafeC4OwnerForLoadedReplays();
        _ = SyncBotHiderPresentationLease(announce: false);
    }

    private void ApplyLoadedReplayMusicKits()
    {
        if (!_cosmeticAlignEnabled)
            return;

        foreach (var slot in _session.LoadedSlots)
        {
            if (!IsReplaySlotStillSafe(slot) ||
                !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
                replay.MusicKitId <= 0)
            {
                continue;
            }

            _ = ApplyReplayMusicKitForSlot(slot, replay.MusicKitId);
        }
    }

    private bool ApplyReplayMusicKitForSlot(int slot, int musicKitId)
    {
        if (!ReplayMusicKitAlignmentAllowed(musicKitId) ||
            !IsReplaySlotStillSafe(slot) ||
            !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replay.SteamId,
                DemoTracerCosmeticWriteField.MusicKit))
            return false;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
            return false;

        try
        {
            if (ReplayMusicKitStateMatches(player, musicKitId))
                return true;
            return ApplyReplayMusicKit(player, musicKitId, replay.SteamId);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: music kit apply failed slot={slot} kit={musicKitId}: {ex.Message}");
            return false;
        }
    }

    private void ScheduleLoadedReplayMusicKitRepairs()
    {
        if (!_cosmeticAlignEnabled)
            return;

        foreach (var slot in _session.LoadedSlots.ToArray())
            ScheduleReplayMusicKitRepairForSlot(slot);
    }

    private void ScheduleReplayMusicKitRepairForSlot(int slot)
    {
        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !ReplayMusicKitAlignmentAllowed(replay.MusicKitId))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } || player.UserId is not int userId)
            return;

        var generation = CurrentReplayIdentityGeneration(slot);
        var expectedMusicKitId = replay.MusicKitId;
        var repairToken = ++_session.NextReplayMusicKitRepairToken;
        _session.ReplayMusicKitRepairTokens[slot] = repairToken;

        void ReconcileWhileCurrent()
        {
            try
            {
                if (!_session.ReplayMusicKitRepairTokens.TryGetValue(slot, out var currentToken) ||
                    currentToken != repairToken ||
                    !IsReplayIdentityGenerationCurrent(slot, generation) ||
                    !_session.LoadedReplays.TryGetValue(slot, out var current) ||
                    current.MusicKitId != expectedMusicKitId ||
                    Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } currentPlayer ||
                    currentPlayer.UserId != userId)
                {
                    return;
                }

                _ = ApplyReplayMusicKitForSlot(slot, expectedMusicKitId);
            }
            finally
            {
                if (_session.ReplayMusicKitRepairTokens.TryGetValue(slot, out var currentToken) &&
                    currentToken == repairToken)
                {
                    _session.ReplayMusicKitRepairTokens.Remove(slot);
                }
            }
        }

        // The cosmetic writer lease is established in the spawn callback. One
        // coalesced next-frame reconciliation runs after all spawn handlers;
        // ownership must prevent later writers from fighting this state.
        Server.NextFrame(ReconcileWhileCurrent);
    }

    private bool ApplyReplayMusicKit(
        CCSPlayerController player,
        int musicKitId,
        ulong replaySteamId)
    {
        if (!ReplayMusicKitAlignmentAllowed(musicKitId) ||
            player is not { IsValid: true } ||
            !TryValidateBotRandomizerClaim(
                player.Slot,
                replaySteamId,
                DemoTracerCosmeticWriteField.MusicKit) ||
            musicKitId is > ushort.MaxValue)
            return false;

        var inventory = player.InventoryServices;
        if (inventory is null || !CaptureReplayMusicKitBaseline(player, inventory))
            return false;

        inventory.MusicID = (ushort)musicKitId;
        TrySetReplayMusicKitStateChanged(
            player,
            "CCSPlayerController",
            "m_pInventoryServices");

        player.MusicKitID = musicKitId;
        TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
        player.MusicKitMVPs = 0;
        TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
        player.MvpNoMusic = false;
        TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");

        return ReplayMusicKitStateMatches(player, musicKitId);
    }

    private bool CaptureReplayMusicKitBaseline(
        CCSPlayerController player,
        CCSPlayerController_InventoryServices inventory)
    {
        var slot = player.Slot;
        if (player.UserId is not int userId ||
            !_session.ReplayIdentityGenerationBySlot.TryGetValue(slot, out var generation))
            return false;

        if (_session.ReplayMusicKitBaselines.TryGetValue(slot, out var existing))
        {
            if (existing.Generation == generation && existing.UserId == userId)
                return true;
            _session.ReplayMusicKitBaselines.Remove(slot);
        }

        _session.ReplayMusicKitBaselines[slot] = new ReplayMusicKitBaseline(
            generation,
            userId,
            inventory.MusicID,
            player.MusicKitID,
            player.MusicKitMVPs,
            player.MvpNoMusic);
        return true;
    }

    private void RestoreReplayMusicKitForSlot(int slot, string reason)
    {
        InvalidateReplayMusicKitRepair(slot);
        if (!ManagedSchemaWritesAllowed())
        {
            _session.ReplayMusicKitBaselines.Remove(slot);
            return;
        }

        if (!_session.ReplayMusicKitBaselines.TryGetValue(slot, out var baseline))
            return;
        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !TryValidateBotRandomizerClaim(
                slot,
                replay.SteamId,
                DemoTracerCosmeticWriteField.MusicKit))
        {
            _session.ReplayMusicKitBaselines.Remove(slot);
            return;
        }

        if (!IsReplayIdentityGenerationCurrent(slot, baseline.Generation) ||
            !IsReplaySlotStillSafe(slot))
        {
            _session.ReplayMusicKitBaselines.Remove(slot);
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.UserId != baseline.UserId)
        {
            _session.ReplayMusicKitBaselines.Remove(slot);
            return;
        }
        var inventory = player.InventoryServices;
        if (inventory is null)
        {
            _session.ReplayMusicKitBaselines.Remove(slot);
            return;
        }

        try
        {
            inventory.MusicID = baseline.InventoryMusicKitId;
            TrySetReplayMusicKitStateChanged(
                player,
                "CCSPlayerController",
                "m_pInventoryServices");

            player.MusicKitID = baseline.ControllerMusicKitId;
            TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
            player.MusicKitMVPs = baseline.ControllerMusicKitMvps;
            TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
            player.MvpNoMusic = baseline.MvpNoMusic;
            TrySetReplayMusicKitStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"dtr: music kit restore failed slot={slot} reason={reason}: {ex.Message}");
        }
        finally
        {
            _session.ReplayMusicKitBaselines.Remove(slot);
        }
    }

    private void RestoreAllReplayMusicKits(string reason)
    {
        foreach (var slot in _session.ReplayMusicKitBaselines.Keys.ToArray())
            RestoreReplayMusicKitForSlot(slot, reason);
        _session.ReplayMusicKitBaselines.Clear();
        _session.ReplayMusicKitRepairTokens.Clear();
    }

    private void InvalidateReplayMusicKitRepair(int slot)
        => _session.ReplayMusicKitRepairTokens.Remove(slot);

    private static bool ReplayMusicKitStateMatches(CCSPlayerController player, int expectedMusicKitId)
    {
        var inventory = player.InventoryServices;
        return ReplayRuntimePolicy.MusicKitStateMatches(
            expectedMusicKitId,
            inventory is null ? null : inventory.MusicID,
            player.MusicKitID,
            player.MusicKitMVPs,
            player.MvpNoMusic);
    }

    private bool ReplayMusicKitAlignmentAllowed(int musicKitId)
        => ReplayRuntimePolicy.ShouldApplyMusicKit(
            _cosmeticAlignEnabled,
            ManagedSchemaWritesAllowed(),
            musicKitId);

    private static bool ManagedSchemaWritesAllowed()
        => ManagedSchemaRuntime.Value.Allowed;

    private static (bool Allowed, string Patch) DetectManagedSchemaRuntime()
    {
        try
        {
            var steamInfPath = Path.Combine(Server.GameDirectory, "steam.inf");
            var patchLine = File.ReadLines(steamInfPath)
                .FirstOrDefault(line => line.StartsWith("PatchVersion=", StringComparison.OrdinalIgnoreCase));
            var patch = patchLine?["PatchVersion=".Length..].Trim() ?? "unknown";
            return ReplayRuntimePolicy.IsManagedSchemaPatchSupported(patch)
                ? (true, patch)
                : (false, patch);
        }
        catch
        {
            return (false, "unknown");
        }
    }

    private static void TrySetReplayMusicKitStateChanged(
        CBaseEntity entity,
        string className,
        string fieldName)
    {
        try
        {
            // Current CSS exposes these controller values for direct server-side
            // reads/writes but does not network every field. Calling
            // SetStateChanged for a non-networked field only emits warnings and
            // duplicates BotRandomizer's presentation traffic.
            if (!Schema.IsSchemaFieldNetworked(className, fieldName))
                return;
            Utilities.SetStateChanged(entity, className, fieldName);
        }
        catch
        {
            // Presentation metadata is best-effort; the MVP event still carries
            // the demo-backed kit even when a field cannot be network-dirtied.
        }
    }

    private int NormalizeMusicKitId(uint? musicKitId)
        => musicKitId is > 0 and <= int.MaxValue && IsKnownMusicKitId((int)musicKitId.Value)
            ? (int)musicKitId.Value
            : 0;

    private int NormalizeMusicKitId(int musicKitId)
        => IsKnownMusicKitId(musicKitId) ? musicKitId : 0;

    private void AlignSafeC4OwnerForLoadedReplays(bool forceReconcile = false)
    {
        if (_session.SafeC4Aligned && !forceReconcile)
            return;

        var plantedOwner = FindLoadedC4Owner(IsBombPlantedEvent);
        var initialOwner = FindLoadedC4Owner(IsBombInitialOwnerEvent);
        var targetOwner = plantedOwner ?? initialOwner;

        if (!targetOwner.HasValue)
            return;

        var targetSlot = targetOwner.Value.Slot;
        var targetSteamId = targetOwner.Value.SteamId;
        if (targetSlot < 0 || !CanWriteReplaySlot(targetSlot))
            return;
        var firstAlignment = !_session.SafeC4Aligned;

        // CS2 may assign its native C4 to another live T, including a human who
        // joins during freeze time. Demo evidence makes this replay slot the
        // authoritative owner, so purge every other player rather than only bots.
        foreach (var candidate in FindTeamPlayers())
        {
            if (candidate.Slot == targetSlot ||
                (_session.LoadedReplays.ContainsKey(candidate.Slot) &&
                 !_session.ReplaySlots.IsOwned(candidate.Slot)))
            {
                continue;
            }
            RemoveC4FromPlayer(candidate, "safe_c4_owner_align");
        }

        var player = Utilities.GetPlayerFromSlot(targetSlot);
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;
        if (CountCurrentReplayItems(player, "weapon_c4") <= 0 &&
            !TryGiveNamedItem(player, "weapon_c4"))
        {
            Server.PrintToConsole(
                $"dtr: C4 safe owner align failed slot={targetSlot} steam_id={targetSteamId}");
            return;
        }

        _session.SafeC4Aligned = true;
        if (!firstAlignment)
            return;

        if (plantedOwner.HasValue &&
            initialOwner.HasValue &&
            plantedOwner.Value.SteamId != initialOwner.Value.SteamId)
        {
            Server.PrintToConsole(
                "dtr: C4 safe owner collapsed to planter " +
                $"slot={targetSlot} steam_id={targetSteamId} initial_steam_id={initialOwner.Value.SteamId}");
            return;
        }

        var source = plantedOwner.HasValue ? "bomb_planted" : "bomb_initial_owner";
        Server.PrintToConsole(
            $"dtr: C4 safe owner aligned slot={targetSlot} steam_id={targetSteamId} source={source}");
    }

    private static bool IsBombInitialOwnerEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_initial_owner", StringComparison.OrdinalIgnoreCase);

    private static bool IsBombPlantedEvent(ReplayHifiEvent replayEvent)
        => replayEvent.Kind.Trim().Equals("bomb_planted", StringComparison.OrdinalIgnoreCase);

    private (int Slot, ulong SteamId)? FindLoadedC4Owner(Func<ReplayHifiEvent, bool> predicate)
    {
        foreach (var slot in _session.LoadedSlots)
        {
            if (!CanWriteReplaySlot(slot) ||
                !_session.LoadedReplays.TryGetValue(slot, out var replay))
                continue;

            var replayEvent = replay.HifiEvents.FirstOrDefault(predicate);
            if (replayEvent is null)
                continue;

            var steamId = replayEvent.ActorSteamId.GetValueOrDefault(replay.SteamId);
            return (slot, steamId);
        }

        return null;
    }

    private void RemoveC4FromPlayer(CCSPlayerController player, string reason)
    {
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return;

        var pawn = player.PlayerPawn.Value;
        foreach (var weapon in GetReplayWeaponsByClass(pawn, "weapon_c4").ToArray())
            RemoveAndKillReplayWeapon(player, pawn, weapon, reason);
    }
}
