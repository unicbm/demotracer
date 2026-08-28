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
    private void ProcessReplayTick()
    {
        ProcessDtrRoundBanner();
        ProcessVoiceTestPlayback();
        ProcessChatPlayback();
        if (_session.LoadedSlots.Count == 0)
        {
            SetReplayPovMask(0);
            RestoreAllReplayBotViewmodels();
            return;
        }

        if (_session.ReplaySlots.PlayingCount == 0)
        {
            SetReplayPovMask(0);
            RestoreNonRetainedReplayBotViewmodels();
            return;
        }

        Span<int> activeSlots = stackalloc int[MaxPlayerSlots];
        Span<ReplayState> activeStates = stackalloc ReplayState[MaxPlayerSlots];
        var trackedSlotCount = 0;
        foreach (var slot in _session.ReplaySlots.PlayingSlots)
        {
            if (slot is >= 0 and < MaxPlayerSlots)
                activeSlots[trackedSlotCount++] = slot;
        }

        var activeSlotCount = 0;
        for (var trackedIndex = 0; trackedIndex < trackedSlotCount; trackedIndex++)
        {
            var slot = activeSlots[trackedIndex];
            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing)
            {
                ReleaseReplaySlot(slot, "replay_finished", ReplayReleaseKind.Finished);
                continue;
            }

            activeSlots[activeSlotCount] = slot;
            activeStates[activeSlotCount] = state;
            activeSlotCount++;
        }

        if (activeSlotCount == 0)
        {
            SetReplayPovMask(0);
            RestoreNonRetainedReplayBotViewmodels();
            return;
        }

        var playerSnapshot = BuildTickPlayerSnapshot();
        UpdateReplayPovMask(playerSnapshot);
        UpdateReplayBotViewmodels(playerSnapshot);

        for (var activeIndex = 0; activeIndex < activeSlotCount; activeIndex++)
        {
            var slot = activeSlots[activeIndex];
            if (!_session.ReplaySlots.IsPlaying(slot))
                continue;
            var state = activeStates[activeIndex];

            if (!IsReplaySlotStillSafe(slot, playerSnapshot))
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "unsafe_replay_target");
                continue;
            }
            if (playerSnapshot.TryGetSlot(slot, out var replayPlayer) &&
                replayPlayer is { IsValid: true, PawnIsAlive: false })
            {
                BotControllerNative.StopReplay(slot);
                ReleaseReplaySlot(slot, "dead_replay_target");
                continue;
            }

            var hasLoadedReplay = _session.LoadedReplays.TryGetValue(slot, out var replay);
            if (hasLoadedReplay)
                ProcessReplayHifiEvents(slot, replay, state.Cursor);

            if (HandoffIncludesContact(_handoffMode) &&
                ReplayBotHasContact(slot, playerSnapshot, out var contactReason, out _))
            {
                HandoffActiveReplays($"enemy_contact_{contactReason}_slot{slot}", slot);
                continue;
            }

            if (!_weaponAlignEnabled)
                continue;

            var weaponDefIndex = NormalizeWeaponDefIndex(state.WeaponDefIndex);
            if (weaponDefIndex < 0)
            {
                _session.LastReplayWeaponDef.Remove(slot);
                continue;
            }
            if (_session.LastReplayWeaponDef.TryGetValue(slot, out var lastDef) &&
                lastDef == weaponDefIndex)
                continue;

            ApplyReplayWeaponPreset(slot, weaponDefIndex, force: false);
        }
    }

    private void ProcessReplayHifiEvents(int slot, LoadedReplay replay, int cursor)
    {
        if (cursor < 0 || replay.HifiEvents.Length == 0)
            return;

        var next = _session.ReplayHifiEventNextBySlot.GetValueOrDefault(slot);
        while (next < replay.HifiEvents.Length && replay.HifiEvents[next].TickIndex <= (uint)cursor)
        {
            ExecuteReplayHifiEvent(slot, replay, replay.HifiEvents[next]);
            next++;
        }
        _session.ReplayHifiEventNextBySlot[slot] = next;
    }

    private void ExecuteReplayHifiEvent(int slot, LoadedReplay replay, ReplayHifiEvent replayEvent)
    {
        var kind = replayEvent.Kind.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "item_drop":
                // Live replay ticks must not mutate inventory/entities. Keep item events
                // as metadata until replay-safe transfer machinery exists.
                break;

            case "bomb_drop":
                // C4 is a unique objective entity. Mid-replay DropActiveWeapon on C4 can
                // leave CS2 in an invalid bomb state, so runtime C4 transfer stays record-only.
                break;

            case "item_pickup":
            case "item_transfer":
                if (ShouldQueueReplayUtilityGrant(replayEvent, replay))
                    QueueReplayUtilityGrant(slot, replayEvent);
                break;

            case "bomb_pickup":
                // Safe C4 ownership is aligned before replay start. Do not clone or move C4
                // during live replay ticks.
                break;

            case "bomb_planted":
                // Actual server bomb_planted drives C4 handoff. Demo metadata stays
                // record-only so a failed or delayed live plant cannot hand off early.
                break;
        }
    }

    private bool ShouldQueueReplayUtilityGrant(
        ReplayHifiEvent replayEvent,
        LoadedReplay replay)
        => ReplayUtilityGrantPolicy.ShouldQueue(
            replayEvent,
            replay.SteamId,
            replay.PlayStartTickIndex,
            _replayEquipment);

    private void QueueReplayUtilityGrant(int slot, ReplayHifiEvent replayEvent)
    {
        var weaponDefIndex = ReplayEventWeaponDefIndex(replayEvent);
        if (!IsUtilityWeaponDefIndex(weaponDefIndex) ||
            !TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true } ||
            player.UserId is not int userId)
        {
            return;
        }
        var writeEpoch = CurrentReplayWriteEpoch(slot);

        var targetCount = Math.Max(1, replayEvent.TargetCountAfter ?? 1);
        var sourceTick = replayEvent.Tick;
        Server.NextFrame(() => EnsureReplayUtilityGrant(
            slot,
            userId,
            writeEpoch,
            className,
            targetCount,
            sourceTick));
    }

    private void EnsureReplayUtilityGrant(
        int slot,
        int userId,
        long writeEpoch,
        string className,
        int targetCount,
        int sourceTick)
    {
        if (!IsReplaySlotStillSafe(slot) ||
            !IsReplaySlotPlaying(slot) ||
            !IsReplayWriteEpochCurrent(slot, writeEpoch))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, PawnIsAlive: true } ||
            player.UserId != userId)
            return;

        var currentCount = CountCurrentReplayItems(player, className);
        if (currentCount >= targetCount)
            return;

        var missing = targetCount - currentCount;
        for (var i = 0; i < missing; i++)
        {
            if (!TryGiveNamedItem(player, className))
            {
                Server.PrintToConsole(
                    $"dtr: hifi utility grant failed slot={slot} item={className} tick={sourceTick}");
                return;
            }
        }

        _session.LastEnsuredWeaponDef.Remove(slot);
        _session.LastReplayWeaponDef.Remove(slot);
    }

    private int ReplayEventWeaponDefIndex(ReplayHifiEvent replayEvent)
    {
        if (replayEvent.WeaponDefIndex.HasValue)
            return NormalizeWeaponDefIndex(replayEvent.WeaponDefIndex.Value);
        if (string.IsNullOrWhiteSpace(replayEvent.ItemName))
            return -1;

        var itemName = NormalizeReplayEventItemName(replayEvent.ItemName);
        return NormalizeWeaponDefIndex(WeaponDefIndex(itemName));
    }

    private static string NormalizeReplayEventItemName(string itemName)
    {
        var normalized = itemName.Trim().ToLowerInvariant() switch
        {
            "decoy_grenade" or "weapon_decoy_grenade" => "weapon_decoy",
            "c4" or "weapon_c4_explosive" => "weapon_c4",
            var value => value
        };
        return normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"weapon_{normalized}";
    }

    private IEnumerable<CBasePlayerWeapon> GetReplayWeaponsByClass(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
            yield break;

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;
            if (ReplayWeaponMatches(weapon, className))
                yield return weapon;
        }
    }

    private void UpdateReplayPovMask(TickPlayerSnapshot playerSnapshot)
    {
        SetReplayPovMask(BuildReplayPovMask(playerSnapshot));
    }

    private ulong BuildReplayPovMask(TickPlayerSnapshot playerSnapshot)
    {
        if (_session.ReplaySlots.PlayingCount == 0)
            return 0;

        Span<uint> replayPawnIndices = stackalloc uint[MaxPlayerSlots];
        Span<int> replaySlots = stackalloc int[MaxPlayerSlots];
        var replayPawnCount = 0;
        foreach (var slot in _session.ReplaySlots.PlayingSlots)
        {
            if (slot is < 0 or >= MaxPlayerSlots)
                continue;

            if (!playerSnapshot.TryGetSlot(slot, out var replayController) ||
                replayController is not { IsValid: true })
                continue;
            if (replayController.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
                continue;

            replayPawnIndices[replayPawnCount] = replayPawn.Value.Index;
            replaySlots[replayPawnCount] = slot;
            replayPawnCount++;
        }

        if (replayPawnCount == 0)
            return 0;

        ulong mask = 0;
        foreach (var controller in playerSnapshot.Controllers)
        {
            if (controller is not { IsValid: true })
                continue;
            if (controller.IsBot || _botHiderBridge.IsManagedBot(controller.Slot))
                continue;
            if (!TryGetInEyeObserverTargetIndex(controller, out var targetIndex))
                continue;
            for (var replayIndex = 0; replayIndex < replayPawnCount; replayIndex++)
            {
                if (replayPawnIndices[replayIndex] != targetIndex)
                    continue;
                mask |= 1UL << replaySlots[replayIndex];
                break;
            }
        }

        return mask;
    }

    private static bool TryGetInEyeObserverTargetIndex(CCSPlayerController controller, out uint targetIndex)
    {
        targetIndex = 0;
        try
        {
            CPlayer_ObserverServices? observerServices = null;
            if (controller.ObserverPawn is { IsValid: true, Value.IsValid: true } observerPawn)
                observerServices = observerPawn.Value.ObserverServices;
            else if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } playerPawn)
                observerServices = playerPawn.Value.ObserverServices;

            if (observerServices == null ||
                observerServices.ObserverMode != (byte)ObserverMode_t.OBS_MODE_IN_EYE)
                return false;
            if (observerServices.ObserverTarget is not { IsValid: true, Value.IsValid: true } target)
                return false;

            targetIndex = target.Value.Index;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetReplayPovMask(ulong mask)
    {
        if (mask == _session.LastReplayPovMask)
            return;

        _ = BotControllerNative.SetReplayPovMask(mask);
        _session.LastReplayPovMask = mask;
    }

    private void ClearReplayPovSlot(int slot)
    {
        if (slot is < 0 or >= MaxPlayerSlots || _session.LastReplayPovMask == ulong.MaxValue)
            return;

        SetReplayPovMask(_session.LastReplayPovMask & ~(1UL << slot));
    }
}
