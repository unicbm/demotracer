/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ProjectilePhysicsModuleTests
{
    private static readonly string Game = Path.Combine(Path.GetTempPath(), "dtr-module-test", "game", "csgo");
    private static readonly string Server = Path.Combine(Game, "bin", "win64", "server.dll");
    private static readonly string Metamod = Path.Combine(Game, "addons", "metamod", "bin", "win64", "server.dll");

    [Fact]
    public void SelectsGameServerWhenMetamodHasTheSameFileName()
    {
        Assert.Equal(1, ProjectilePhysicsHook.FindGameServerModuleIndex([Metamod, Server], Server));
        Assert.Equal(0, ProjectilePhysicsHook.FindGameServerModuleIndex([Server, Metamod], Server));
    }

    [Fact]
    public void WindowsModulePathComparisonIgnoresCaseAndNormalizesSegments()
        => Assert.Equal(0, ProjectilePhysicsHook.FindGameServerModuleIndex(
            [Path.Combine(Game.ToUpperInvariant(), "bin", "win64", ".", "SERVER.DLL")], Server));

    [Fact]
    public void DoesNotFallBackToMetamodOrAnotherGameInstallation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProjectilePhysicsHook.FindGameServerModuleIndex([Metamod], Server));
        Assert.Throws<InvalidOperationException>(() =>
            ProjectilePhysicsHook.FindGameServerModuleIndex([Server], Path.Combine(Game + "-other", "bin", "win64", "server.dll")));
    }

    [Fact]
    public void RejectsAnAmbiguousExactPath()
        => Assert.Throws<InvalidOperationException>(() =>
            ProjectilePhysicsHook.FindGameServerModuleIndex([Server, Server], Server));
}
