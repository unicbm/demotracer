/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly DtrReplayPrefetch _dtrReplayPrefetch = new();
    private string _prefetchedManifestPath = string.Empty;
    private ReplayFileStamp _prefetchedManifestStamp;
    private ConversionManifest? _prefetchedManifest;

    [GameEventHandler]
    public HookResult OnRoundEndReplayPrefetch(EventRoundEnd @event, GameEventInfo info)
    {
        if (_session.Plan.SequenceActive &&
            !_session.Plan.SequencePrepared &&
            _session.Plan.SequenceIndex >= 0 &&
            _session.Plan.SequenceIndex < _session.Plan.SequenceRounds.Length &&
            !ReplayPrefetchActive())
        {
            PrefetchRoundReplays(_session.Plan.SequenceManifestPath, _session.Plan.SequenceRounds[_session.Plan.SequenceIndex]);
        }
        else if (IsPlayoffPlanReady())
        {
            // Select and decode while the completed round still carries the
            // retained roster evidence, but let round_prestart own bot loading.
            _ = PrepareNextPlayoffRound("round_end prefetch", allowLoad: false);
        }

        return HookResult.Continue;
    }

    private void PrefetchRoundReplays(
        string manifestPath,
        ConversionManifest manifest,
        int round,
        ReplayFileStamp? stableManifestStamp = null)
    {
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        if (stableManifestStamp.HasValue)
            RememberPrefetchedManifest(resolvedManifestPath, manifest, stableManifestStamp.Value);

        var manifestDir = Path.GetDirectoryName(resolvedManifestPath) ?? ".";
        var roundFiles = manifest.Files.Where(file => file.Round == round).ToList();
        var tFiles = SortReplayFilesForScoreboard(roundFiles, "t")
            .Take(StandardTeamSize)
            .ToList();
        var ctFiles = SortReplayFilesForScoreboard(roundFiles, "ct")
            .Take(StandardTeamSize)
            .ToList();
        var paths = new List<string>(StandardTeamSize * 2);
        for (var index = 0; index < Math.Max(tFiles.Count, ctFiles.Count); index++)
        {
            AddPath(tFiles, index);
            AddPath(ctFiles, index);
        }

        _dtrReplayPrefetch.Begin(paths);

        void AddPath(IReadOnlyList<ManifestFile> files, int index)
        {
            if (index >= files.Count ||
                !TryResolveChildPathUnderRoot(manifestDir, files[index].Path, out var path, out _))
            {
                return;
            }
            paths.Add(path);
        }
    }

    private void PrefetchRoundReplays(string manifestPath, int round)
    {
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        if (TryGetPrefetchedManifest(resolvedManifestPath, out var cachedManifest))
        {
            PrefetchRoundReplays(resolvedManifestPath, cachedManifest, round);
            return;
        }

        if (!ReplayFileStamp.TryRead(resolvedManifestPath, out var before) ||
            !TryReadManifest(resolvedManifestPath, out var manifest, out _) ||
            !ReplayFileStamp.TryRead(resolvedManifestPath, out var after) ||
            before != after)
        {
            _dtrReplayPrefetch.Cancel();
            return;
        }

        PrefetchRoundReplays(resolvedManifestPath, manifest, round, after);
    }

    private void PrefetchPlayoffRoundReplays(
        string manifestPath,
        ConversionManifest manifest,
        int tRound,
        int ctRound,
        IReadOnlySet<ulong> tSteamIds,
        IReadOnlySet<ulong> ctSteamIds)
    {
        var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
        var manifestDir = Path.GetDirectoryName(resolvedManifestPath) ?? ".";
        var paths = new List<string>(tSteamIds.Count + ctSteamIds.Count);
        AddSide("t", tRound, tSteamIds);
        AddSide("ct", ctRound, ctSteamIds);
        _dtrReplayPrefetch.Begin(paths);

        void AddSide(string side, int round, IReadOnlySet<ulong> steamIds)
        {
            if (round < 0 || steamIds.Count == 0)
                return;

            foreach (var file in manifest.Files
                         .Where(file => file.Round == round &&
                                        file.Side.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                                        steamIds.Contains(file.SteamId))
                         .OrderBy(file => file.SteamId))
            {
                if (TryResolveChildPathUnderRoot(manifestDir, file.Path, out var path, out _))
                    paths.Add(path);
            }
        }
    }

    private DtrReplayPrefetchTakeStatus TryTakePrefetchedReplay(
        string path,
        out DtrReplayFile replay)
        => _dtrReplayPrefetch.TryTake(path, out replay);

    private bool ReplayPrefetchReady()
        => _dtrReplayPrefetch.AllPendingCompleted();

    private bool ReplayPrefetchActive()
        => _dtrReplayPrefetch.HasGeneration;

    private void FinishReplayPrefetchRound()
        => _dtrReplayPrefetch.Cancel();

    private void CancelReplayPrefetch()
    {
        _dtrReplayPrefetch.Cancel();
        _prefetchedManifestPath = string.Empty;
        _prefetchedManifestStamp = default;
        _prefetchedManifest = null;
    }

    private void RememberPrefetchedManifest(
        string path,
        ConversionManifest manifest,
        ReplayFileStamp stableStamp)
    {
        if (!ReplayFileStamp.TryRead(path, out var currentStamp) || currentStamp != stableStamp)
            return;

        _prefetchedManifestPath = Path.GetFullPath(path);
        _prefetchedManifestStamp = stableStamp;
        _prefetchedManifest = manifest;
    }

    private bool TryGetPrefetchedManifest(string path, out ConversionManifest manifest)
    {
        manifest = null!;
        if (_prefetchedManifest == null ||
            !_prefetchedManifestPath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            !ReplayFileStamp.TryRead(path, out var currentStamp) ||
            currentStamp != _prefetchedManifestStamp)
        {
            return false;
        }

        manifest = _prefetchedManifest;
        return true;
    }
}

