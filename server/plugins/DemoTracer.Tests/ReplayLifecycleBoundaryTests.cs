/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;

namespace DemoTracer.Tests;

public sealed class ReplayLifecycleBoundaryTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

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

        // The actual release entry point must be safe without an engine: it
        // must neither unlock this slot nor clear a later owner's native input,
        // equipment or buy plan simply because DTR retained its old buffer.
        Invoke<object?>(plugin, "ReleaseReplaySlot", 4, "dispose_after_handoff", kind);
        Invoke<object?>(plugin, "ReleaseReplaySlot", 4, "repeat_dispose", kind);

        Assert.True(slots.IsLoaded(4));
        Assert.False(slots.IsOwned(4));
        Assert.Equal(releasedEpoch, slots.CurrentEpoch(4));
        Assert.True(slots.Unload(4));
        Invoke<object?>(plugin, "ReleaseReplaySlot", 4, "repeat_after_unload", kind);
    }

    [Fact]
    public void StopCancelsQueuedStartsAndSpawnRetriesBeforeAnotherStartCanBeQueued()
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
        foreach (var name in new[]
                 {
                     "_session", "_replaySlotWork", "_replayRoundWork", "_pendingSafeC4DropHandles",
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
