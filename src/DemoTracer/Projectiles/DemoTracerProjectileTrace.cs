/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private void TraceProjectileState(
        CBaseCSGrenadeProjectile projectile,
        ReplayProjectileKind kind,
        string phase,
        int spawnTick,
        float observedSpawnTime)
    {
        if (!_session.ProjectileTraceEnabled)
            return;

        try
        {
            // Copy values now: CSS vectors/timers are native-backed views, not
            // immutable snapshots. A spawn-listener sample may be pre-init and
            // the first_physics samples run before the shared native movement
            // function, before its substeps and collision handling.
            var fire = kind == ReplayProjectileKind.Molotov
                ? new CMolotovProjectile(projectile.Handle)
                : null;
            var fireState = fire == null ? string.Empty :
                $" inc={fire.IsIncGrenade} detonated={fire.Detonated} still_since={fire.StillTimer.Timestamp:F6}";
            RememberProjectileAlignEvent(
                "projectile_trace",
                $"phase={phase} projectile={projectile.Index} entity_handle={projectile.EntityHandle.Raw} kind={kind} server_tick={Server.TickCount} observed_spawn_tick={spawnTick} delay={(Server.CurrentTime - observedSpawnTime):F6} spawn_time={projectile.SpawnTime:F6} age={(Server.CurrentTime - projectile.SpawnTime):F6} deadline={projectile.DetonateTime:F6} next_think={projectile.NextThinkTick} bounces={projectile.Bounces} zero_ticks={projectile.TicksAtZeroVelocity} pos={FormatProjectileVector(projectile.AbsOrigin)} vel={FormatProjectileVector(projectile.AbsVelocity)} init_pos={FormatProjectileVector(projectile.InitialPosition)} init_vel={FormatProjectileVector(projectile.InitialVelocity)} base_vel={FormatProjectileVector(projectile.BaseVelocity)} spin={FormatProjectileVector(projectile.GrenadeSpin)} last_normal={FormatProjectileVector(projectile.LastHitSurfaceNormal)}{fireState}");
        }
        catch (Exception ex)
        {
            // Optional diagnostics must never prevent normal playback.
            RememberProjectileAlignEvent(
                "projectile_trace_unavailable",
                $"phase={phase} kind={kind} error=\"{EscapeConsoleString(ex.Message)}\"");
        }
    }
}
