/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private ProjectilePhysicsHook? _projectilePhysicsHook;
    private string ProjectilePhysicsHookStatus => _projectilePhysicsHook?.Status ?? "not_initialized";

    private void InstallProjectilePhysicsHook()
    {
        _projectilePhysicsHook = new(Addresses.ServerPath, ProcessProjectileFirstPhysics);
        _projectilePhysicsHook.Install();
        Server.PrintToConsole($"dtr: projectile birth boundary {ProjectilePhysicsHookStatus}");
        if (_projectilePhysicsHook.Ready)
            Logger.LogInformation("Projectile birth boundary {Status}", ProjectilePhysicsHookStatus);
        else
            Logger.LogWarning("Projectile alignment unavailable: {Status}", ProjectilePhysicsHookStatus);
    }

    private void OnProjectileEntityDeleted(CEntityInstance entity)
        => _session.ProjectileBirths.Remove(entity.Handle);
}
