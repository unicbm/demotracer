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
        var replay = new DtrReplayFile(11, [], [], ReplayHighFidelityMetadata.Empty, [], [], [], [], [], 64, 0);
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

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
    }
}
