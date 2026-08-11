/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const int ReplayCosmeticAlignmentAttemptLimit = 4;

    private bool TryAlignLoadedReplayCosmeticsForSlot(int slot, LoadedReplay replay)
    {
        if (!CanWriteReplaySlot(slot))
            return false;

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId is not int userId ||
            pawn is not { IsValid: true })
        {
            return false;
        }

        var identity = new ReplayCosmeticPawnIdentity(
            userId,
            pawn.Handle,
            CurrentReplayIdentityGeneration(slot),
            replay.SteamId);
        if (_cosmeticAlignmentTracker.IsAligned(slot, identity))
            return true;

        if (!_botRandomizerLease.TryGet(slot, replay.SteamId, out _))
            return false;

        var applied = 0;
        var skipped = 0;
        if (replay.Cosmetics.Agent is { } agentCosmetic)
        {
            if (TryApplyAgentCosmetic(player, pawn, agentCosmetic, replay.SteamId))
                applied++;
            else
                skipped++;
        }
        else if (ShouldClaimAgentOwnership(_cosmeticAlignEnabled, _cosmeticAgentsEnabled))
        {
            if (TryRestoreNativeAgentModel(player, pawn, replay.SteamId))
                applied++;
            else
                skipped++;
        }

        if (_weaponAlignEnabled && WeaponCosmeticFeatureEnabled())
        {
            foreach (var cosmetic in replay.Cosmetics.Weapons)
            {
                if (TryFindReplayWeaponByDefIndex(pawn, cosmetic.WeaponDefIndex, out var weapon) &&
                    TryApplyWeaponCosmetic(player, weapon, cosmetic, replay.SteamId))
                {
                    applied++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        // Knife cosmetics are applied in place. Never rebuild a knife by
        // dropping it first: an asynchronous replacement failure leaves the
        // bot without slot 3 and corrupts every later weapon-switch replay.
        if (replay.Cosmetics.Knife is { } knifeCosmetic)
        {
            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyKnifeCosmetic(player, knife, knifeCosmetic, replay.SteamId))
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }
        if (replay.Cosmetics.Glove is { } gloveCosmetic)
        {
            if (TryApplyGloveCosmetic(player, pawn, gloveCosmetic, replay.SteamId, out var changed))
            {
                if (changed)
                    applied++;
            }
            else
                skipped++;
        }

        _cosmeticAppliedCount += applied;
        _cosmeticSkippedCount += skipped;
        if (!_cosmeticAlignmentTracker.TryMarkAligned(slot, identity, skipped))
            return false;

        _session.CosmeticSyncedSlots.Add(slot);
        if (applied > 0)
        {
            Server.PrintToConsole(
                $"dtr: cosmetic aligned slot={slot} player={replay.PlayerName} applied={applied} skipped={skipped}");
        }
        return true;
    }

    private void QueueLoadedReplayCosmeticAlignmentForSlot(
        int slot,
        int attemptsRemaining = ReplayCosmeticAlignmentAttemptLimit)
    {
        if (attemptsRemaining <= 0 ||
            !CanWriteReplaySlot(slot) ||
            !_session.LoadedReplays.ContainsKey(slot))
            return;

        var token = _cosmeticAlignmentTracker.Queue(slot);
        ScheduleCosmeticNextFrame(() =>
        {
            if (!_cosmeticAlignmentTracker.TryConsume(slot, token))
                return;
            if (_session.LoadedReplays.TryGetValue(slot, out var replay) &&
                !TryAlignLoadedReplayCosmeticsForSlot(slot, replay) &&
                attemptsRemaining > 1)
            {
                QueueLoadedReplayCosmeticAlignmentForSlot(slot, attemptsRemaining - 1);
            }
        });
    }

    private void InvalidateLoadedReplayCosmeticAlignmentForSlot(int slot)
    {
        _cosmeticAlignmentTracker.Invalidate(slot);
        foreach (var key in _appliedWeaponCosmeticWrites.Keys.Where(key => key.Slot == slot).ToArray())
            _appliedWeaponCosmeticWrites.Remove(key);
        foreach (var key in _appliedKnifeCosmeticWrites.Keys.Where(key => key.Slot == slot).ToArray())
            _appliedKnifeCosmeticWrites.Remove(key);
        _session.CosmeticSyncedSlots.Remove(slot);
        _session.ActiveWeaponCosmetics.Remove(slot);
        _appliedGloveCosmetics.Remove(slot);
        _gloveCosmeticTokens.Remove(slot);
    }

    private bool HasCurrentLoadedReplayCosmeticAlignment(int slot, LoadedReplay replay)
    {
        if (!_session.CosmeticSyncedSlots.Contains(slot) ||
            !_session.ReplayIdentityGenerationBySlot.TryGetValue(slot, out var generation))
        {
            return false;
        }

        // During a human takeover the original bot controller may no longer
        // expose the pawn through PlayerPawn, but this is still the same live
        // cosmetic alignment until spawn/identity invalidation clears it.
        return _cosmeticAlignmentTracker.HasReplayAlignment(
            slot,
            generation,
            replay.SteamId);
    }

    private bool TryGetWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        out ReplayWeaponCosmetic cosmetic,
        out ulong replaySteamId)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (_session.LoadedReplays.TryGetValue(slot, out var replay) &&
            TryFindReplayWeaponCosmetic(replay, normalized, out cosmetic))
        {
            replaySteamId = replay.SteamId;
            return true;
        }

        replaySteamId = 0;
        cosmetic = null!;
        return false;
    }

    private void ApplyReplayWeaponCosmeticForSlot(int slot, int weaponDefIndex)
    {
        _ = TryApplyReplayWeaponCosmeticForSlot(
            slot,
            weaponDefIndex,
            activeOnly: false,
            countResult: true);
    }

    private void ApplyActiveReplayWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        TickPlayerSnapshot? playerSnapshot = null)
    {
        _ = TryApplyReplayWeaponCosmeticForSlot(
            slot,
            weaponDefIndex,
            activeOnly: true,
            countResult: false,
            playerSnapshot: playerSnapshot);
    }

    private bool TryApplyReplayWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        bool activeOnly,
        bool countResult,
        TickPlayerSnapshot? playerSnapshot = null)
    {
        if (!CanWriteReplaySlot(slot) ||
            !WeaponCosmeticFeatureEnabled() ||
            !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !HasCosmeticEvidence(replay.Cosmetics))
        {
            return false;
        }
        if (playerSnapshot != null)
        {
            if (!IsReplaySlotStillSafe(slot, playerSnapshot))
                return false;
        }
        else if (!IsReplaySlotStillSafe(slot))
        {
            return false;
        }

        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        var cosmetic = replay.Cosmetics.Weapons
            .FirstOrDefault(weapon => weapon.WeaponDefIndex == normalized);
        if (cosmetic == null)
            return false;

        CCSPlayerController? player;
        if (playerSnapshot != null)
        {
            if (!playerSnapshot.TryGetSlot(slot, out var snapshotPlayer))
                return false;
            player = snapshotPlayer;
        }
        else
        {
            player = Utilities.GetPlayerFromSlot(slot);
        }
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
            return false;

        var isActiveWeapon = TryFindActiveReplayWeaponByDefIndex(pawn, normalized, out var weapon);
        if (!isActiveWeapon && activeOnly)
            return false;
        if (!isActiveWeapon && !TryFindReplayWeaponByDefIndex(pawn, normalized, out weapon))
            return false;

        var weaponHandle = weapon.Handle;
        if (isActiveWeapon &&
            _session.ActiveWeaponCosmetics.TryGetValue(slot, out var applied) &&
            applied.WeaponDefIndex == normalized &&
            applied.WeaponHandle == weaponHandle &&
            (!HasActiveBotRandomizerClaim(
                 slot,
                 replay.SteamId,
                 DemoTracerCosmeticWriteField.WeaponPaint,
                 normalized) ||
             HasExpectedWeaponPaintState(weapon, cosmetic)))
        {
            return false;
        }

        var ok = TryApplyWeaponCosmetic(player, weapon, cosmetic, replay.SteamId, countStickerStats: countResult);
        if (ok)
        {
            if (isActiveWeapon)
                _session.ActiveWeaponCosmetics[slot] = new AppliedActiveWeaponCosmetic(normalized, weaponHandle);
            if (countResult)
                _cosmeticAppliedCount++;
            return true;
        }

        if (countResult)
            _cosmeticSkippedCount++;
        return false;
    }

    private HookResult OnGiveNamedItemPostForCosmetics(DynamicHook hook)
    {
        try
        {
            if (_session.LoadedReplays.Count == 0)
                return HookResult.Continue;

            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var weapon = hook.GetReturn<CBasePlayerWeapon>();
            if (weapon == null || !weapon.IsValid)
                return HookResult.Continue;

            if (!TryFindReplayPlayerByItemServices(itemServices, out var slot, out _))
                return HookResult.Continue;

            var weaponEntityHandle = weapon.EntityHandle.Raw;
            if (weaponEntityHandle != Utilities.InvalidEHandleIndex)
            {
                ScheduleGivenWeaponCosmeticNextFrame(
                    slot,
                    weaponEntityHandle,
                    CurrentReplayWriteEpoch(slot),
                    countResult: true);
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: cosmetic GiveNamedItem post failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private void ScheduleGivenWeaponCosmeticNextFrame(
        int slot,
        uint weaponEntityHandle,
        long writeEpoch,
        bool countResult)
    {
        ScheduleCosmeticNextFrame(() =>
        {
            if (!IsReplayWriteEpochCurrent(slot, writeEpoch) ||
                !TryResolveOwnedReplayWeapon(slot, weaponEntityHandle, out var player, out var weapon))
            {
                return;
            }

            TryApplyGivenWeaponCosmetic(
                slot,
                player,
                weapon,
                countResult);
        });
    }

    private bool TryResolveOwnedReplayWeapon(
        int slot,
        uint weaponEntityHandle,
        out CCSPlayerController player,
        out CBasePlayerWeapon weapon)
    {
        player = null!;
        weapon = null!;
        if (!CanWriteReplaySlot(slot))
            return false;

        var candidatePlayer = Utilities.GetPlayerFromSlot(slot);
        var pawn = candidatePlayer?.PlayerPawn.Value;
        if (candidatePlayer is not { IsValid: true, PawnIsAlive: true } ||
            pawn is not { IsValid: true })
        {
            return false;
        }

        var candidateWeapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
        if (candidateWeapon is not { IsValid: true } || !PawnOwnsWeapon(pawn, candidateWeapon))
            return false;

        player = candidatePlayer;
        weapon = candidateWeapon;
        return true;
    }

    private bool TryFindReplayPlayerByItemServices(
        CCSPlayer_ItemServices itemServices,
        out int slot,
        out CCSPlayerController player)
    {
        slot = -1;
        player = null!;
        if (itemServices == null || itemServices.Handle == IntPtr.Zero)
            return false;

        var candidates = _session.LoadedSlots
            .Select(slot => Utilities.GetPlayerFromSlot(slot))
            .Where(candidate => candidate is { IsValid: true })
            .Cast<CCSPlayerController>()
            .GroupBy(candidate => candidate.Slot)
            .Select(group => group.First())
            .ToList();

        foreach (var candidate in candidates)
        {
            var candidateSlot = candidate.Slot;
            if (!CanWriteReplaySlot(candidateSlot))
                continue;

            var pawn = candidate?.PlayerPawn.Value;
            if (candidate is not { IsValid: true, PawnIsAlive: true } ||
                pawn is not { IsValid: true } ||
                pawn.ItemServices == null ||
                pawn.ItemServices.Handle != itemServices.Handle)
            {
                continue;
            }

            slot = candidateSlot;
            player = candidate;
            return true;
        }

        return false;
    }

    private bool TryApplyGivenWeaponCosmetic(
        int slot,
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        bool countResult)
    {
        if (!CanWriteReplaySlot(slot))
        {
            return false;
        }

        var weaponDefIndex = WeaponDefIndex(weapon);
        if (IsKnifeCosmeticDefIndex(weaponDefIndex))
        {
            if (!_session.LoadedReplays.TryGetValue(slot, out var replay) ||
                replay.Cosmetics.Knife is not { } desiredKnife ||
                !HasActiveBotRandomizerClaim(
                    slot,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife))
            {
                return false;
            }

            var knifeOk = TryApplyKnifeCosmetic(player, weapon, desiredKnife, replay.SteamId);
            if (countResult)
            {
                if (knifeOk)
                    _cosmeticAppliedCount++;
                else
                    _cosmeticSkippedCount++;
            }
            return knifeOk;
        }

        var normalizedWeaponDefIndex = NormalizeWeaponDefIndex(weaponDefIndex);
        if (!IsWeaponCosmeticDefIndex(normalizedWeaponDefIndex))
            return false;
        if (!IsReplaySlotPlaying(slot))
            return false;

        if (!TryGetWeaponCosmeticForSlot(slot, normalizedWeaponDefIndex, out var cosmetic, out var replaySteamId))
        {
            return false;
        }

        var ok = TryApplyWeaponCosmetic(player, weapon, cosmetic, replaySteamId);
        if (countResult)
        {
            if (ok)
                _cosmeticAppliedCount++;
            else
                _cosmeticSkippedCount++;
        }
        return ok;
    }

    private void TryApplySpawnedReplayWeaponCosmetic(CEntityInstance entity)
    {
        if (_session.LoadedReplays.Count == 0)
            return;
        var name = entity.DesignerName;
        if (string.IsNullOrWhiteSpace(name) ||
            !name.Contains("weapon", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var weaponEntityHandle = entity.EntityHandle.Raw;
        if (weaponEntityHandle == Utilities.InvalidEHandleIndex)
            return;
        var writeEpochs = _session.LoadedSlots
            .Distinct()
            .ToDictionary(slot => slot, CurrentReplayWriteEpoch);

        ScheduleCosmeticNextFrame(() =>
        {
            if (_session.LoadedReplays.Count == 0)
                return;

            var weapon = new CHandle<CBasePlayerWeapon>(weaponEntityHandle).Value;
            if (weapon is not { IsValid: true })
                return;

            var weaponDefIndex = WeaponDefIndex(weapon);
            var normalizedWeaponDefIndex = NormalizeWeaponDefIndex(weaponDefIndex);
            var isReplayWeaponCosmetic = IsWeaponCosmeticDefIndex(normalizedWeaponDefIndex);
            var isReplayKnifeCosmetic = IsKnifeCosmeticDefIndex(weaponDefIndex);
            if (!isReplayWeaponCosmetic && !isReplayKnifeCosmetic)
                return;

            var candidates = _session.LoadedSlots
                .Select(slot => Utilities.GetPlayerFromSlot(slot))
                .Where(candidate => candidate is { IsValid: true })
                .Cast<CCSPlayerController>()
                .GroupBy(candidate => candidate.Slot)
                .Select(group => group.First())
                .ToList();

            foreach (var player in candidates)
            {
                var slot = player.Slot;
                if (!writeEpochs.TryGetValue(slot, out var writeEpoch) ||
                    !IsReplayWriteEpochCurrent(slot, writeEpoch) ||
                    !IsReplaySlotStillSafe(slot))
                {
                    continue;
                }

                var pawn = player?.PlayerPawn.Value;
                if (player is not { IsValid: true, PawnIsAlive: true } ||
                    pawn is not { IsValid: true } ||
                    !PawnOwnsWeapon(pawn, weapon))
                {
                    continue;
                }

                var attempted = false;
                var applied = false;
                ReplayItemCosmetic? knifeCosmetic = null;
                ReplayWeaponCosmetic? weaponCosmetic = null;
                ulong replaySteamId = 0;
                if (isReplayKnifeCosmetic)
                {
                    if (_session.LoadedReplays.TryGetValue(slot, out var replay) &&
                        replay.Cosmetics.Knife is { } replayKnifeCosmetic &&
                        HasActiveBotRandomizerClaim(
                            slot,
                            replay.SteamId,
                            DemoTracerCosmeticWriteField.Knife))
                    {
                        replaySteamId = replay.SteamId;
                        knifeCosmetic = replayKnifeCosmetic;
                        attempted = true;
                        applied = TryApplyKnifeCosmetic(player, weapon, knifeCosmetic, replaySteamId);
                    }
                }
                else if (IsReplaySlotPlaying(slot) &&
                         TryGetWeaponCosmeticForSlot(slot, normalizedWeaponDefIndex, out weaponCosmetic, out replaySteamId))
                {
                    attempted = true;
                    applied = TryApplyWeaponCosmetic(player, weapon, weaponCosmetic, replaySteamId);
                }

                if (!attempted)
                    continue;

                if (applied)
                    _cosmeticAppliedCount++;
                else
                {
                    _cosmeticSkippedCount++;
                }
                return;
            }
        });
    }

    private static bool TryFindReplayWeaponCosmetic(
        LoadedReplay replay,
        int weaponDefIndex,
        out ReplayWeaponCosmetic cosmetic)
    {
        cosmetic = replay.Cosmetics.Weapons
            .FirstOrDefault(candidate => candidate.WeaponDefIndex == weaponDefIndex)!;
        return cosmetic != null;
    }

    private static bool PawnOwnsWeapon(CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            if (candidate.Handle == weapon.Handle)
                return true;
        }

        return false;
    }

    private bool TryFindReplayWeaponByDefIndex(
        CCSPlayerPawn pawn,
        int weaponDefIndex,
        out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className) ||
            pawn.WeaponServices == null)
        {
            return false;
        }

        if (TryFindActiveReplayWeaponByDefIndex(pawn, weaponDefIndex, out weapon))
            return true;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            if (WeaponClassMatches(candidate.DesignerName, className) ||
                WeaponDefIndex(candidate) == weaponDefIndex)
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryFindActiveReplayWeaponByDefIndex(
        CCSPlayerPawn pawn,
        int weaponDefIndex,
        out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className) ||
            pawn.WeaponServices == null)
        {
            return false;
        }

        var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
            return false;

        if (WeaponClassMatches(activeWeapon.DesignerName, className) ||
            WeaponDefIndex(activeWeapon) == NormalizeWeaponDefIndex(weaponDefIndex))
        {
            weapon = activeWeapon;
            return true;
        }

        return false;
    }

    private int WeaponDefIndex(CBasePlayerWeapon weapon)
    {
        var designerDef = WeaponDefIndex(weapon.DesignerName);
        try
        {
            var rawItemDef = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (IsExactKnifeCosmeticDefIndex(rawItemDef))
                return rawItemDef;
            if (IsExactKnifeCosmeticDefIndex(designerDef))
                return designerDef;

            var itemDef = NormalizeWeaponDefIndex(rawItemDef);
            if (IsKnownWeaponDefIndex(itemDef))
                return itemDef;
        }
        catch
        {
        }

        return designerDef;
    }

    private static bool TryFindReplayKnife(CCSPlayerPawn pawn, out CBasePlayerWeapon weapon)
    {
        weapon = null!;
        if (pawn.WeaponServices == null)
            return false;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;
            var name = candidate.DesignerName;
            if (name.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
            {
                weapon = candidate;
                return true;
            }
        }

        return false;
    }

}