internal sealed class DtrReplayPrefetch
{
    // Approximate managed replay-array budget for pending cache entries. A
    // consumed replay releases its reservation before the native handoff, so
    // at most that one handoff object temporarily sits outside this budget.
    private const long MaxRetainedDecodedBytes = 64L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _singleReader = new(1, 1);
    private readonly Func<string, DtrReplayFile> _readReplay;
    private Dictionary<string, PrefetchEntry> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private PrefetchGeneration? _generation;

    internal DtrReplayPrefetch(Func<string, DtrReplayFile>? readReplay = null)
    {
        _readReplay = readReplay ?? DtrReplayReader.Read;
    }

    public bool HasGeneration
    {
        get
        {
            lock (_sync)
                return _generation != null;
        }
    }

    public void Begin(IEnumerable<string> paths)
    {
        var fullPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var generation = new PrefetchGeneration(fullPaths.Length, MaxRetainedDecodedBytes);
        PrefetchGeneration? previous;
        lock (_sync)
        {
            previous = _generation;
            _generation = generation;
            _pending = new Dictionary<string, PrefetchEntry>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var fullPath in fullPaths)
            {
                var task = DecodeAsync(fullPath, generation);
                _pending[fullPath] = new PrefetchEntry(task, generation);
                generation.Track(task);
            }
        }

