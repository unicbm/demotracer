/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;

namespace DemoTracer.Tests;

public sealed class DtrReplayPrefetchTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"demotracer-prefetch-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task TryTakeNeverWaitsForPendingDecode()
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "pending.dtr");
        File.WriteAllBytes(path, [1, 2, 3]);

        using var enteredReader = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        var prefetch = new DtrReplayPrefetch(_ =>
        {
            enteredReader.Set();
            releaseReader.Wait();
            return default;
        });

        try
        {
            prefetch.Begin([path]);
            Assert.True(prefetch.HasGeneration);
            Assert.True(enteredReader.Wait(TimeSpan.FromSeconds(5)));

            var takeTask = Task.Run(() => prefetch.TryTake(path, out _));
            var completed = await Task.WhenAny(
                takeTask,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(takeTask, completed);
            Assert.Equal(
                DtrReplayPrefetchTakeStatus.Pending,
                await takeTask);
        }
        finally
        {
            releaseReader.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            prefetch.AllPendingCompleted,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(
            DtrReplayPrefetchTakeStatus.Failed,
            prefetch.TryTake(path, out _));
        Assert.Equal(
            DtrReplayPrefetchTakeStatus.Missing,
            prefetch.TryTake(path, out _));
        Assert.True(prefetch.HasGeneration);
        prefetch.Cancel();
        Assert.False(prefetch.HasGeneration);
    }

    [Fact]
    public void SwappedSidePrefetchCanBeConsumedAndBothOrientationsAreCancelled()
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "swapped.dtr");
        File.WriteAllBytes(path, [1, 2, 3]);
        var replay = new DtrReplayFile(12, [], [], ReplayHighFidelityMetadata.Empty, [], [], [], 64, 0);
        var normal = new DtrReplayPrefetch(_ => replay);
        var swapped = new DtrReplayPrefetch(_ => replay);
        var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DemoTracerPlugin).GetField("_dtrReplayPrefetch", flags)!.SetValue(plugin, normal);
        typeof(DemoTracerPlugin).GetField("_playoffSwappedReplayPrefetch", flags)!.SetValue(plugin, swapped);

        normal.Begin([]);
        swapped.Begin([path]);
        Assert.True(SpinWait.SpinUntil(swapped.AllPendingCompleted, TimeSpan.FromSeconds(5)));
        object?[] args = [path, null];
        var status = typeof(DemoTracerPlugin).GetMethod("TryTakePrefetchedReplay", flags)!.Invoke(plugin, args);
        Assert.Equal(DtrReplayPrefetchTakeStatus.Success, Assert.IsType<DtrReplayPrefetchTakeStatus>(status));
        Assert.Equal(64f, Assert.IsType<DtrReplayFile>(args[1]).TickRate);

        typeof(DemoTracerPlugin).GetMethod("FinishReplayPrefetchRound", flags)!.Invoke(plugin, null);
        Assert.False(normal.HasGeneration);
        Assert.False(swapped.HasGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayoffWaitsOnlyForTheResolvedOrientation(bool swapTeams)
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "shared.dtr");
        File.WriteAllBytes(path, [1, 2, 3]);
        var replay = new DtrReplayFile(12, [], [], ReplayHighFidelityMetadata.Empty, [], [], [], 64, 0);
        using var enteredReader = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        var ready = new DtrReplayPrefetch(_ => replay);
        var pending = new DtrReplayPrefetch(_ =>
        {
            enteredReader.Set();
            releaseReader.Wait();
            return replay;
        });
        var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DemoTracerPlugin).GetField("_dtrReplayPrefetch", flags)!.SetValue(plugin, swapTeams ? pending : ready);
        typeof(DemoTracerPlugin).GetField("_playoffSwappedReplayPrefetch", flags)!.SetValue(plugin, swapTeams ? ready : pending);
        var tRoster = new HashSet<ulong> { 101 };
        var ctRoster = new HashSet<ulong> { 102 };
        var sources = new PlayoffSourceSelection(tRoster, ctRoster, new(4, 6, "same"), new(7, 8, "swapped"));
        Assert.True(sources.TryResolve(swapTeams ? ctRoster : tRoster, swapTeams ? tRoster : ctRoster,
            out var selected, out var swapped));
        Assert.Equal(swapTeams, swapped);
        Assert.Equal(swapTeams ? 7 : 4, selected.TRound);

        try
        {
            pending.Begin([path]);
            ready.Begin([path]);
            Assert.True(enteredReader.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(ready.AllPendingCompleted, TimeSpan.FromSeconds(5)));
            var isReady = typeof(DemoTracerPlugin).GetMethod("PlayoffOrientationPrefetchReady", flags)!;
            Assert.Equal(true, isReady.Invoke(plugin, [swapped]));
            Assert.Equal(false, isReady.Invoke(plugin, [!swapped]));
        }
        finally
        {
            releaseReader.Set();
            Assert.True(SpinWait.SpinUntil(pending.AllPendingCompleted, TimeSpan.FromSeconds(5)));
            pending.Cancel();
            ready.Cancel();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }
}
