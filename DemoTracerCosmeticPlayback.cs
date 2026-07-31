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
    private void ApplyLoadedReplayCosmeticsForSlot(int slot, LoadedReplay replay)
    {
        if (!IsReplaySlotStillSafe(slot))
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
            return;
        if (!_botRandomizerLease.TryGet(slot, replay.SteamId, out _))
        {
            return;
        }

        var applied = 0;
        var skipped = 0;
        if (replay.Cosmetics.Agent is { } agentCosmetic)
        {
            if (TryApplyAgentCosmetic(player, pawn, agentCosmetic, replay.SteamId))
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
                    ScheduleReplayWeaponCosmeticRetry(slot, cosmetic, framesRemaining: 3);
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
            var appliedKnife = false;
            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(
                    player,
                    knife,
                    knifeCosmetic,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled))
            {
                applied++;
                appliedKnife = true;
            }
            else
            {
                skipped++;
            }
            if (_session.LoadedReplays.ContainsKey(slot))
                ScheduleReplayKnifeCosmeticRetry(slot, knifeCosmetic, framesRemaining: appliedKnife ? 2 : 4);
            else
                ScheduleKnifeCosmeticRetry(slot, knifeCosmetic, replay.SteamId, framesRemaining: appliedKnife ? 2 : 4);
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
        if (applied > 0)
        {
            _session.CosmeticSyncedSlots.Add(slot);
            Server.PrintToConsole(
                $"dtr: cosmetic aligned slot={slot} player={replay.PlayerName} applied={applied} skipped={skipped}");
        }

        if (WeaponCosmeticFeatureEnabled() && replay.Cosmetics.Weapons.Count > 0)
            ScheduleReplayCosmeticHeartbeat(slot);
    }

    private void ScheduleReplayCosmeticHeartbeat(int slot)
    {
        var token = ++_nextCosmeticHeartbeatToken;
        _cosmeticHeartbeatTokens[slot] = token;
        AddTimer(
            CosmeticHeartbeatIntervalSeconds,
            () => RunReplayCosmeticHeartbeat(slot, token, CosmeticHeartbeatAttempts),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RunReplayCosmeticHeartbeat(int slot, int token, int attemptsRemaining)
    {
        if (attemptsRemaining <= 0 ||
            !_cosmeticHeartbeatTokens.TryGetValue(slot, out var activeToken) ||
            activeToken != token)
        {
            return;
        }

        if (!WeaponCosmeticFeatureEnabled() ||
            !_weaponAlignEnabled ||
            !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            !HasCosmeticEvidence(replay.Cosmetics) ||
            !IsReplaySlotStillSafe(slot))
        {
            _cosmeticHeartbeatTokens.Remove(slot);
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player is { IsValid: true, PawnIsAlive: true } && pawn is { IsValid: true })
        {
            var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value;
            if (activeWeapon is { IsValid: true })
                ApplyActiveReplayWeaponCosmeticForSlot(
                    slot,
                    WeaponDefIndex(activeWeapon),
                    force: false,
                    scheduleNextFrame: true);
        }

        if (attemptsRemaining == 1)
        {
            _cosmeticHeartbeatTokens.Remove(slot);
            return;
        }

        AddTimer(
            CosmeticHeartbeatIntervalSeconds,
            () => RunReplayCosmeticHeartbeat(slot, token, attemptsRemaining - 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ScheduleReplayWeaponCosmeticRetry(
        int slot,
        ReplayWeaponCosmetic cosmetic,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!WeaponCosmeticFeatureEnabled() || !_weaponAlignEnabled || !IsReplaySlotStillSafe(slot))
                return;

            var refreshedPlayer = Utilities.GetPlayerFromSlot(slot);
            var refreshedPawn = refreshedPlayer?.PlayerPawn.Value;
            if (refreshedPlayer is not { IsValid: true, PawnIsAlive: true } ||
                refreshedPawn is not { IsValid: true })
            {
                return;
            }

            ApplyActiveReplayWeaponCosmeticForSlot(
                slot,
                cosmetic.WeaponDefIndex,
                force: false,
                scheduleNextFrame: true);

            ScheduleReplayWeaponCosmeticRetry(slot, cosmetic, framesRemaining - 1);
        });
    }

    private void ScheduleReplayKnifeCosmeticRetry(
        int slot,
        ReplayItemCosmetic cosmetic,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!IsReplaySlotStillSafe(slot) ||
                !_session.LoadedReplays.TryGetValue(slot, out var replay))
            {
                return;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(
                    player,
                    knife,
                    cosmetic,
                    replay.SteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled))
            {
                if (framesRemaining <= 2)
                    return;
            }

            ScheduleReplayKnifeCosmeticRetry(slot, cosmetic, framesRemaining - 1);
        });
    }

    private void ScheduleKnifeCosmeticRetry(
        int slot,
        ReplayItemCosmetic cosmetic,
        ulong replaySteamId,
        int framesRemaining)
    {
        if (framesRemaining <= 0)
            return;

        ScheduleCosmeticNextFrame(() =>
        {
            if (!IsReplaySlotStillSafe(slot))
                return;

            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player is not { IsValid: true, PawnIsAlive: true } || pawn is not { IsValid: true })
                return;

            if (TryFindReplayKnife(pawn, out var knife) &&
                TryApplyItemCosmetic(
                    player,
                    knife,
                    cosmetic,
                    replaySteamId,
                    DemoTracerCosmeticWriteField.Knife,
                    allowSubclassChange: true,
                    applyPaint: true,
                    applyCustomName: _cosmeticNamesEnabled))
            {
                if (framesRemaining <= 2)
                    return;
            }

            ScheduleKnifeCosmeticRetry(slot, cosmetic, replaySteamId, framesRemaining - 1);
        });
    }

    private void ScheduleLoadedReplayCosmeticRepairForSlot(int slot)
    {
        if (!_session.LoadedReplays.ContainsKey(slot))
            return;

        AddTimer(0.05f, () => ApplyLoadedReplayCosmeticRepairForSlot(slot), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(0.20f, () => ApplyLoadedReplayCosmeticRepairForSlot(slot), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ApplyLoadedReplayCosmeticRepairForSlot(int slot)
    {
        if (_session.LoadedReplays.TryGetValue(slot, out var replay))
            ApplyLoadedReplayCosmeticsForSlot(slot, replay);
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
            forceActive: false,
            countResult: true);
    }

    private void ApplyActiveReplayWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        bool force,
        bool scheduleNextFrame,
        TickPlayerSnapshot? playerSnapshot = null)
    {
        if (TryApplyReplayWeaponCosmeticForSlot(
                slot,
                weaponDefIndex,
                activeOnly: true,
                forceActive: force,
                countResult: false,
                playerSnapshot: playerSnapshot) &&
            scheduleNextFrame)
        {
            ScheduleActiveReplayWeaponCosmeticNextFrame(slot, NormalizeWeaponDefIndex(weaponDefIndex));
        }
    }

    private void ScheduleActiveReplayWeaponCosmeticNextFrame(int slot, int weaponDefIndex)
    {
        ScheduleCosmeticNextFrame(() =>
            ApplyActiveReplayWeaponCosmeticForSlot(
                slot,
                weaponDefIndex,
                force: true,
                scheduleNextFrame: false));
    }

    private bool TryApplyReplayWeaponCosmeticForSlot(
        int slot,
        int weaponDefIndex,
        bool activeOnly,
        bool forceActive,
        bool countResult,
        TickPlayerSnapshot? playerSnapshot = null)
    {
        if (!WeaponCosmeticFeatureEnabled() ||
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
            !forceActive &&
            _session.ActiveWeaponCosmetics.TryGetValue(slot, out var applied) &&
            applied.WeaponDefIndex == normalized &&
            applied.WeaponHandle == weaponHandle)
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
        bool countResult)
    {
        ScheduleCosmeticNextFrame(() =>
        {
            if (!TryResolveOwnedReplayWeapon(slot, weaponEntityHandle, out var player, out var weapon))
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
        if (!IsReplaySlotStillSafe(slot))
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
            if (!IsReplaySlotStillSafe(candidateSlot))
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
        if (!IsReplaySlotStillSafe(slot))
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

            var knifeOk = TryApplyItemCosmetic(
                player,
                weapon,
                desiredKnife,
                replay.SteamId,
                DemoTracerCosmeticWriteField.Knife,
                allowSubclassChange: true,
                applyPaint: true,
                applyCustomName: _cosmeticNamesEnabled);
            ScheduleKnifeCosmeticRetry(
                slot,
                desiredKnife,
                replay.SteamId,
                framesRemaining: knifeOk ? 3 : 8);
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
                if (!IsReplaySlotStillSafe(slot))
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
                        applied = TryApplyItemCosmetic(
                            player,
                            weapon,
                            knifeCosmetic,
                            replaySteamId,
                            DemoTracerCosmeticWriteField.Knife,
                            allowSubclassChange: true,
                            applyPaint: true,
                            applyCustomName: _cosmeticNamesEnabled);
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
                if (isReplayKnifeCosmetic &&
                    knifeCosmetic != null &&
                    _session.LoadedReplays.TryGetValue(slot, out var currentReplay) &&
                    currentReplay.Cosmetics.Knife != null)
                {
                    ScheduleKnifeCosmeticRetry(slot, knifeCosmetic, replaySteamId, framesRemaining: applied ? 3 : 8);
                }

                ScheduleCosmeticNextFrame(() =>
                {
                    if (!TryResolveOwnedReplayWeapon(
                            slot,
                            weaponEntityHandle,
                            out var retryPlayer,
                            out var retryWeapon))
                    {
                        return;
                    }

                    if (isReplayKnifeCosmetic)
                    {
                        if (_session.LoadedReplays.TryGetValue(slot, out var replay) &&
                            replay.Cosmetics.Knife is { } desiredKnife &&
                            HasActiveBotRandomizerClaim(
                                slot,
                                replay.SteamId,
                                DemoTracerCosmeticWriteField.Knife))
                        {
                            _ = TryApplyItemCosmetic(
                                retryPlayer,
                                retryWeapon,
                                desiredKnife,
                                replay.SteamId,
                                DemoTracerCosmeticWriteField.Knife,
                                allowSubclassChange: true,
                                applyPaint: true,
                                applyCustomName: _cosmeticNamesEnabled);
                        }
                    }
                    else if (GivenItemCosmeticFeatureEnabled() &&
                             _weaponAlignEnabled &&
                             IsReplaySlotPlaying(slot) &&
                             TryGetWeaponCosmeticForSlot(slot, normalizedWeaponDefIndex, out var retryCosmetic, out var retrySteamId))
                    {
                        _ = TryApplyWeaponCosmetic(retryPlayer, retryWeapon, retryCosmetic, retrySteamId);
                    }
                });
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
