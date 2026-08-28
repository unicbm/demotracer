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
    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!_mapActive || _lifecycleResetInProgress)
            return;

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

        RememberProjectileAlignEvent(
            "projectile_align_candidate",
            $"projectile={projectile.Index} kind={kind} weapon={weaponDefIndex}");

        var projectileIndex = projectile.Index;
        var projectileHandle = projectile.Handle;
        Server.NextFrame(() => ProcessProjectileAlignCandidate(
            projectileIndex,
            projectileHandle,
            kind,
            weaponDefIndex));
    }

    private void ProcessProjectileAlignCandidate(
        uint projectileIndex,
        IntPtr projectileHandle,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        if (!_mapActive || _lifecycleResetInProgress || !_projectileAlignEnabled)
            return;

        try
        {
            var projectile = new CBaseCSGrenadeProjectile(projectileHandle);
            if (!projectile.IsValid || projectile.Index != projectileIndex)
            {
                RememberProjectileAlignEvent(
                    "projectile_align_skipped",
                    $"projectile={projectileIndex} kind={kind} weapon={weaponDefIndex} reason=entity_invalid_next_frame");
                return;
            }

            TryResolveAndApplyProjectileAlign(projectile, kind, weaponDefIndex);
        }
        catch (Exception ex)
        {
            RememberProjectileAlignEvent(
                "projectile_align_failed",
                $"projectile={projectileIndex} kind={kind} weapon={weaponDefIndex} error=\"{EscapeConsoleString(ex.Message)}\"");
        }
    }

    private bool TryResolveAndApplyProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex)
    {
        if (!_projectileAlignEnabled)
            return false;

        if (!TryResolveProjectileAlign(
                projectile,
                kind,
                weaponDefIndex,
                out var slot,
                out var eventIndex,
                out var align,
                out var failureReason))
        {
            RememberProjectileAlignEvent(
                "projectile_align_skipped",
                $"projectile={projectile.Index} kind={kind} weapon={weaponDefIndex} reason={failureReason}");
            return false;
        }

        var decision = EvaluateProjectileAlign(projectile, align, out var skipReason);
        _session.ProjectileAlignNextBySlot[slot] = eventIndex + 1;
        if (decision == ProjectileAlignDecision.Skip)
        {
            var message =
                $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} reason={skipReason}";
            RememberProjectileAlignEvent("projectile_align_skipped", message);
            return true;
        }

        var liveInitialPosition = FormatProjectileVector(projectile.InitialPosition);
        var liveInitialVelocity = FormatProjectileVector(projectile.InitialVelocity);
        ApplyProjectileBirthAlign(projectile, align);

        RememberProjectileAlignEvent(
            "projectile_align",
            $"slot={slot} event={eventIndex} tick_index={align.TickIndex} projectile={projectile.Index} kind={align.Kind} mode=engine_birth_once live_init_pos={liveInitialPosition} live_init_vel={liveInitialVelocity} init_pos=({align.InitialPosition.X:F3},{align.InitialPosition.Y:F3},{align.InitialPosition.Z:F3}) init_vel=({align.InitialVelocity.X:F3},{align.InitialVelocity.Y:F3},{align.InitialVelocity.Z:F3})");
        return true;
    }

    private bool TryResolveProjectileAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        int weaponDefIndex,
        out int slot,
        out int eventIndex,
        out ReplayProjectileEvent align,
        out string failureReason)
    {
        slot = -1;
        eventIndex = -1;
        align = default;
        failureReason = string.Empty;

        if (!TryGetProjectileThrowerSlot(projectile, out slot, out failureReason))
            return false;
        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) || replay.Projectiles.Length == 0)
        {
            failureReason = $"replay_projectiles_unavailable_slot={slot}";
            return false;
        }

        var state = BotControllerNative.GetReplayState(slot);
        if (!state.Playing)
        {
            failureReason = $"replay_not_playing_slot={slot}";
            return false;
        }

        var next = _session.ProjectileAlignNextBySlot.TryGetValue(slot, out var value) ? value : 0;
        eventIndex = FindProjectileAlignEvent(replay.Projectiles, next, state.Cursor, kind, weaponDefIndex);
        if (eventIndex < 0)
        {
            failureReason = $"event_not_found_slot={slot}_cursor={state.Cursor}_next={next}";
            return false;
        }

        align = replay.Projectiles[eventIndex];
        return true;
    }

    private static void ApplyProjectileAlign(CBaseCSGrenadeProjectile projectile, ReplayProjectileEvent align)
    {
        projectile.Teleport(
            new System.Numerics.Vector3(
                align.InitialPosition.X,
                align.InitialPosition.Y,
                align.InitialPosition.Z),
            null,
            new System.Numerics.Vector3(
                align.InitialVelocity.X,
                align.InitialVelocity.Y,
                align.InitialVelocity.Z));
        SetVector(projectile.InitialPosition, align.InitialPosition);
        SetVector(projectile.InitialVelocity, align.InitialVelocity);
    }

    private static void ApplyProjectileBirthAlign(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileEvent align)
    {
        ApplyProjectileAlign(projectile, align);
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
        if (!ReplayVectorIsMeaningful(align.InitialPosition) ||
            !ReplayVectorIsMeaningful(align.InitialVelocity))
        {
            skipReason = "invalid_initial_vector";
            return ProjectileAlignDecision.Skip;
        }

        var liveBirthPosition = VectorIsMeaningful(projectile.InitialPosition)
            ? projectile.InitialPosition
            : projectile.AbsOrigin;
        if (!VectorIsMeaningful(liveBirthPosition))
        {
            skipReason = "birth_position_unavailable";
            return ProjectileAlignDecision.Skip;
        }

        var initialDistance = VectorDistance(liveBirthPosition, align.InitialPosition);
        if (initialDistance > ProjectileAlignMaxInitialPositionDistance)
        {
            skipReason = $"initial_position_distance={initialDistance:F1}";
            return ProjectileAlignDecision.Skip;
        }

        return ProjectileAlignDecision.Apply;
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

    private static bool TryGetProjectileThrowerSlot(
        CBaseCSGrenadeProjectile projectile,
        out int slot,
        out string failureReason)
    {
        slot = -1;
        failureReason = string.Empty;
        var thrower = projectile.Thrower.Value;
        if (thrower is not { IsValid: true })
        {
            failureReason = "thrower_unavailable";
            return false;
        }

        foreach (var player in FindTeamPlayers())
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is { IsValid: true } && pawn.Handle == thrower.Handle)
            {
                slot = player.Slot;
                return true;
            }
        }

        failureReason = $"thrower_slot_unmapped_handle={thrower.Handle}";
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

    private static string FormatProjectileVector(Vector? vector)
        => vector == null
            ? "unavailable"
            : $"({vector.X:F3},{vector.Y:F3},{vector.Z:F3})";

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

}
