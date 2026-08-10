/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class RuntimeCompatibilityTests
{
    [Theory]
    [InlineData("1.41.6.9", true)]
    [InlineData("1.41.7.0", true)]
    [InlineData("1.41.7.2", true)]
    [InlineData("1.41.7.3", true)]
    [InlineData("1.41.7.4", true)]
    [InlineData("1.41.7.5", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void ManagedSchemaWritesFailClosedOutsideVerifiedPatchRange(
        string patch,
        bool expected)
    {
        Assert.Equal(expected, ReplayRuntimePolicy.IsManagedSchemaPatchSupported(patch));
    }

    [Fact]
    public void ManagedSchemaPatchProbeFallsBackFromHostGameDirectoryToPluginAncestors()
    {
        var csgoDirectory = Path.Combine(
            Path.GetTempPath(),
            "demotracer-managed-schema-test",
            "game",
            "csgo");
        var assemblyLocation = Path.Combine(
            csgoDirectory,
            "addons",
            "counterstrikesharp",
            "plugins",
            "DemoTracer",
            "DemoTracer.dll");

        var candidates = ReplayRuntimePolicy.ManagedSchemaSteamInfCandidates(
            "csgo",
            assemblyLocation);

        Assert.Contains(Path.Combine(csgoDirectory, "steam.inf"), candidates);
    }

    [Theory]
    [InlineData(false, true, 70, false)]
    [InlineData(true, false, 70, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 70, true)]
    public void MusicKitRequiresCosmeticOptInAndSupportedRuntime(
        bool cosmeticsEnabled,
        bool runtimeSupported,
        int musicKitId,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayRuntimePolicy.ShouldApplyMusicKit(
                cosmeticsEnabled,
                runtimeSupported,
                musicKitId));
    }

    [Fact]
    public void PawnEquipmentRequiresPawnItemServicesAndControllerMirrorsToMatch()
    {
        Assert.True(ReplayRuntimePolicy.PawnEquipmentStateMatches(
            expectedArmor: 100,
            expectedHelmet: true,
            expectedDefuser: true,
            pawnArmor: 100,
            itemServicesAvailable: true,
            itemServicesHelmet: true,
            itemServicesDefuser: true,
            controllerArmor: 100,
            controllerHelmet: true,
            controllerDefuser: true));

        Assert.False(ReplayRuntimePolicy.PawnEquipmentStateMatches(
            expectedArmor: 100,
            expectedHelmet: true,
            expectedDefuser: true,
            pawnArmor: 100,
            itemServicesAvailable: true,
            itemServicesHelmet: true,
            itemServicesDefuser: true,
            controllerArmor: 0,
            controllerHelmet: false,
            controllerDefuser: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ScoreboardFlairFollowsSupportedReplayIdentity(
        bool identitySupportsFlair,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReplayRuntimePolicy.ShouldApplyScoreboardFlair(identitySupportsFlair));
    }
}
