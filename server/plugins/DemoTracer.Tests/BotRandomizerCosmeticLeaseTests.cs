/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;

namespace DemoTracer.Tests;

public sealed class BotRandomizerCosmeticLeaseTests
{
    [Fact]
    public void ContractRequiresReplayPlanProviderV2()
    {
        Assert.Equal(2, BotRandomizerContract.ApiVersion);
        Assert.Equal("botrandomizer:replay-cosmetic-plan:v2", BotRandomizerContract.Capability);
    }

    [Fact]
    public void SnapshotRetainsADeepCopyOfTheCompletePlan()
    {
        var source = CompletePlan();
        var snapshot = new DemoTracerBotRandomizerPlanSnapshot();

        snapshot.Activate("token", "epoch", [source]);
        source.Agent.ModelPath = "mutated";
        source.Weapons[0].PaintKit = 1;
        source.Weapons[0].Stickers[0].StickerId = 2;

        Assert.True(snapshot.TryBuildRetainedPlan(3, 76561198000000001, out var retained));
        Assert.Equal("agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl", retained.Agent.ModelPath);
        Assert.Equal(BotRandomizerReplayTeamPolicy.CounterTerrorist, retained.SpawnTeam);
        Assert.Equal((uint)309, retained.Weapons[0].PaintKit);
        Assert.Equal((uint)661, retained.Weapons[0].Stickers[0].StickerId);
        Assert.Equal((uint)37, retained.Weapons[0].Keychains[0].KeychainId);
        Assert.NotSame(source, retained);
        Assert.NotSame(source.Weapons[0], retained.Weapons[0]);
    }

    [Fact]
    public void SnapshotRejectsTheWrongReplayIdentity()
    {
        var snapshot = new DemoTracerBotRandomizerPlanSnapshot();
        snapshot.Activate("token", "epoch", [CompletePlan()]);

        Assert.False(snapshot.TryGet(3, 76561198000000002, out _));
    }

    [Fact]
    public void WeaponsAndKnifeRequireConstructionTimePrebuild()
    {
        var plan = CompletePlan();
        Assert.True(DemoTracerPlugin.RequestsRequireReplayPrebuild([plan]));

        plan.Knife = null;
        plan.Weapons = [];
        Assert.False(DemoTracerPlugin.RequestsRequireReplayPrebuild([plan]));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void PlanFenceTracksWritableOrAlreadyAcceptedPawn(
        bool canWrite,
        bool alreadyAccepted,
        bool expected)
        => Assert.Equal(
            expected,
            DemoTracerPlugin.ShouldHoldBotRandomizerCosmeticLease(canWrite, alreadyAccepted));

    [Fact]
    public void FailedReplacementKeepsTheOldPlanExceptWhenProviderLostIt()
    {
        Assert.True(DemoTracerPlugin.ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(true, "invalid_plan"));
        Assert.False(DemoTracerPlugin.ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(true, "lease_not_found"));
        Assert.False(DemoTracerPlugin.ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(
            true,
            "invalid_spawn_team:3:2:3"));
        Assert.False(DemoTracerPlugin.ShouldRetainActiveBotRandomizerLeaseAfterSyncFailure(false, "invalid_plan"));
    }

    [Theory]
    [InlineData("invalid_spawn_team:3:2:3")]
    [InlineData("stale_incarnation:3")]
    [InlineData("slot_not_managed:3")]
    public void RosterTopologyFailuresAbortInsteadOfRetryingTheOldPlan(string reason)
        => Assert.True(DemoTracerPlugin.IsBotRandomizerRosterTopologyFailure(reason));

    [Theory]
    [InlineData("invalid_plan")]
    [InlineData("lease_not_found")]
    [InlineData("")]
    public void NonTopologyFailuresDoNotAbortTheReplayRound(string reason)
        => Assert.False(DemoTracerPlugin.IsBotRandomizerRosterTopologyFailure(reason));

    private static BotRandomizerReplayCosmeticPlan CompletePlan()
        => new()
        {
            Slot = 3,
            Incarnation = 7,
            SubjectSteamId = 76561198000000001,
            SpawnTeam = BotRandomizerReplayTeamPolicy.CounterTerrorist,
            Agent = new BotRandomizerAgentPlan
            {
                Mode = BotRandomizerAgentPlanMode.ReplayModel,
                ItemDefinitionIndex = 5036,
                ModelPath = "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl"
            },
            Knife = new BotRandomizerReplayItem
            {
                ItemDefinitionIndex = 507,
                PaintKit = 38,
                PaintSeed = 12,
                PaintWear = 0.04f,
                ItemId = 1234,
                CustomName = "demo knife"
            },
            Gloves = new BotRandomizerReplayItem
            {
                ItemDefinitionIndex = 5030,
                PaintKit = 10018,
                PaintSeed = 4,
                PaintWear = 0.1f
            },
            MusicKit = 3,
            Weapons =
            [
                new BotRandomizerReplayWeapon
                {
                    ItemDefinitionIndex = 60,
                    PaintKit = 309,
                    PaintSeed = 42,
                    PaintWear = 0.07f,
                    Quality = 9,
                    StattrakCounter = 88,
                    PaintUsesLegacyModel = false,
                    Stickers =
                    [
                        new BotRandomizerReplaySticker
                        {
                            Slot = 0,
                            StickerId = 661,
                            Schema = 0,
                            Wear = 0,
                            OffsetX = 0,
                            OffsetY = 0
                        }
                    ],
                    Keychains =
                    [
                        new BotRandomizerReplayKeychain
                        {
                            Slot = 0,
                            KeychainId = 37,
                            Seed = 123,
                            OffsetX = 1,
                            OffsetY = 2,
                            OffsetZ = 3
                        }
                    ]
                }
            ]
        };
}
