/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Diagnostics;
using CounterStrikeSharp.API;

namespace DemoTracer;

internal sealed class ReplayPhaseTimer(string label) : IDisposable
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _previous = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> _phases = [];
    public void Mark(string phase)
    {
        var now = Stopwatch.GetTimestamp();
        _phases[phase] = _phases.GetValueOrDefault(phase) + Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds;
        _previous = now;
    }
    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        if (elapsed < Server.TickInterval * 1000) return;
        var phases = string.Join(" ", _phases.Select(pair => FormattableString.Invariant($"{pair.Key}={pair.Value:F1}ms")));
        Server.PrintToConsole(FormattableString.Invariant($"[DTR PERF] {label} total={elapsed:F1}ms {phases}"));
    }
}
