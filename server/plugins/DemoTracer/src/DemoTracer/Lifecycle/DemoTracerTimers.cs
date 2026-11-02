/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly Dictionary<CssTimer, int> _ownedTimers = [];

    private CssTimer AddOwnedTimer(float interval, Action callback, TimerFlags flags = 0)
    {
        CssTimer? timer = null;
        Action wrapped = () =>
        {
            try { callback(); }
            finally
            {
                if ((flags & TimerFlags.REPEAT) == 0)
                    ReleaseOwnedTimer(timer!, kill: false);
            }
        };
        // CSS 1.0.378 retains both BasePlugin.Timers and permanent callback
        // references. Own both lifetimes; Timer's delegate conversion reuses this reference.
        var reference = FunctionReference.Create(wrapped);
        try
        {
            timer = new CssTimer(interval, wrapped, flags);
            _ownedTimers.Add(timer, reference.Identifier);
            return timer;
        }
        catch
        {
            FunctionReference.Remove(reference.Identifier);
            throw;
        }
    }

    private void ReleaseOwnedTimer(CssTimer? timer, bool kill = true)
    {
        if (timer == null || !_ownedTimers.TryGetValue(timer, out var reference))
            return;
        if (kill)
            timer.Kill();
        _ownedTimers.Remove(timer);
        FunctionReference.Remove(reference);
    }

    private void ClearOwnedTimers()
    {
        foreach (var timer in _ownedTimers.Keys.ToArray())
            ReleaseOwnedTimer(timer);
    }
}
