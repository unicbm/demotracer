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
    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!_mapActive || _lifecycleResetInProgress)
            return;

        TryApplySpawnedReplayWeaponCosmetic(entity);

        if (!TryGetProjectileKind(entity, out var kind, out var weaponDefIndex))
            return;

        try
        {
            var projectile = new CBaseCSGrenadeProjectile(entity.Handle);
            if (!projectile.IsValid)
                return;
            TrackProjectileAlignCandidate(projectile, kind, weaponDefIndex);
        }
        catch (Exception ex)
        {
            RememberProjectileAlignEvent(
                "projectile_spawn_failed",
                $"entity={entity.Index} error=\"{EscapeConsoleString(ex.Message)}\"");
        }
    }

    private bool TryGetProjectileKind(
        CEntityInstance entity,
        out ReplayProjectileKind kind,
        out int weaponDefIndex)
    {
        kind = ReplayProjectileKind.Unknown;
        weaponDefIndex = -1;
        if (!entity.IsValid || string.IsNullOrEmpty(entity.DesignerName))
            return false;

        var name = entity.DesignerName;
        string? weaponClassName = null;
        if (IsSmokeProjectileName(name))
        {
            kind = ReplayProjectileKind.Smoke;
            weaponClassName = "weapon_smokegrenade";
        }
        else if (name.Contains("flashbang_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Flash;
            weaponClassName = "weapon_flashbang";
        }
        else if (name.Contains("hegrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("he_grenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.He;
            weaponClassName = "weapon_hegrenade";
        }
        else if (name.Contains("incgrenade_projectile", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("incendiarygrenade_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Molotov;
            weaponClassName = "weapon_incgrenade";
        }
        else if (name.Contains("molotov_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Molotov;
            weaponClassName = "weapon_molotov";
        }
        else if (name.Contains("decoy_projectile", StringComparison.OrdinalIgnoreCase))
        {
            kind = ReplayProjectileKind.Decoy;
            weaponClassName = "weapon_decoy";
        }

        if (weaponClassName == null)
            return false;
        weaponDefIndex = WeaponDefIndex(weaponClassName);
        return weaponDefIndex > 0;
    }

    private static bool IsSmokeProjectileName(string name)
        => name.Contains("smokegrenade_projectile", StringComparison.OrdinalIgnoreCase);

    private void TrackProjectileAlignCandidate(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        if (!_projectileAlignEnabled)
            return;

        var pending = new PendingProjectileAlign(projectile.Index, projectile.Handle, kind, weaponDefIndex)
        {
            MatchAttemptsRemaining = ProjectileAlignMatchAttempts,
            WritesRemaining = 0
        };
        _session.PendingProjectileAlign[projectile.Index] = pending;
        RememberProjectileAlignEvent(
            "projectile_align_candidate",
            $"projectile={projectile.Index} kind={kind} weapon={weaponDefIndex} ticks={FormatProjectileAlignTicks()}");

        TryResolveAndApplyProjectileAlign(projectile, pending);
    }

    private void ProcessPendingProjectileAlign()
    {
        if (_session.PendingProjectileAlign.Count == 0)
            return;

        _session.PendingProjectileAlignTickScratch.Clear();
        foreach (var entry in _session.PendingProjectileAlign)
            _session.PendingProjectileAlignTickScratch.Add(entry);
        _session.PendingProjectileAlignTickScratch.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        foreach (var entry in _session.PendingProjectileAlignTickScratch)
        {
            var pending = entry.Value;
            try
            {
                var projectile = new CBaseCSGrenadeProjectile(pending.Handle);
                if (!projectile.IsValid)
                {
                    FinishProjectileAlign(entry.Key, pending, "entity_invalid");
                    continue;
                }

                if (!pending.Matched)
                {
                    if (TryResolveAndApplyProjectileAlign(projectile, pending))
                        continue;

                    pending.MatchAttemptsRemaining--;
                    if (pending.MatchAttemptsRemaining <= 0)
                    {
                        RememberProjectileAlignEvent(
                            "projectile_align_expired",
                            $"projectile={pending.Index} kind={pending.Kind} weapon={pending.WeaponDefIndex} reason=no_match");
                        _session.PendingProjectileAlign.Remove(entry.Key);
                    }
                    else
                        _session.PendingProjectileAlign[entry.Key] = pending;
                    continue;
                }

                if (pending.WritesRemaining == 0)
                {
                    if (TryProcessMolotovPointAlign(projectile, pending))
                    {
                        if (pending.MolotovPointAlignApplied)
                            FinishProjectileAlign(entry.Key, pending, "molotov_point_align");
                        else
                            _session.PendingProjectileAlign[entry.Key] = pending;
                        continue;
                    }

                    FinishProjectileAlign(entry.Key, pending, "write_budget");
                    continue;
                }

                ApplyTrackedProjectileAlign(projectile, pending);
                if (pending.WritesRemaining != ProjectileAlignUntilDelete)
                    pending.WritesRemaining--;
                if (pending.WritesRemaining == 0)
                {
                    if (TryProcessMolotovPointAlign(projectile, pending))
                    {
                        if (pending.MolotovPointAlignApplied)
                            FinishProjectileAlign(entry.Key, pending, "molotov_point_align");
                        else
                            _session.PendingProjectileAlign[entry.Key] = pending;
                        continue;
                    }

                    FinishProjectileAlign(entry.Key, pending, "write_budget");
                }
                else
                    _session.PendingProjectileAlign[entry.Key] = pending;
            }
            catch (Exception ex)
            {
                _session.PendingProjectileAlign.Remove(entry.Key);
                RememberProjectileAlignEvent(
                    "projectile_align_failed",
                    $"projectile={entry.Key} kind={pending.Kind} error=\"{EscapeConsoleString(ex.Message)}\"");
            }
        }
        _session.PendingProjectileAlignTickScratch.Clear();
    }

    private bool TryResolveAndApplyProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending)
    {
        if (!_projectileAlignEnabled ||
            !TryResolveProjectileAlign(
                projectile,
                pending.Kind,
                pending.WeaponDefIndex,
                out var slot,
                out var eventIndex,
                out var align))
            return false;

        var decision = EvaluateProjectileAlign(projectile, align, out var skipReason);
        if (decision == ProjectileAlignDecision.Retry)
        {
            if (pending.MatchAttemptsRemaining > 1)
                return false;

            skipReason = $"{skipReason}_expired";
            decision = ProjectileAlignDecision.Skip;
        }

        _session.ProjectileAlignNextBySlot[slot] = eventIndex + 1;
        if (decision == ProjectileAlignDecision.Skip)
        {
            _session.PendingProjectileAlign.Remove(pending.Index);
            var message =
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} reason={skipReason}";
            RememberProjectileAlignEvent("projectile_align_skipped", message);
            return true;
        }

        pending.Matched = true;
        pending.Slot = slot;
        pending.EventIndex = eventIndex;
        pending.Align = align;
        pending.TotalWritesTarget = _projectileAlignTotalWrites;
        ArmMolotovPointAlign(pending, align);
        ApplyTrackedProjectileAlign(projectile, pending);
        pending.WritesRemaining = RemainingProjectileAlignWrites(pending.TotalWritesTarget, pending.WritesApplied);
        var writeBudgetExhausted = pending.WritesRemaining == 0;
        if (!writeBudgetExhausted || pending.MolotovPointAlignArmed)
            _session.PendingProjectileAlign[pending.Index] = pending;

        RememberProjectileAlignEvent(
            "projectile_align",
            $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} ticks={FormatProjectileAlignTicks()} native_birth_rc={pending.LastNativeBirthRc} molotov_point={FormatPendingMolotovPointAlign(pending)} init_vel=({align.InitialVelocity.X:F3},{align.InitialVelocity.Y:F3},{align.InitialVelocity.Z:F3}) effect={align.EffectSource}:{align.EffectConfidence:F2}");
        if (writeBudgetExhausted && !pending.MolotovPointAlignArmed)
            FinishProjectileAlign(pending.Index, pending, "write_budget");
        return true;
    }

    private bool TryResolveProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex,
        out int slot,
        out int eventIndex,
        out ReplayProjectileEvent align)
    {
        slot = -1;
        eventIndex = -1;
        align = default;

        if (!TryGetProjectileThrowerSlot(projectile, out slot))
            return false;
        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) || replay.Projectiles.Length == 0)
            return false;

        var state = BotControllerNative.GetReplayState(slot);
        if (!state.Playing)
            return false;

        var next = _session.ProjectileAlignNextBySlot.TryGetValue(slot, out var value) ? value : 0;
        eventIndex = FindProjectileAlignEvent(replay.Projectiles, next, state.Cursor, kind, weaponDefIndex);
        if (eventIndex < 0)
            return false;

        align = replay.Projectiles[eventIndex];
        return true;
    }

    private static void ApplyProjectileAlign(CBaseCSGrenadeProjectile projectile, ReplayProjectileEvent align)
    {
        SetVector(projectile.InitialPosition, align.InitialPosition);
        SetVector(projectile.InitialVelocity, align.InitialVelocity);
        SetVector(projectile.AbsOrigin, align.InitialPosition);
        SetVector(projectile.AbsVelocity, align.InitialVelocity);
    }

    private void ApplyTrackedProjectileAlign(CBaseCSGrenadeProjectile projectile, PendingProjectileAlign pending)
    {
        pending.LastNativeBirthRc = QueueNativeProjectileBirthAlign(projectile, pending.Align);
        if (pending.LastNativeBirthRc != 0)
            ApplyProjectileAlign(projectile, pending.Align);
        var now = Server.CurrentTime;
        if (pending.WritesApplied == 0)
            pending.FirstWriteTime = now;
        pending.LastWriteTime = now;
        pending.WritesApplied++;
    }

    private int QueueNativeProjectileBirthAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileEvent align)
    {
        var entityPtr = unchecked((ulong)projectile.Handle);
        var rc = BotControllerNative.QueueProjectileBirthAlign(
            entityPtr,
            align.InitialPosition,
            align.InitialVelocity);
        if (rc == 0)
            return 0;

        RememberProjectileAlignEvent(
            "projectile_birth_align_fallback",
            $"projectile={projectile.Index} kind={align.Kind} native_birth_rc={rc}");
        return rc;
    }

    private void ArmMolotovPointAlign(PendingProjectileAlign pending, ReplayProjectileEvent align)
    {
        pending.MolotovPointAlignArmed = false;
        pending.MolotovPointAlignApplied = false;
        pending.MolotovPointAlignMode = MolotovPointAlignMode.Off;
        pending.MolotovPointAlignTargetTickIndex = -1;

        if (_molotovPointAlignMode == MolotovPointAlignMode.Off ||
            align.Kind != ReplayProjectileKind.Molotov ||
            !HasReliableFireProjectileMetadata(align))
        {
            return;
        }

        pending.MolotovPointAlignArmed = true;
        pending.MolotovPointAlignMode = _molotovPointAlignMode;
        pending.MolotovPointAlignTargetTickIndex = Math.Max(0, align.EffectTickIndex - _molotovPointAlignLeadTicks);
    }

    private bool TryProcessMolotovPointAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending)
    {
        if (!pending.MolotovPointAlignArmed || pending.MolotovPointAlignApplied)
            return false;

        var state = BotControllerNative.GetReplayState(pending.Slot);
        if (!state.Playing)
            return true;

        var targetTick = pending.MolotovPointAlignTargetTickIndex;
        if (targetTick < 0)
            return false;

        var cursor = state.Cursor;
        if (cursor > pending.Align.EffectTickIndex + 16)
        {
            RememberProjectileAlignEvent(
                "molotov_point_align_expired",
                $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} cursor={cursor} effect_tick={pending.Align.EffectTickIndex}");
            pending.MolotovPointAlignApplied = true;
            return true;
        }

        if (cursor < targetTick)
            return true;

        ApplyMolotovPointAlign(projectile, pending, cursor);
        pending.MolotovPointAlignApplied = true;
        return true;
    }

    private void ApplyMolotovPointAlign(
        CBaseCSGrenadeProjectile projectile,
        PendingProjectileAlign pending,
        int cursor)
    {
        var point = pending.Align.EffectPosition;
        var zero = new ReplayVector3(0.0f, 0.0f, 0.0f);

        SetVector(projectile.AbsOrigin, point);
        SetVector(projectile.ExplodeEffectOrigin, point);
        SetVector(projectile.AbsVelocity, zero);
        SetVector(projectile.BaseVelocity, zero);
        SetVector(projectile.InitialVelocity, zero);
        projectile.TicksAtZeroVelocity = Math.Max(projectile.TicksAtZeroVelocity, 8);

        var rc = 0;
        try
        {
            var molotov = new CMolotovProjectile(projectile.Handle);
            if (molotov.IsValid)
            {
                SetVector(molotov.AbsOrigin, point);
                SetVector(molotov.ExplodeEffectOrigin, point);
                SetVector(molotov.AbsVelocity, zero);
                SetVector(molotov.BaseVelocity, zero);
                molotov.TicksAtZeroVelocity = Math.Max(molotov.TicksAtZeroVelocity, 8);
                if (pending.MolotovPointAlignMode == MolotovPointAlignMode.Detonate)
                {
                    molotov.DetonateTime = Server.CurrentTime;
                    molotov.DetonationRecorded = false;
                    molotov.IsLive = true;
                }
            }
            else
            {
                rc = -2;
            }
        }
        catch
        {
            rc = -8;
        }

        RememberProjectileAlignEvent(
            "molotov_point_align",
            $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} mode={FormatMolotovPointAlignMode(pending.MolotovPointAlignMode)} cursor={cursor} target_tick={pending.MolotovPointAlignTargetTickIndex} effect_tick={pending.Align.EffectTickIndex} rc={rc} point=({point.X:F3},{point.Y:F3},{point.Z:F3})");
    }

    private void FinishProjectileAlign(uint index, PendingProjectileAlign pending, string reason)
    {
        _session.PendingProjectileAlign.Remove(index);
        if (!pending.Matched)
            return;

        var duration = Math.Max(0.0f, pending.LastWriteTime - pending.FirstWriteTime);
        var message =
            $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} kind={pending.Kind} reason={reason} writes={pending.WritesApplied} target={FormatProjectileAlignTicks()} native_birth_rc={pending.LastNativeBirthRc} molotov_point={FormatPendingMolotovPointAlign(pending)} duration={duration.ToString("F3", CultureInfo.InvariantCulture)}";
        RememberProjectileAlignEvent("projectile_align_finished", message);
    }

    private void RememberProjectileAlignEvent(string kind, string message)
    {
        var line =
            $"{Server.CurrentTime.ToString("F3", CultureInfo.InvariantCulture)} {kind} {message}";
        _session.ProjectileAlignLog.Enqueue(line);
        while (_session.ProjectileAlignLog.Count > ProjectileAlignLogMaxEntries)
            _session.ProjectileAlignLog.Dequeue();
    }

    private static ProjectileAlignDecision EvaluateProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileEvent align,
        out string skipReason)
    {
        skipReason = string.Empty;
        if (align.Kind != ReplayProjectileKind.Molotov)
            return ProjectileAlignDecision.Apply;

        if (!HasReliableFireProjectileMetadata(align))
        {
            skipReason = "unreliable_fire_metadata";
            return ProjectileAlignDecision.Skip;
        }
        if (!ReplayVectorIsMeaningful(align.InitialPosition) ||
            !ReplayVectorIsMeaningful(align.InitialVelocity))
        {
            skipReason = "invalid_fire_initial_vector";
            return ProjectileAlignDecision.Skip;
        }

        if (!VectorIsMeaningful(projectile.InitialPosition))
        {
            skipReason = "fire_initial_position_pending";
            return ProjectileAlignDecision.Retry;
        }

        var initialDistance = VectorDistance(projectile.InitialPosition, align.InitialPosition);
        if (initialDistance > FireProjectileAlignMaxInitialPositionDistance)
        {
            skipReason = $"fire_initial_position_distance={initialDistance:F1}";
            return ProjectileAlignDecision.Skip;
        }

        return ProjectileAlignDecision.Apply;
    }

    private static bool HasReliableFireProjectileMetadata(ReplayProjectileEvent align)
    {
        if (align.EffectConfidence < 0.75f || align.EffectTickIndex < 0)
            return false;
        if (!ReplayVectorIsMeaningful(align.EffectPosition))
            return false;

        return align.EffectSource.Equals("inferno_start_burn_event", StringComparison.OrdinalIgnoreCase) ||
               align.EffectSource.Equals("molotov_detonation_event", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindProjectileAlignEvent(
        IReadOnlyList<ReplayProjectileEvent> events,
        int start,
        int cursor,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        const int MaxCursorDistance = 96;
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = Math.Max(start, 0); i < events.Count; i++)
        {
            var candidate = events[i];
            if (candidate.Kind != kind)
                continue;
            if (!ProjectileWeaponDefMatches(kind, weaponDefIndex, candidate.WeaponDefIndex))
                continue;

            var diff = Math.Abs((int)candidate.TickIndex - cursor);
            if (diff < bestDistance)
            {
                best = i;
                bestDistance = diff;
            }
            if ((int)candidate.TickIndex > cursor + MaxCursorDistance)
                break;
        }

        return bestDistance <= MaxCursorDistance ? best : -1;
    }

    private static bool ProjectileWeaponDefMatches(
        ReplayProjectileKind kind,
        int liveWeaponDefIndex,
        int replayWeaponDefIndex)
    {
        if (liveWeaponDefIndex <= 0 || replayWeaponDefIndex <= 0)
            return true;
        if (liveWeaponDefIndex == replayWeaponDefIndex)
            return true;

        // CS2 commonly exposes incendiary projectiles under the same molotov
        // projectile class. Treat 46/48 as the same projectile kind for align,
        // while still preparing the bot with the exact replay weapon def.
        return kind == ReplayProjectileKind.Molotov &&
               liveWeaponDefIndex is 46 or 48 &&
               replayWeaponDefIndex is 46 or 48;
    }

    private static bool TryGetProjectileThrowerSlot(CBaseCSGrenadeProjectile projectile, out int slot)
    {
        slot = -1;
        var thrower = projectile.Thrower.Value;
        if (thrower is not { IsValid: true })
            return false;

        foreach (var player in FindTeamPlayers())
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.Handle == thrower.Handle)
            {
                slot = player.Slot;
                return true;
            }
        }

        return false;
    }

    private static void SetVector(Vector? vector, ReplayVector3 value)
    {
        if (vector == null)
            return;
        vector.X = value.X;
        vector.Y = value.Y;
        vector.Z = value.Z;
    }

    private static float VectorDistance(Vector? vector, ReplayVector3 value)
    {
        if (vector == null)
            return float.PositiveInfinity;
        var dx = vector.X - value.X;
        var dy = vector.Y - value.Y;
        var dz = vector.Z - value.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool VectorIsMeaningful(Vector? value)
        => value != null &&
           float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           (MathF.Abs(value.X) > float.Epsilon ||
            MathF.Abs(value.Y) > float.Epsilon ||
            MathF.Abs(value.Z) > float.Epsilon);

    private static bool ReplayVectorIsMeaningful(ReplayVector3 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           (MathF.Abs(value.X) > float.Epsilon ||
            MathF.Abs(value.Y) > float.Epsilon ||
            MathF.Abs(value.Z) > float.Epsilon);

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (!_mapActive || _lifecycleResetInProgress)
            return;

        if (_session.PendingProjectileAlign.Remove(entity.Index, out var pending) &&
            pending.MolotovPointAlignArmed &&
            !pending.MolotovPointAlignApplied)
        {
            RememberProjectileAlignEvent(
                "molotov_point_align_deleted",
                $"slot={pending.Slot} event={pending.EventIndex} projectile={pending.Index} target_tick={pending.MolotovPointAlignTargetTickIndex} effect_tick={pending.Align.EffectTickIndex}");
        }

    }
}
