/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer.Tests;

public sealed class BotRandomizerCosmeticLeaseTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("_cosmeticAgentsEnabled")]
    [InlineData("_cosmeticWeaponsEnabled")]
    [InlineData("_cosmeticKnivesEnabled")]
    [InlineData("_cosmeticGlovesEnabled")]
    [InlineData("_cosmeticNamesEnabled")]
    [InlineData("_stickerAlignEnabled")]
    [InlineData("_charmAlignEnabled")]
    [InlineData("_weaponAlignEnabled")]
    [InlineData("_cosmeticAlignEnabled")]
    public void RetainedDemoEvidenceHonorsCurrentTogglesAndCanBeEnabledAgain(string flag)
    {
        var (plugin, _, snapshot) = CreateRetainedPlugin();
        var original = JsonSerializer.Serialize(Assert.Single(BuildPlans(plugin)));
        typeof(DemoTracerPlugin).GetField(flag, PrivateInstance)!.SetValue(plugin, false);
        var plans = BuildPlans(plugin);
        if (flag == "_cosmeticAlignEnabled")
        {
            Assert.Empty(plans);
            // Turning every claim off releases the accepted plan, but keeps
            // the original demo evidence for a later on command.
            typeof(DemoTracerPlugin).GetField("_botRandomizerLeaseTransitionDepth", PrivateInstance)!.SetValue(plugin, 1);
            typeof(DemoTracerPlugin).GetMethod("ReleaseBotRandomizerCosmeticLease", PrivateInstance,
                [typeof(string), typeof(bool)])!.Invoke(plugin, ["no_replay_plans", false]);
        }
        else
        {
            var plan = Assert.Single(plans);
            switch (flag)
            {
                case "_cosmeticAgentsEnabled": Assert.Equal(BotRandomizerAgentPlanMode.Randomized, plan.Agent.Mode); break;
                case "_cosmeticWeaponsEnabled": Assert.Empty(plan.Weapons); break;
                case "_cosmeticKnivesEnabled": Assert.Null(plan.Knife); break;
                case "_cosmeticGlovesEnabled": Assert.Null(plan.Gloves); break;
                case "_cosmeticNamesEnabled":
                    Assert.Null(Assert.Single(plan.Weapons).CustomName);
                    Assert.Null(plan.Knife!.CustomName);
                    break;
                case "_stickerAlignEnabled": Assert.Empty(Assert.Single(plan.Weapons).Stickers); break;
                case "_charmAlignEnabled": Assert.Empty(Assert.Single(plan.Weapons).Keychains); break;
                case "_weaponAlignEnabled":
                    Assert.Empty(plan.Weapons);
                    Assert.Null(plan.Knife);
                    Assert.Null(plan.Gloves);
                    break;
            }
        }
        snapshot.Invalidate(); // Re-enabling must rebuild from source, not the old accepted plan.
        typeof(DemoTracerPlugin).GetField(flag, PrivateInstance)!.SetValue(plugin, true);
        Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(BuildPlans(plugin))));
    }

    [Theory]
    [InlineData(8UL, 3)]
    [InlineData(7UL, 2)]
    public void RetainedEvidenceIsDiscardedWhenBotIncarnationOrSideChanges(ulong incarnation, byte team)
    {
        var (plugin, provider, _) = CreateRetainedPlugin();
        provider.Incarnation = incarnation;
        provider.Team = team;
        Assert.Empty(BuildPlans(plugin));
        provider.Incarnation = 7;
        provider.Team = 3;
        Assert.Empty(BuildPlans(plugin)); // Stale evidence cannot reappear if a slot later matches again.
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

        Assert.True(snapshot.TryGet(3, 76561198000000001, out var retained));
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AtomicUnloadInvalidatesThePlanCacheWithoutReleasingTheProvider(int transitionDepth)
    {
        var (plugin, _, snapshot) = CreateRetainedPlugin();
        var signature = typeof(DemoTracerPlugin).GetField("_botRandomizerLeaseSignature", PrivateInstance)!;
        signature.SetValue(plugin, "accepted_plan");
        typeof(DemoTracerPlugin).GetField("_botRandomizerLeaseTransitionDepth", PrivateInstance)!
            .SetValue(plugin, transitionDepth);
        var session = typeof(DemoTracerPlugin).GetField("_session", PrivateInstance)!.GetValue(plugin)!;
        var synced = (HashSet<int>)session.GetType().GetProperty("CosmeticSyncedSlots")!.GetValue(session)!;
        synced.Add(3);

        typeof(DemoTracerPlugin).GetMethod("ReleaseBotRandomizerCosmeticLease", PrivateInstance,
            [typeof(string)])!.Invoke(plugin, ["unload_all"]);

        // A repeated dtr_go may load exactly the same players and cosmetics.
        // It must replace the plan again to rebuild retention and invalidate
        // old provider callbacks, while keeping ownership during the transition.
        Assert.Equal(string.Empty, signature.GetValue(plugin));
        Assert.Empty(BuildPlans(plugin));
        Assert.Empty(synced);
        Assert.Equal("token", snapshot.Token);
        Assert.True(snapshot.TryGet(3, 76561198000000001, out _));
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

    private static BotRandomizerReplayCosmeticPlan[] BuildPlans(DemoTracerPlugin plugin)
        => (BotRandomizerReplayCosmeticPlan[])typeof(DemoTracerPlugin)
            .GetMethod("BuildBotRandomizerReplayPlans", PrivateInstance)!.Invoke(plugin, null)!;

    private static (DemoTracerPlugin, ManagedBotProvider, DemoTracerBotRandomizerPlanSnapshot) CreateRetainedPlugin()
    {
        var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
        typeof(DemoTracerPlugin).GetField("_replayEquipment", PrivateInstance)!.SetValue(plugin, ReplayEquipmentCatalog.Empty);
        foreach (var name in new[] { "_session", "_botRandomizerBridge", "_botRandomizerLease",
                     "_retainedBotRandomizerCosmeticSlots", "_legacyCosmeticPaints" })
        {
            var field = typeof(DemoTracerPlugin).GetField(name, PrivateInstance)!;
            field.SetValue(plugin, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }
        foreach (var name in new[] { "_cosmeticAlignEnabled", "_cosmeticAgentsEnabled", "_cosmeticWeaponsEnabled",
                     "_cosmeticKnivesEnabled", "_cosmeticGlovesEnabled", "_cosmeticNamesEnabled",
                     "_stickerAlignEnabled", "_charmAlignEnabled", "_weaponAlignEnabled" })
            typeof(DemoTracerPlugin).GetField(name, PrivateInstance)!.SetValue(plugin, true);
        var session = typeof(DemoTracerPlugin).GetField("_session", PrivateInstance)!.GetValue(plugin)!;
        var replays = (IDictionary)session.GetType().GetProperty("LoadedReplays")!.GetValue(session)!;
        var replayType = replays.GetType().GetGenericArguments()[1];
        var replay = Activator.CreateInstance(replayType)!;
        replayType.GetProperty("SteamId")!.SetValue(replay, 76561198000000001UL);
        replayType.GetProperty("ManifestTeam")!.SetValue(replay, CsTeam.CounterTerrorist);
        var cosmetics = replayType.GetProperty("Cosmetics")!;
        cosmetics.SetValue(replay, JsonSerializer.Deserialize("""
            {
              "Agent":{"ModelPath":"demo_agent"},
              "Knife":{"ItemDefIndex":507,"PaintKit":38,"CustomName":"knife"},
              "Glove":{"ItemDefIndex":5030,"PaintKit":10018},
              "Weapons":[{"WeaponDefIndex":60,"PaintKit":309,"CustomName":"rifle",
                "Stickers":[{"Slot":0,"StickerId":661}],"Charms":[{"Slot":0,"CharmId":37}]}]
            }
            """, cosmetics.PropertyType));
        replays.Add(3, replay);
        var snapshot = (DemoTracerBotRandomizerPlanSnapshot)typeof(DemoTracerPlugin)
            .GetField("_botRandomizerLease", PrivateInstance)!.GetValue(plugin)!;
        snapshot.Activate("token", "epoch", [CompletePlan()]);
        typeof(DemoTracerPlugin).GetMethod("RetainLoadedBotRandomizerCosmetics", PrivateInstance)!.Invoke(plugin, null);
        replays.Clear(); // Natural unload releases execution and metadata.
        var bridge = typeof(DemoTracerPlugin).GetField("_botRandomizerBridge", PrivateInstance)!.GetValue(plugin)!;
        var api = DispatchProxy.Create<IBotRandomizerApi, ManagedBotProvider>();
        bridge.GetType().GetField("_api", PrivateInstance)!.SetValue(bridge, api);
        bridge.GetType().GetField("_resolved", PrivateInstance)!.SetValue(bridge, true);
        return (plugin, (ManagedBotProvider)api, snapshot);
    }

    public class ManagedBotProvider : DispatchProxy
    {
        public ulong Incarnation { get; set; } = 7;
        public byte Team { get; set; } = 3;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_ApiVersion") return BotRandomizerContract.ApiVersion;
            if (method.Name != nameof(IBotRandomizerApi.TryGetManagedBot))
                throw new InvalidOperationException($"Unexpected provider call: {method.Name}");
            args![1] = new BotRandomizerManagedBot { Slot = 3, Incarnation = Incarnation, Team = Team };
            return (int)args[0]! == 3;
        }
    }
}
