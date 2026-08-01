/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static readonly string[] ReplayBuyCommandNames = ["buy", "autobuy", "rebuy"];

    private void RegisterReplayBuySuppressionHooks()
    {
        foreach (var commandName in ReplayBuyCommandNames)
            AddCommandListener(commandName, SuppressReplayBuyCommand, HookMode.Pre);
    }

    private void UnregisterReplayBuySuppressionHooks()
    {
        foreach (var commandName in ReplayBuyCommandNames)
            RemoveCommandListener(commandName, SuppressReplayBuyCommand, HookMode.Pre);
    }

    private HookResult SuppressReplayBuyCommand(
        CCSPlayerController? player,
        CommandInfo command)
    {
        if (player is not { IsValid: true })
            return HookResult.Continue;

        var slot = player.Slot;
        return ShouldSuppressReplayBuyCommand(
            _session.LoadedReplays.ContainsKey(slot),
            _session.ReplaySlots.IsOwned(slot),
            IsReplaySlotStillSafe(slot))
            ? HookResult.Handled
            : HookResult.Continue;
    }

    internal static bool ShouldSuppressReplayBuyCommand(
        bool replayLoaded,
        bool replayOwned,
        bool replaySlotSafe)
        => replayLoaded && replayOwned && replaySlotSafe;
}