        previous?.Retire();
    }

    public DtrReplayPrefetchTakeStatus TryTake(
        string path,
        out DtrReplayFile replay)
    {
        replay = default;
        PrefetchEntry entry;
        var fullPath = Path.GetFullPath(path);
        lock (_sync)
        {
            if (!_pending.TryGetValue(fullPath, out entry))
                return DtrReplayPrefetchTakeStatus.Missing;
            if (!entry.Task.IsCompleted)
                return DtrReplayPrefetchTakeStatus.Pending;
            _pending.Remove(fullPath);
        }

        // DecodeAsync converts every exception into a completed failed result.
        // This GetResult therefore only unwraps an already-completed task and
        // can never park the game thread behind disk I/O or Brotli decoding.
        var result = entry.Task.GetAwaiter().GetResult();
        try
        {
            if (!result.Ok ||
                !ReplayFileStamp.TryRead(fullPath, out var currentStamp) ||
                currentStamp != result.Stamp)
            {
                return DtrReplayPrefetchTakeStatus.Failed;
            }

            replay = result.Replay;
            return DtrReplayPrefetchTakeStatus.Success;
        }
        finally
        {
            entry.Generation.ReleaseDecodedBytes(result.ReservedBytes);
        }
    }

    public bool AllPendingCompleted()
    {
        lock (_sync)
            return _pending.Values.All(static entry => entry.Task.IsCompleted);
    }

    public void Cancel()
    {
        PrefetchGeneration? previous;
        lock (_sync)
        {
            previous = _generation;
            _generation = null;
            _pending.Clear();
        }

        previous?.Retire();
    }

    private async Task<DtrReplayDecodeResult> DecodeAsync(
        string path,
        PrefetchGeneration generation)
    {
        var cancellationToken = generation.Token;
        try
        {
            await _singleReader.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReplayFileStamp.TryRead(path, out var before))
                    return DtrReplayDecodeResult.Failed;

                var replay = await Task.Run(() => _readReplay(path), cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested ||
                    !ReplayFileStamp.TryRead(path, out var after) ||
                    before != after)
                {
                    return DtrReplayDecodeResult.Failed;
                }
                var reservedBytes = EstimateReplayBytes(replay);
                if (!generation.TryReserveDecodedBytes(reservedBytes))
                    return DtrReplayDecodeResult.Failed;

                return new DtrReplayDecodeResult(true, replay, after, reservedBytes);
            }
            finally
            {
                _singleReader.Release();
            }
        }
        catch
        {
            return DtrReplayDecodeResult.Failed;
        }
    }

    private static long EstimateReplayBytes(DtrReplayFile replay)
    {
        try
        {
            checked
            {
                var bytes = replay.Ticks.LongLength * BotControllerNative.ReplayTickByteSize +
                            replay.Subticks.LongLength * BotControllerNative.SubtickMoveByteSize +
                            replay.CommandFrames.LongLength * BotControllerNative.ReplayCommandFrameByteSize +
                            replay.MovementExtras.LongLength * BotControllerNative.ReplayMovementExtraByteSize +
                            replay.Projectiles.LongLength * 128L;
                if (replay.HighFidelity != null)
                {
                    bytes += replay.HighFidelity.Events.LongLength * 128L;
                    bytes += replay.HighFidelity.InventorySnapshots.LongLength * 256L;
                    bytes += replay.HighFidelity.Projectiles.LongLength * 256L;
                }
                return Math.Max(1L, bytes);
            }
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private sealed class PrefetchGeneration(int taskCount, long decodedByteLimit)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _remainingTasks = taskCount;
        private int _retireState;
        private int _disposed;
        private long _decodedBytes;

        public CancellationToken Token => _cancellation.Token;

        public void Track(Task<DtrReplayDecodeResult> task)
        {
            _ = task.ContinueWith(
                static (_, state) => ((PrefetchGeneration)state!).TaskCompleted(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public bool TryReserveDecodedBytes(long bytes)
        {
            while (true)
            {
                var current = Volatile.Read(ref _decodedBytes);
                if (bytes > decodedByteLimit - current)
                    return false;
                if (Interlocked.CompareExchange(ref _decodedBytes, current + bytes, current) == current)
                    return true;
            }
        }

        public void ReleaseDecodedBytes(long bytes)
        {
            if (bytes > 0)
                Interlocked.Add(ref _decodedBytes, -bytes);
        }

        public void Retire()
        {
            if (Interlocked.CompareExchange(ref _retireState, 1, 0) != 0)
                return;
            try
            {
                _cancellation.Cancel();
            }
            finally
            {
                Volatile.Write(ref _retireState, 2);
                TryDispose();
            }
        }

        private void TaskCompleted()
        {
            Interlocked.Decrement(ref _remainingTasks);
            TryDispose();
        }

        private void TryDispose()
        {
            if (Volatile.Read(ref _retireState) != 2 ||
                Volatile.Read(ref _remainingTasks) != 0 ||
                Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                return;
            }
            _cancellation.Dispose();
        }
    }

    private readonly record struct PrefetchEntry(
        Task<DtrReplayDecodeResult> Task,
        PrefetchGeneration Generation);
}

internal readonly record struct DtrReplayDecodeResult(
    bool Ok,
    DtrReplayFile Replay,
    ReplayFileStamp Stamp,
    long ReservedBytes)
{
    public static DtrReplayDecodeResult Failed { get; } = new(false, default, default, 0);
}

internal enum DtrReplayPrefetchTakeStatus
{
    Missing,
    Pending,
    Failed,
    Success,
}

internal readonly record struct ReplayFileStamp(long Length, long LastWriteTimeUtcTicks)
{
    public static bool TryRead(string path, out ReplayFileStamp stamp)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                stamp = default;
                return false;
            }

            stamp = new ReplayFileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            stamp = default;
            return false;
        }
    }
}
