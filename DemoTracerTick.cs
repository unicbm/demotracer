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
            RestoreAllReplayBotViewmodels();
            return;
        }

        if (_session.ReplaySlots.PlayingCount == 0)
        {
            RestoreNonRetainedReplayBotViewmodels();
            return;
        }

        Span<int> activeSlots = stackalloc int[MaxPlayerSlots];
        Span<ReplayState> activeStates = stackalloc ReplayState[MaxPlayerSlots];
        Span<int> completedLoopSlots = stackalloc int[MaxPlayerSlots];
        var completedLoopCount = 0;
        var trackedSlotCount = 0;
        foreach (var slot in _session.ReplaySlots.PlayingSlots)
        {
            if (slot is >= 0 and < MaxPlayerSlots)
                activeSlots[trackedSlotCount++] = slot;
        }

        var playerSnapshot = BuildTickPlayerSnapshot();
        var activeSlotCount = 0;
        for (var trackedIndex = 0; trackedIndex < trackedSlotCount; trackedIndex++)
        {
            var slot = activeSlots[trackedIndex];
            if (!_session.ReplaySlots.IsPlaying(slot))
                continue;
            // Completed loop slots still own writes while they wait for the
            // other participants. Death and takeover end that ownership now.
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
            if (HandoffIncludesContact(_handoffMode) &&
                ReplayBotHasContact(slot, playerSnapshot, out var contactReason, out _))
            {
                HandoffActiveReplays($"enemy_contact_{contactReason}_slot{slot}", slot);
                continue;
            }
            var state = BotControllerNative.GetReplayState(slot);
            if (!state.Playing)
            {
                if (state.Total > 0 && state.Cursor >= state.Total &&
                    _session.ReplaySlots.TryGet(slot, out var runtime) && runtime.Loop)
                {
                    completedLoopSlots[completedLoopCount++] = slot;
                    continue;
                }
                ReleaseReplaySlot(slot, "replay_finished", ReplayReleaseKind.Finished);
                continue;
            }

            activeSlots[activeSlotCount] = slot;
            activeStates[activeSlotCount] = state;
            activeSlotCount++;
        }

        if (_session.ReplaySlots.IsCompletedLoop(completedLoopSlots[..completedLoopCount]))
        {
            RestartCompletedReplayLoop(completedLoopSlots[..completedLoopCount]);
            return;
        }

        if (activeSlotCount == 0)
        {
            RestoreNonRetainedReplayBotViewmodels();
            return;
        }

        UpdateReplayBotViewmodels(playerSnapshot);

        for (var activeIndex = 0; activeIndex < activeSlotCount; activeIndex++)
        {
            var slot = activeSlots[activeIndex];
            if (!_session.ReplaySlots.IsPlaying(slot))
                continue;
            var state = activeStates[activeIndex];

            var hasLoadedReplay = _session.LoadedReplays.TryGetValue(slot, out var replay);
            if (hasLoadedReplay)
                ProcessReplayHifiEvents(slot, replay, state.Cursor);

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
}
