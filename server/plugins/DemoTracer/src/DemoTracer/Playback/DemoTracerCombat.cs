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
    private void HandoffActiveReplays(string reason, int triggerSlot = -1, bool forceAll = false)
    {
        if (!forceAll && triggerSlot < 0 && !_handoffAllSlots)
            return;

        var stopped = 0;
        var slots = (!forceAll && !_handoffAllSlots && triggerSlot >= 0)
            ? [triggerSlot]
            : _session.LoadedSlots.ToArray();
        foreach (var slot in slots)
        {
            if (!_session.ReplaySlots.IsPlaying(slot))
                continue;

            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason, ReplayReleaseKind.Handoff);
            stopped++;

            if (!forceAll && !_handoffAllSlots)
                break;
        }

        if (stopped > 0)
        {
            // Native replay stop may restore base userinfo; republish the retained lease.
            _ = SyncBotHiderPresentationLease(announce: false, forceReplace: true);
            Server.PrintToConsole($"dtr: handoff stopped {stopped} replay slot(s), reason={reason}");
        }
    }

    private int GetDeathHandoffSlot(int victimSlot, int attackerSlot)
    {
        if (_session.ReplaySlots.IsPlaying(victimSlot))
            return victimSlot;
        if (_session.ReplaySlots.IsPlaying(attackerSlot))
            return attackerSlot;
        return -1;
    }

    private bool TryGetEnemyBulletHandoffPair(
        CCSPlayerController? attacker,
        CCSPlayerController? victim,
        out int victimSlot,
        out int attackerSlot)
    {
        victimSlot = -1;
        attackerSlot = -1;

        if (attacker is not { IsValid: true } ||
            victim is not { IsValid: true } ||
            attacker.Slot == victim.Slot ||
            attacker.Team == victim.Team ||
            !victim.PawnIsAlive ||
            !attacker.PawnIsAlive)
            return false;

        if (!_session.ReplaySlots.IsPlaying(victim.Slot))
            return false;

        victimSlot = victim.Slot;
        attackerSlot = attacker.Slot;
        return true;
    }

    private bool TryHandoffBulletDamagedReplay(int victimSlot, int attackerSlot, int damage)
    {
        if (damage < BulletHandoffMinDamage ||
            !_session.ReplaySlots.IsPlaying(victimSlot))
            return false;

        HandoffActiveReplays(
            $"bullet_damage_slot{victimSlot}_attacker{attackerSlot}_dmg{damage}",
            victimSlot);
        return true;
    }

    private void PruneExpiredBulletHandoffState()
    {
        if (_session.PendingBulletHits.Count == 0 && _session.PendingBulletDamages.Count == 0)
            return;

        foreach (var (slot, hit) in _session.PendingBulletHits.ToArray())
        {
            if (!IsFreshBulletHandoffEvent(hit.Time))
                _session.PendingBulletHits.Remove(slot);
        }

        foreach (var (slot, damage) in _session.PendingBulletDamages.ToArray())
        {
            if (!IsFreshBulletHandoffEvent(damage.Time))
                _session.PendingBulletDamages.Remove(slot);
        }
    }

    private static bool IsFreshBulletHandoffEvent(float eventTime)
        => Server.CurrentTime - eventTime <= BulletHandoffMatchSeconds;

    private bool ReplayBotHasContact(int slot, out string contactReason)
    {
        contactReason = string.Empty;
        // Only native vision captured after replay ownership can trigger contact.
        // Missing or invalid perception must never become a distance-only handoff.
        if (!_session.ReplayPerceptionBaselineSerial.TryGetValue(slot, out var baselineSerial) ||
            !BotControllerNative.TryGetNativePerceptionState(slot, out var state) ||
            !IsFreshNativeVisibleEnemy(state, baselineSerial))
            return false;

        contactReason = $"native_visible_parts{state.VisibleEnemyParts}";
        return true;
    }

    internal static bool IsFreshNativeVisibleEnemy(
        NativePerceptionState state,
        uint baselineSerial)
    {
        var serialDelta = unchecked(state.UpdateSerial - baselineSerial);
        var hasFreshUpdate = serialDelta != 0 && serialDelta < 0x80000000u;
        return state.Valid != 0 &&
               hasFreshUpdate &&
               state.HasEnemy != 0 &&
               state.EnemyVisible != 0 &&
               state.VisibleEnemyParts != 0 &&
               state.LastEnemyDead == 0;
    }

    private static bool HasLivePawn(CCSPlayerController? player)
    {
        if (player is not { IsValid: true } ||
            player.PlayerPawn is not { IsValid: true, Value.IsValid: true } pawn)
            return false;

        if (player.PawnIsAlive)
            return true;

        try
        {
            return pawn.Value.Health > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPawnOrigin(CCSPlayerController player, out Vector origin)
    {
        origin = new Vector(0.0f, 0.0f, 0.0f);
        if (player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;
        var value = player.PlayerPawn.Value.AbsOrigin;
        if (value == null)
            return false;
        origin = value;
        return true;
    }
}
