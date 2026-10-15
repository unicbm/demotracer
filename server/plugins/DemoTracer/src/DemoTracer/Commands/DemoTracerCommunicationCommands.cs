/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private bool _muteDivergedDeadCommsEnabled = true;

    [ConsoleCommand("dtr_mute_dead_comms", "dtr_mute_dead_comms [status|on|off]")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void MuteDeadCommsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
            _muteDivergedDeadCommsEnabled = ParseOnOff(command.GetArg(1), _muteDivergedDeadCommsEnabled);
        command.ReplyToCommand(
            $"[DTR OK] mute_dead_comms={FormatOnOff(_muteDivergedDeadCommsEnabled)} scope=dead_pawn_during_recorded_life");
    }

    private bool ShouldMuteReplayCommunication(CCSPlayerController sender, long demoTick, int liveStartTick)
    {
        if (!_muteDivergedDeadCommsEnabled || sender.PawnIsAlive ||
            !_session.LoadedReplays.TryGetValue(sender.Slot, out var replay))
            return false;

        return IsWithinRecordedLife(demoTick, liveStartTick, replay.TickCount, replay.PlayStartTickIndex);
    }

    internal static bool IsWithinRecordedLife(long demoTick, int liveStartTick, int tickCount, uint playStartTickIndex)
    {
        // The exporter retains consecutive living rows only. The final post
        // snapshot is living too; after it, do not infer a death or mute the
        // original demo's legitimate post-death communication.
        if (liveStartTick <= 0 || tickCount <= 0)
            return false;
        var firstTick = (long)liveStartTick - playStartTickIndex;
        return demoTick >= firstTick && demoTick <= firstTick + tickCount;
    }
}
