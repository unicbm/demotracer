/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using System.Globalization;
using System.Reflection;

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
            if (!BotControllerNative.GetReplayState(slot).Playing)
                continue;

            BotControllerNative.StopReplay(slot);
            ReleaseReplaySlot(slot, reason, ReplayReleaseKind.Handoff);
            stopped++;

            if (!forceAll && !_handoffAllSlots)
                break;
        }

        if (stopped > 0)
        {
            // Stopping native replay can republish the bots' base userinfo.
            // The lease contents and signature are intentionally unchanged at
            // handoff, so a heartbeat alone cannot repair that external write.
            _ = SyncBotHiderPresentationLease(announce: false, forceReplace: true);
            Server.PrintToConsole($"dtr: handoff stopped {stopped} replay slot(s), reason={reason}");
        }
    }

    private int GetDeathHandoffSlot(EventPlayerDeath @event)
    {
        if (@event.Userid is { IsValid: true } victim && IsReplaySlotPlaying(victim.Slot))
            return victim.Slot;
        if (@event.Attacker is { IsValid: true } attacker && IsReplaySlotPlaying(attacker.Slot))
            return attacker.Slot;
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

        if (!IsReplaySlotPlaying(victim.Slot))
            return false;

        victimSlot = victim.Slot;
        attackerSlot = attacker.Slot;
        return true;
    }

    private bool TryHandoffBulletDamagedReplay(int victimSlot, int attackerSlot, int damage)
    {
        if (damage < BulletHandoffMinDamage ||
            !IsReplaySlotPlaying(victimSlot))
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

    private static bool IsReplaySlotPlaying(int slot)
    {
        return slot >= 0 && BotControllerNative.GetReplayState(slot).Playing;
    }

    private bool ReplayHasPassedHandoffGrace(int slot)
    {
        return !_session.ReplayStartedAt.TryGetValue(slot, out var startedAt) ||
               Server.CurrentTime - startedAt >= HandoffGraceSeconds;
    }

    private static bool ReplayBotSeesEnemy(int slot, out string contactReason)
    {
        return ReplayBotSeesEnemy(slot, FindTeamPlayers(), out contactReason, out _);
    }

    private static bool ReplayBotSeesEnemy(
        int slot,
        IReadOnlyList<CCSPlayerController> teamPlayers,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot == null || !HasLivePawn(bot))
            return false;

        foreach (var enemy in teamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy))
                continue;

            if (PlayerSeesTarget(bot, enemy, out contactReason))
            {
                contactSlot = enemy.Slot;
                return true;
            }
        }

        return false;
    }

    private static bool ReplayBotSeesEnemy(
        int slot,
        TickPlayerSnapshot playerSnapshot,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        if (!playerSnapshot.TryGetSlot(slot, out var bot) || !HasLivePawn(bot))
            return false;

        foreach (var enemy in playerSnapshot.TeamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy))
                continue;

            if (PlayerSeesTarget(bot, enemy, out contactReason))
            {
                contactSlot = enemy.Slot;
                return true;
            }
        }

        return false;
    }

    private bool ReplayBotHasContact(
        int slot,
        IReadOnlyList<CCSPlayerController> teamPlayers,
        out string contactReason,
        out int contactSlot)
    {
        if (TryEvaluateNativeReplayContact(slot, out var nativeContact, out contactReason))
        {
            contactSlot = -1;
            return nativeContact;
        }

        if (!ReplayHasPassedHandoffGrace(slot))
        {
            contactReason = string.Empty;
            contactSlot = -1;
            return false;
        }

        if (ReplayBotSeesEnemy(slot, teamPlayers, out contactReason, out contactSlot))
            return true;

        if (_handoffThreat360Enabled &&
            ReplayBotHasNearby360Threat(slot, teamPlayers, out contactReason, out contactSlot))
            return true;

        contactSlot = -1;
        return false;
    }

    private bool ReplayBotHasContact(
        int slot,
        TickPlayerSnapshot playerSnapshot,
        out string contactReason,
        out int contactSlot)
    {
        if (TryEvaluateNativeReplayContact(slot, out var nativeContact, out contactReason))
        {
            contactSlot = -1;
            return nativeContact;
        }

        if (!ReplayHasPassedHandoffGrace(slot))
        {
            contactReason = string.Empty;
            contactSlot = -1;
            return false;
        }

        if (ReplayBotSeesEnemy(slot, playerSnapshot, out contactReason, out contactSlot))
            return true;

        if (_handoffThreat360Enabled &&
            ReplayBotHasNearby360Threat(slot, playerSnapshot, out contactReason, out contactSlot))
            return true;

        contactSlot = -1;
        return false;
    }

    // Return true when the native lane is available/evaluated, with contact
    // carrying its result. A false return means an older BotController is in
    // use and the managed spotted/raytrace detector should remain the fallback.
    private bool TryEvaluateNativeReplayContact(
        int slot,
        out bool contact,
        out string contactReason)
    {
        contact = false;
        contactReason = string.Empty;
        if (!BotControllerNative.HasNativePerceptionCapability)
            return false;

        // Never consume a visibility result captured before this replay took
        // ownership. Update/Upkeep may shadow-run for perception, but only a
        // fresh, currently visible live enemy is allowed to trigger handoff.
        if (!_session.ReplayPerceptionBaselineSerial.TryGetValue(slot, out var baselineSerial) ||
            !BotControllerNative.TryGetNativePerceptionState(slot, out var state))
            return true;

        if (IsFreshNativeVisibleEnemy(state, baselineSerial))
        {
            contact = true;
            contactReason = $"native_visible_parts{state.VisibleEnemyParts}";
            return true;
        }
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

    private bool ReplayBotHasNearby360Threat(
        int slot,
        IReadOnlyList<CCSPlayerController> teamPlayers,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot == null ||
            !HasLivePawn(bot) ||
            !TryGetPawnOrigin(bot, out var botOrigin))
        {
            _session.PendingThreat360.Remove(slot);
            return false;
        }

        var rangeSq = _handoffThreat360Range * _handoffThreat360Range;
        var bestEnemySlot = -1;
        var bestDistanceSq = float.MaxValue;

        foreach (var enemy in teamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy) ||
                !IsHandoff360ThreatActor(enemy) ||
                !TryGetPawnOrigin(enemy, out var enemyOrigin))
                continue;

            var dz = MathF.Abs(enemyOrigin.Z - botOrigin.Z);
            if (dz > HandoffThreat360MaxVerticalDelta)
                continue;

            var dx = enemyOrigin.X - botOrigin.X;
            var dy = enemyOrigin.Y - botOrigin.Y;
            var distanceSq = dx * dx + dy * dy;
            if (distanceSq > rangeSq || distanceSq >= bestDistanceSq)
                continue;
            if (_handoffThreat360LosEnabled && !HasHandoff360LineOfSight(bot, enemy))
                continue;

            bestEnemySlot = enemy.Slot;
            bestDistanceSq = distanceSq;
        }

        if (bestEnemySlot < 0)
        {
            _session.PendingThreat360.Remove(slot);
            return false;
        }

        var distance = MathF.Sqrt(bestDistanceSq);
        if (distance <= MathF.Min(HandoffThreat360ImmediateRange, _handoffThreat360Range))
        {
            _session.PendingThreat360.Remove(slot);
            contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: true);
            contactSlot = bestEnemySlot;
            return true;
        }

        var now = Server.CurrentTime;
        if (!_session.PendingThreat360.TryGetValue(slot, out var pending) ||
            pending.EnemySlot != bestEnemySlot)
        {
            _session.PendingThreat360[slot] = new PendingThreat360(bestEnemySlot, now);
            return false;
        }

        if (now - pending.FirstSeenAt < HandoffThreat360HoldSeconds)
            return false;

        _session.PendingThreat360.Remove(slot);
        contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: false);
        contactSlot = bestEnemySlot;
        return true;
    }

    private bool ReplayBotHasNearby360Threat(
        int slot,
        TickPlayerSnapshot playerSnapshot,
        out string contactReason,
        out int contactSlot)
    {
        contactReason = string.Empty;
        contactSlot = -1;
        if (!playerSnapshot.TryGetSlot(slot, out var bot) ||
            !HasLivePawn(bot) ||
            !TryGetPawnOrigin(bot, out var botOrigin))
        {
            _session.PendingThreat360.Remove(slot);
            return false;
        }

        var rangeSq = _handoffThreat360Range * _handoffThreat360Range;
        var bestEnemySlot = -1;
        var bestDistanceSq = float.MaxValue;

        foreach (var enemy in playerSnapshot.TeamPlayers)
        {
            if (enemy.Slot == bot.Slot ||
                enemy.Team == bot.Team ||
                !HasLivePawn(enemy) ||
                !IsHandoff360ThreatActor(enemy, playerSnapshot) ||
                !TryGetPawnOrigin(enemy, out var enemyOrigin))
                continue;

            var dz = MathF.Abs(enemyOrigin.Z - botOrigin.Z);
            if (dz > HandoffThreat360MaxVerticalDelta)
                continue;

            var dx = enemyOrigin.X - botOrigin.X;
            var dy = enemyOrigin.Y - botOrigin.Y;
            var distanceSq = dx * dx + dy * dy;
            if (distanceSq > rangeSq || distanceSq >= bestDistanceSq)
                continue;
            if (_handoffThreat360LosEnabled && !HasHandoff360LineOfSight(bot, enemy))
                continue;

            bestEnemySlot = enemy.Slot;
            bestDistanceSq = distanceSq;
        }

        if (bestEnemySlot < 0)
        {
            _session.PendingThreat360.Remove(slot);
            return false;
        }

        var distance = MathF.Sqrt(bestDistanceSq);
        if (distance <= MathF.Min(HandoffThreat360ImmediateRange, _handoffThreat360Range))
        {
            _session.PendingThreat360.Remove(slot);
            contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: true);
            contactSlot = bestEnemySlot;
            return true;
        }

        var now = Server.CurrentTime;
        if (!_session.PendingThreat360.TryGetValue(slot, out var pending) ||
            pending.EnemySlot != bestEnemySlot)
        {
            _session.PendingThreat360[slot] = new PendingThreat360(bestEnemySlot, now);
            return false;
        }

        if (now - pending.FirstSeenAt < HandoffThreat360HoldSeconds)
            return false;

        _session.PendingThreat360.Remove(slot);
        contactReason = FormatThreat360Reason(bestEnemySlot, distance, immediate: false);
        contactSlot = bestEnemySlot;
        return true;
    }

    private bool IsHandoff360ThreatActor(CCSPlayerController enemy)
    {
        if (enemy is not { IsValid: true } || enemy.Slot < 0)
            return false;
        if (_session.ReplaySlots.IsLoaded(enemy.Slot) || IsReplaySlotPlaying(enemy.Slot))
            return false;
        if (_botHiderBridge.IsManagedBot(enemy.Slot))
            return false;
        var controllingBot = TryGetControllingBotState(enemy, out var controlsBot) && controlsBot;
        return !enemy.IsBot || controllingBot;
    }

    private bool IsHandoff360ThreatActor(
        CCSPlayerController enemy,
        TickPlayerSnapshot playerSnapshot)
    {
        if (enemy is not { IsValid: true } || enemy.Slot < 0)
            return false;
        if (_session.ReplaySlots.IsLoaded(enemy.Slot))
            return false;
        if (_botHiderBridge.IsManagedBot(enemy.Slot))
            return false;
        var controllingBot = TryGetControllingBotState(enemy, out var controlsBot) && controlsBot;
        return !enemy.IsBot || controllingBot;
    }

    private bool HasHandoff360LineOfSight(CCSPlayerController bot, CCSPlayerController enemy)
    {
        if (!TryGetPawnEyePosition(enemy, out var enemyEye) ||
            !TryGetPawnEyePosition(bot, out var botEye) ||
            !TryGetPawnPoint(bot, HandoffThreat360ChestZScale, out var botChest))
            return false;

        if (!_rayTraceLosProbe.TryIsWorldLineClear(enemyEye, botEye, out var eyeClear))
            return true;
        if (eyeClear)
            return true;

        return _rayTraceLosProbe.TryIsWorldLineClear(enemyEye, botChest, out var chestClear) && chestClear;
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

    private static bool TryGetPawnEyePosition(CCSPlayerController player, out Vector eye)
    {
        return TryGetPawnPoint(player, 1.0f, out eye);
    }

    private static bool TryGetPawnPoint(CCSPlayerController player, float viewOffsetScale, out Vector point)
    {
        point = new Vector(0.0f, 0.0f, 0.0f);
        if (player.PlayerPawn is not { IsValid: true, Value.IsValid: true } pawn)
            return false;
        var origin = pawn.Value.AbsOrigin;
        var viewOffset = pawn.Value.ViewOffset;
        if (origin == null || viewOffset == null)
            return false;
        point = new Vector(
            origin.X + viewOffset.X,
            origin.Y + viewOffset.Y,
            origin.Z + viewOffset.Z * viewOffsetScale);
        return true;
    }

    private static string FormatThreat360Reason(int enemySlot, float distance, bool immediate)
    {
        var kind = immediate ? "near360" : "near360_held";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{kind}_enemy{enemySlot}_dist{distance:F0}");
    }

    private static bool PlayerSeesTarget(
        CCSPlayerController observer,
        CCSPlayerController target,
        out string contactReason)
    {
        contactReason = string.Empty;
        if (observer.Slot < 0)
            return false;
        if (target.PlayerPawn is not { IsValid: true, Value.IsValid: true })
            return false;

        try
        {
            var spotted = target.PlayerPawn.Value.EntitySpottedState;
            if (spotted != null)
            {
                var mask = spotted.SpottedByMask;
                var word = observer.Slot / 32;
                var bit = observer.Slot % 32;
                if (word >= 0 && word < mask.Length && (mask[word] & (1u << bit)) != 0)
                {
                    contactReason = "spotted";
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private sealed class RayTraceLosProbe
    {
        private const string CapabilityName = "raytrace:craytraceinterface";
        private const string ApiAssemblyName = "RayTraceApi";
        private const string RayTraceInterfaceTypeName = "RayTraceAPI.CRayTraceInterface";
        private const string TraceOptionsTypeName = "RayTraceAPI.TraceOptions";
        private const string TraceResultTypeName = "RayTraceAPI.TraceResult";
        private const string InteractionLayersTypeName = "RayTraceAPI.InteractionLayers";

        private bool _initialized;
        private object? _capability;
        private object? _traceOptions;
        private MethodInfo? _getMethod;
        private MethodInfo? _traceEndShapeMethod;
        private FieldInfo? _fractionField;
        private string _status = "unresolved";
        private DateTime _nextInitAttemptAt = DateTime.MinValue;

        public string Status
        {
            get
            {
                EnsureInitialized();
                return _status;
            }
        }

        public string ProbeStatus
        {
            get
            {
                _ = TryGetRayTrace(out _);
                return _status;
            }
        }

        public bool TryIsWorldLineClear(Vector start, Vector end, out bool clear)
        {
            clear = false;
            if (!TryGetRayTrace(out var rayTrace))
                return false;

            try
            {
                var args = new object?[] { start, end, null, _traceOptions, null };
                var hit = _traceEndShapeMethod!.Invoke(rayTrace, args) is true;
                if (!hit)
                {
                    clear = true;
                    _status = "available";
                    return true;
                }

                var result = args[4];
                if (result == null)
                {
                    _status = "bad_result";
                    return false;
                }

                var fraction = Convert.ToSingle(_fractionField!.GetValue(result), CultureInfo.InvariantCulture);
                clear = fraction >= 0.999f;
                _status = "available";
                return true;
            }
            catch
            {
                _status = "invoke_error";
                return false;
            }
        }

        private bool TryGetRayTrace(out object rayTrace)
        {
            rayTrace = null!;
            EnsureInitialized();
            if (_capability == null || _getMethod == null)
                return false;

            try
            {
                var value = _getMethod.Invoke(_capability, null);
                if (value == null)
                {
                    _status = "no_provider";
                    return false;
                }

                rayTrace = value;
                return true;
            }
            catch
            {
                _status = "get_error";
                return false;
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            if (DateTime.UtcNow < _nextInitAttemptAt)
                return;
            _initialized = true;

            try
            {
                var interfaceType = ResolveRayTraceType(RayTraceInterfaceTypeName);
                var optionsType = ResolveRayTraceType(TraceOptionsTypeName);
                var resultType = ResolveRayTraceType(TraceResultTypeName);
                var layersType = ResolveRayTraceType(InteractionLayersTypeName);
                if (interfaceType == null || optionsType == null || resultType == null || layersType == null)
                {
                    _status = "api_missing";
                    RetryInitializeLater();
                    return;
                }

                var capabilityType = typeof(PluginCapability<>).MakeGenericType(interfaceType);
                _capability = Activator.CreateInstance(capabilityType, CapabilityName);
                _getMethod = capabilityType.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance);
                _traceEndShapeMethod = interfaceType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "TraceEndShape" && method.GetParameters().Length == 5);
                _fractionField = resultType.GetField("Fraction", BindingFlags.Public | BindingFlags.Instance);
                _traceOptions = Activator.CreateInstance(optionsType);
                var interactsWith = optionsType.GetField("InteractsWith", BindingFlags.Public | BindingFlags.Instance);
                if (_traceOptions != null && interactsWith != null)
                {
                    var worldOnly = Enum.Parse(layersType, "MASK_WORLD_ONLY");
                    interactsWith.SetValue(_traceOptions, Convert.ToUInt64(worldOnly, CultureInfo.InvariantCulture));
                }

                if (_capability == null ||
                    _getMethod == null ||
                    _traceEndShapeMethod == null ||
                    _fractionField == null ||
                    _traceOptions == null)
                {
                    _status = "api_incomplete";
                    return;
                }

                _status = "ready";
            }
            catch
            {
                _status = "init_error";
                RetryInitializeLater();
            }
        }

        private void RetryInitializeLater()
        {
            _initialized = false;
            _nextInitAttemptAt = DateTime.UtcNow.AddSeconds(1.0);
        }

        private static Type? ResolveRayTraceType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var existing = assembly.GetType(fullName, throwOnError: false);
                if (existing != null)
                    return existing;
            }

            try
            {
                return Assembly.Load(new AssemblyName(ApiAssemblyName)).GetType(fullName, throwOnError: false);
            }
            catch
            {
                return Type.GetType($"{fullName}, {ApiAssemblyName}", throwOnError: false);
            }
        }
    }
}
