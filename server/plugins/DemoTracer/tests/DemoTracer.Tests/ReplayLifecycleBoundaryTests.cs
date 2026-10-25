/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Text.Json;
using BotRandomizerApi;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer.Tests;

public sealed class ReplayLifecycleBoundaryTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void NaturalBoundaryKeepsCosmeticsWithoutReplayOwnershipAndExplicitUnloadClearsThem()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var replays = GetProperty<IDictionary>(session, "LoadedReplays");
        var replayType = replays.GetType().GetGenericArguments()[1];
        var replay = Activator.CreateInstance(replayType)!;
        replayType.GetProperty("SteamId")!.SetValue(replay, 101UL);
        replayType.GetProperty("ManifestTeam")!.SetValue(replay, CsTeam.CounterTerrorist);
        var cosmetics = replayType.GetProperty("Cosmetics")!;
        cosmetics.SetValue(replay, JsonSerializer.Deserialize("""
            {"Agent":{"ModelPath":"demo_agent"},"Weapons":[{"WeaponDefIndex":60,"PaintKit":309}]}
            """, cosmetics.PropertyType));
        foreach (var flag in new[] { "_cosmeticAlignEnabled", "_cosmeticAgentsEnabled", "_cosmeticWeaponsEnabled", "_weaponAlignEnabled" })
            typeof(DemoTracerPlugin).GetField(flag, PrivateInstance)!.SetValue(plugin, true);
        replays.Add(3, replay);
        var snapshot = GetField<DemoTracerBotRandomizerPlanSnapshot>(plugin, "_botRandomizerLease");
        snapshot.Activate("token", "epoch", [new()
        {
            Slot = 3, Incarnation = 7, SubjectSteamId = 101, SpawnTeam = 3,
            Agent = new() { Mode = BotRandomizerAgentPlanMode.ReplayModel, ModelPath = "demo_agent" },
            Weapons = [new() { ItemDefinitionIndex = 60, PaintKit = 309 }]
        }]);
        var bridge = GetField<object>(plugin, "_botRandomizerBridge");
        var api = DispatchProxy.Create<IBotRandomizerApi, CosmeticProvider>();
        bridge.GetType().GetField("_api", PrivateInstance)!.SetValue(bridge, api);
        bridge.GetType().GetField("_resolved", PrivateInstance)!.SetValue(bridge, true);
        typeof(DemoTracerPlugin).GetField("_botRandomizerLeaseTransitionDepth", PrivateInstance)!.SetValue(plugin, 1);

        // Exercise the actual round-boundary unload after execution has ended.
        // There are no native buffers in this fixture; only the accepted plan.
        typeof(DemoTracerPlugin).GetMethod("StopAndUnloadLoaded", PrivateInstance,
            [typeof(bool), typeof(bool), typeof(bool)])!.Invoke(plugin, [false, true, true]);
        Assert.Empty(replays);
        Assert.False(GetProperty<ReplaySlotRegistry>(session, "ReplaySlots").HasAnyState);
        var retained = Assert.Single(Invoke<BotRandomizerReplayCosmeticPlan[]>(plugin, "BuildBotRandomizerReplayPlans"));
        Assert.Equal("demo_agent", retained.Agent.ModelPath);
        Assert.Equal(309U, Assert.Single(retained.Weapons).PaintKit);
        Assert.Single(Invoke<BotRandomizerReplayCosmeticPlan[]>(plugin, "BuildBotRandomizerReplayPlans"));

        // Explicit unload inside the same transition cannot resurrect the plan.
        typeof(DemoTracerPlugin).GetMethod("StopAndUnloadLoaded", PrivateInstance, Type.EmptyTypes)!.Invoke(plugin, null);
        Assert.Empty(Invoke<BotRandomizerReplayCosmeticPlan[]>(plugin, "BuildBotRandomizerReplayPlans"));

        // Stop-all can retain loaded metadata inside an atomic transition.
        // That metadata must not revive the just-released cosmetic plan.
        replays.Add(3, replay);
        GetProperty<HashSet<int>>(session, "CosmeticSyncedSlots").Add(3);
        typeof(DemoTracerPlugin).GetMethod("ReleaseBotRandomizerCosmeticLease", PrivateInstance,
            [typeof(string)])!.Invoke(plugin, ["stop_all"]);
        Assert.Empty(Invoke<BotRandomizerReplayCosmeticPlan[]>(plugin, "BuildBotRandomizerReplayPlans"));
    }

    [Fact]
    public void PlayoffIgnoresOrdinaryBotsButKeepsTrackedIdentityValidation()
    {
        var plugin = CreatePlugin();
        var replays = GetProperty<IDictionary>(GetField<object>(plugin, "_session"), "LoadedReplays");
        var replay = Activator.CreateInstance(replays.GetType().GetGenericArguments()[1])!;
        replays.Add(3, replay); // Tracked but missing SteamID must still reach roster validation.
        var retained = GetField<IDictionary>(plugin, "_retainedBotHiderPresentation");
        var evidence = Activator.CreateInstance(retained.GetType().GetGenericArguments()[1],
            [4, "demo player", 101UL, 0, null, null, null]);
        retained.Add(4, evidence);

        Assert.True(Invoke<bool>(plugin, "HasPlayoffReplayIdentity", 3));
        Assert.True(Invoke<bool>(plugin, "HasPlayoffReplayIdentity", 4));
        Assert.False(Invoke<bool>(plugin, "HasPlayoffReplayIdentity", 5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayoffCountsAndRecordsOnlyStartedRoundsIncludingFallback(bool prepared)
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var plan = GetProperty<ReplayPlanState>(session, "Plan");
        plan.PlayoffPrepared = prepared;
        plan.PlayoffPreparedTRound = 4;
        plan.PlayoffPreparedCtRound = 6;
        var replays = GetProperty<IDictionary>(session, "LoadedReplays");
        var replayType = replays.GetType().GetGenericArguments()[1];
        var slots = GetProperty<ReplaySlotRegistry>(session, "ReplaySlots");
        foreach (var (slot, steamId, team) in new[] { (3, 101UL, CsTeam.Terrorist), (4, 102UL, CsTeam.CounterTerrorist) })
        {
            var replay = Activator.CreateInstance(replayType)!;
            replayType.GetProperty("SteamId")!.SetValue(replay, steamId);
            replayType.GetProperty("ManifestTeam")!.SetValue(replay, team);
            replays.Add(slot, replay);
            slots.LoadAndClaim(slot);
        }
        typeof(DemoTracerPlugin).GetField("_loadedRoundSources", PrivateInstance)!
            .SetValue(plugin, new PlayoffRoundSources(4, 6, "test"));
        var history = GetField<PlayoffRoundHistory>(plugin, "_playoffHistory");
        var uses = GetField<Dictionary<(string, string, int), int>>(history, "_uses");

        // A queued or failed attempt cannot consume the prepared selection.
        Invoke<object?>(plugin, "CompletePlayoffRoundStart", 0);
        Assert.Equal(0, plan.PlayoffRoundIndex);
        Assert.Equal(prepared, plan.PlayoffPrepared);
        Assert.Empty(uses);

        // The delayed success callback counts only the side that actually ran.
        slots.MarkPlaying(3);
        Invoke<object?>(plugin, "CompletePlayoffRoundStart", 1);
        Assert.Equal(1, plan.PlayoffRoundIndex);
        Assert.False(plan.PlayoffPrepared);
        Assert.Equal(1, uses[("t", "101", 4)]);
        Assert.False(uses.ContainsKey(("ct", "102", 6)));

        // Reusing the same loaded source in a later round also counts its use.
        slots.MarkPlaying(4);
        Invoke<object?>(plugin, "CompletePlayoffRoundStart", 2);
        Assert.Equal(2, plan.PlayoffRoundIndex);
        Assert.Equal(2, uses[("t", "101", 4)]);
        Assert.Equal(1, uses[("ct", "102", 6)]);
    }

    public class CosmeticProvider : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_ApiVersion")
                return BotRandomizerContract.ApiVersion;
            if (method.Name != nameof(IBotRandomizerApi.TryGetManagedBot))
                throw new InvalidOperationException($"Unexpected provider call: {method.Name}");
            args![1] = new BotRandomizerManagedBot { Slot = 3, Incarnation = 7, Team = 3 };
            return (int)args[0]! == 3;
        }
    }

    [Fact]
    public void CleanupTargetsIncludeReleasedAndWarmBuffersButNotUntrackedSlots()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var slots = GetProperty<ReplaySlotRegistry>(session, "ReplaySlots");
        var warm = GetProperty<HashSet<int>>(session, "WarmReplayBufferSlots");
        slots.LoadAndClaim(2);
        slots.MarkPlaying(2);
        slots.Release(2); // Handoff releases input, but DTR still owns its buffer.
        slots.LoadAndClaim(5); // A partially loaded round must also be cleaned up.
        warm.UnionWith([2, 8]);

        Assert.Equal([2, 5, 8], Invoke<int[]>(plugin, "ReplayBufferSlots").Order());
        Assert.True(Invoke<bool>(plugin, "IsDisconnectingReplaySlot", 2));
        Assert.True(Invoke<bool>(plugin, "IsDisconnectingReplaySlot", 8));
        Assert.False(Invoke<bool>(plugin, "IsDisconnectingReplaySlot", 11));
    }

    [Fact]
    public void PendingPlansDoNotClaimOtherProvidersBots()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var plan = GetProperty<ReplayPlanState>(session, "Plan");
        var slots = GetProperty<ReplaySlotRegistry>(session, "ReplaySlots");
        plan.Armed = true;
        plan.ArmedPrepared = true;
        plan.SequenceActive = true;

        Assert.False(Invoke<bool>(plugin, "IsDemoTracerBot", 4));
        slots.LoadAndClaim(4);
        Assert.True(Invoke<bool>(plugin, "IsDemoTracerBot", 4));
        slots.Release(4);
        Assert.False(Invoke<bool>(plugin, "IsDemoTracerBot", 4));
        Assert.True(slots.IsLoaded(4));
    }

    [Theory]
    [InlineData("Immediate")]
    [InlineData("Handoff")]
    [InlineData("Finished")]
    public void ReleasingRetainedBufferDoesNotRunExecutionCleanupAgain(string releaseKind)
    {
        var plugin = CreatePlugin();
        var kind = Enum.Parse(
            typeof(DemoTracerPlugin).GetNestedType("ReplayReleaseKind", BindingFlags.NonPublic)!,
            releaseKind);
        var session = GetField<object>(plugin, "_session");
        var slots = GetProperty<ReplaySlotRegistry>(session, "ReplaySlots");
        slots.LoadAndClaim(4);
        slots.MarkPlaying(4);
        Assert.True(slots.Release(4)); // Execution already handed to another provider.
        var releasedEpoch = slots.CurrentEpoch(4);
        var prepared = GetProperty<HashSet<int>>(session, "WeaponLoadoutSyncedSlots");
        prepared.Add(4);

        // The actual release entry point must be safe without an engine: it
        // must neither unlock this slot nor clear a later owner's native input,
        // equipment or buy plan simply because DTR retained its old buffer.
        Invoke<object?>(plugin, "ReleaseReplaySlot", 4, "dispose_after_handoff", kind);
        Invoke<object?>(plugin, "ReleaseReplaySlot", 4, "repeat_dispose", kind);

        Assert.True(slots.IsLoaded(4));
        Assert.False(slots.IsOwned(4));
        Assert.Equal(releasedEpoch, slots.CurrentEpoch(4));
        Assert.Contains(4, prepared);
        Assert.True(slots.Unload(4));
        Invoke<object?>(plugin, "ReleaseReplaySlot", 4, "repeat_after_unload", kind);
    }

    [Fact]
    public void SpawnAssignmentWaitsForTheWholeRoundSpawnBatch()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        GetProperty<ReplaySlotRegistry>(session, "ReplaySlots").LoadAndClaim(4);
        session.GetType().GetProperty("RoundSpawnsPending")!.SetValue(session, true);

        // A bot's player_spawn can precede the human's respawn. Neither the
        // event path nor a direct start may place bots or latch completion yet.
        Invoke<object?>(plugin, "ScheduleInitialRoundSpawnAssignment");
        object?[] arguments = [null];
        Assert.False(Invoke<bool>(plugin, "TryAssignInitialRoundSpawns", arguments));
        Assert.Contains("round_start", Assert.IsType<string>(arguments[0]));
        Assert.False(GetProperty<bool>(session, "InitialSpawnAssignmentComplete"));
        Assert.False(GetProperty<bool>(session, "InitialSpawnAssignmentScheduled"));

        // Loading a new source during prestart invalidates placement state but
        // must not reopen the gate while the engine is still respawning players.
        Invoke<object?>(plugin, "InvalidateInitialSpawnAssignment");
        Assert.True(GetProperty<bool>(session, "RoundSpawnsPending"));
        Assert.False(Invoke<bool>(plugin, "TryAssignInitialRoundSpawns", arguments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopCancelsQueuedStartsAndSpawnRetriesBeforeAnotherStartCanBeQueued(bool unload)
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var roundWork = GetField<EpochWorkCoalescer<ReplayRoundWorkKind, long>>(plugin, "_replayRoundWork");
        var previousEpoch = GetField<long>(plugin, "_replayRoundWorkEpoch");
        var freezeToken = GetProperty<int>(session, "FreezePrerollToken");
        var spawnToken = GetProperty<int>(session, "InitialSpawnAssignmentToken");
        Assert.True(roundWork.TrySchedule(ReplayRoundWorkKind.Start, previousEpoch));

        // Exercise the actual stop entry point with no live pawns. Its pending
        // work exists before the first native replay starts, as on freeze end
        // or the deferred respawn path.
        if (unload)
            typeof(DemoTracerPlugin).GetMethod("StopAndUnloadLoaded", PrivateInstance, Type.EmptyTypes)!.Invoke(plugin, null);
        else
            Invoke<object?>(plugin, "StopLoadedReplaySlots", "test_stop");

        var nextEpoch = GetField<long>(plugin, "_replayRoundWorkEpoch");
        Assert.True(nextEpoch > previousEpoch);
        Assert.True(GetProperty<int>(session, "FreezePrerollToken") > freezeToken);
        Assert.True(GetProperty<int>(session, "InitialSpawnAssignmentToken") > spawnToken);
        Assert.True(roundWork.TrySchedule(ReplayRoundWorkKind.Start, nextEpoch));
        Assert.False(roundWork.TryConsume(ReplayRoundWorkKind.Start, previousEpoch));
        Assert.True(roundWork.TryConsume(ReplayRoundWorkKind.Start, nextEpoch));
    }

    [Fact]
    public void UnloadingSessionDiscardsOldExecutionButKeepsWarmBuffersAndNextPlan()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var slots = GetProperty<ReplaySlotRegistry>(session, "ReplaySlots");
        var oldEpoch = slots.LoadAndClaim(4).Epoch;
        slots.Release(4);
        var plan = GetProperty<ReplayPlanState>(session, "Plan");
        plan.SequenceActive = true;
        var warm = GetProperty<HashSet<int>>(session, "WarmReplayBufferSlots");
        warm.Add(4);
        var inventories = GetProperty<Dictionary<int, ReplayInventoryTimeline>>(session, "ReplayInventoryBySlot");
        inventories[4] = new ReplayInventoryTimeline([]);
        var equipment = GetProperty<ReplayPawnEquipmentSyncTracker>(session, "PawnEquipmentSync");
        var identity = new ReplayPawnEquipmentIdentity(4, 0x8004, 1, 76561198000000004);
        equipment.MarkSynced(4, identity);
        GetProperty<HashSet<int>>(session, "FreezePrerollSlots").Add(4);

        Invoke<object?>(plugin, "ClearLoadedReplaySession");
        Invoke<object?>(plugin, "ClearLoadedReplaySession");

        Assert.False(slots.HasAnyState);
        Assert.False(slots.IsCurrentEpoch(4, oldEpoch));
        Assert.Empty(inventories);
        Assert.Empty(GetProperty<HashSet<int>>(session, "FreezePrerollSlots"));
        Assert.False(equipment.IsSynced(4, identity));
        Assert.Contains(4, warm);
        Assert.True(plan.SequenceActive);
        Assert.True(slots.LoadAndClaim(4).Epoch > oldEpoch);
    }

    [Fact]
    public void AutomaticLoopDoesNotReplaceAnIndependentVoiceTest()
    {
        var plugin = CreatePlugin();
        var playbackField = typeof(DemoTracerPlugin).GetField("_voiceTestPlayback", PrivateInstance)!;
        var manualPlayback = RuntimeHelpers.GetUninitializedObject(playbackField.FieldType);
        playbackField.SetValue(plugin, manualPlayback);
        var anchorType = typeof(DemoTracerPlugin).GetNestedType("ReplayStartAnchor", BindingFlags.NonPublic)!;

        var result = Invoke<string>(plugin, "TryStartLoadedAutoVoicePlayback",
            Enum.Parse(anchorType, "Live"), null, 1, true);

        Assert.Empty(result);
        Assert.Same(manualPlayback, playbackField.GetValue(plugin));
    }

    [Fact]
    public void DeathHandoffFindsOwnedLoopWaitersWithoutNativePlayback()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var slots = GetProperty<ReplaySlotRegistry>(session, "ReplaySlots");
        slots.MarkPlaying(slots.LoadAndClaim(2).Slot, loop: true);
        slots.MarkPlaying(slots.LoadAndClaim(5).Slot, loop: true);

        // Native execution has already finished for a waiting loop slot. The
        // actual death selector must use ownership without querying a host.
        Assert.Equal(2, Invoke<int>(plugin, "GetDeathHandoffSlot", 2, 5));
        Assert.Equal(5, Invoke<int>(plugin, "GetDeathHandoffSlot", 11, 5));
        slots.Release(2);
        Assert.Equal(5, Invoke<int>(plugin, "GetDeathHandoffSlot", 2, 5));
        slots.Release(5);
        Assert.Equal(-1, Invoke<int>(plugin, "GetDeathHandoffSlot", 2, 5));
        Assert.False(slots.IsCompletedLoop([2, 5]));
    }

    [Fact]
    public void CommandOnlyFreezePrerollKeepsHandDesireBeforeManagedPlayingStarts()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        GetProperty<HashSet<int>>(session, "FreezePrerollSlots").Add(4);
        var owner = new DemoTracerPlugin.ReplayViewOwner(0x8005, 0x8010, 0x1000);
        GetField<Dictionary<int, DemoTracerPlugin.ReplayViewOwner>>(plugin, "_replayLeftHandDesiredLatches").Add(4, owner);

        // No manifest viewmodel and no PlayingSlots yet. Running the real
        // cleanup must not clear the already-running native pre-roll's desire.
        Invoke<object?>(plugin, "RestoreNonRetainedReplayBotViewmodels");

        Assert.Equal(owner, GetField<Dictionary<int, DemoTracerPlugin.ReplayViewOwner>>(plugin, "_replayLeftHandDesiredLatches")[4]);
        Assert.True(Invoke<bool>(plugin, "IsReplayViewmodelSlotTracked", 4));
    }

    [Fact]
    public void ReleasingRetainedViewmodelOffsetsPreservesHandoffHandDesire()
    {
        var plugin = CreatePlugin();
        var retained = GetField<HashSet<int>>(plugin, "_retainedReplayViewmodelSlots");
        var hands = GetField<Dictionary<int, DemoTracerPlugin.ReplayViewOwner>>(plugin, "_replayLeftHandDesiredLatches");
        var owner = new DemoTracerPlugin.ReplayViewOwner(0x8005, 0x8010, 0x1000);
        retained.Add(4);
        hands.Add(4, owner);

        // This entry point implements viewmodel_continuity=release. Changing
        // visual offsets must not force a weapon redeploy during native combat.
        Invoke<object?>(plugin, "RestoreRetainedReplayBotViewmodels");
        Invoke<object?>(plugin, "RestoreNonRetainedReplayBotViewmodels");

        Assert.Contains(4, retained);
        Assert.Equal(owner, hands[4]);
    }

    [Fact]
    public void SpawnDiscardsAppliedFailedAndRestoreStateWithoutTouchingResetPawn()
    {
        var plugin = CreatePlugin();
        var session = GetField<object>(plugin, "_session");
        var views = GetProperty<Dictionary<int, DemoTracerPlugin.ReplayPawnViewState>>(session, "ReplayViewmodels");
        var retained = GetField<HashSet<int>>(plugin, "_retainedReplayViewmodelSlots");
        var owner = new DemoTracerPlugin.ReplayViewOwner(0x8005, 0x8010, 0x1000);
        var old = new DemoTracerPlugin.ReplayPawnViewState(owner) { Failed = true, Applied = new() { Fov = 68 } };
        old.Capture(new() { Fov = 60 }, new() { Fov = 68 });
        views.Add(4, old);
        retained.Add(4);

        // Same pointer/handle on respawn must still discard the whole state.
        // This runs without CSS/native entities, so any restore attempt fails.
        Invoke<object?>(plugin, "InvalidateReplayPawnViewState", 4);
        Assert.Empty(views);
        Assert.Empty(retained);
        Assert.False(Invoke<bool>(plugin, "IsReplayViewmodelSlotTracked", 4));
    }

    private static DemoTracerPlugin CreatePlugin()
    {
        // BasePlugin requires a running CSS host. Initialize only the managed
        // state touched by cleanup; no controller or native replay is present.
        var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
        typeof(DemoTracerPlugin).GetField("_replayEquipment", PrivateInstance)!.SetValue(plugin, ReplayEquipmentCatalog.Empty);
        foreach (var name in new[]
                 {
                    "_session", "_replaySlotWork", "_replayRoundWork", "_pendingSafeC4DropHandles", "_botRandomizerLease",
                    "_retainedBotRandomizerCosmeticSlots", "_botRandomizerBridge", "_legacyCosmeticPaints", "_playoffHistory",
                     "_voiceClipPreloadGate",
                     "_retainedBotHiderPresentation", "_activeBotHiderReplaySteamIds",
                     "_retainedReplayViewmodelSlots", "_replayLeftHandDesiredLatches"
                 })
        {
            var field = typeof(DemoTracerPlugin).GetField(name, PrivateInstance)!;
            field.SetValue(plugin, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }
        typeof(DemoTracerPlugin).GetField("_botHiderPresentationLeaseToken", PrivateInstance)!
            .SetValue(plugin, string.Empty);
        return plugin;
    }

    private static T GetField<T>(object target, string name)
        => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;

    private static T GetProperty<T>(object target, string name)
        => (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private static T Invoke<T>(object target, string name, params object?[] arguments)
        => (T)target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, arguments)!;
}
