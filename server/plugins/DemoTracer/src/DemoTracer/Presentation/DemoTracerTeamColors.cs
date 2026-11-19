/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly Dictionary<int, (uint Handle, int Original, int Applied)> _replayTeamColors = new();
    private readonly Dictionary<ulong, int> _sequenceSourceColors = new();

    private void SyncReplayTeamColors()
    {
        var desired = new Dictionary<int, (CCSPlayerController Player, int Color)>();
        if (_replayIdentityMode != ReplayIdentityMode.Off)
        {
            foreach (var (slot, evidence) in _retainedBotHiderPresentation)
            {
                var bot = Utilities.GetPlayerFromSlot(slot);
                if (evidence.TeamColor is int color && bot is { IsValid: true, IsHLTV: false } &&
                    bot.EntityHandle.Raw == evidence.ControllerHandle && (bot.IsBot || _botHiderBridge.IsManagedBot(slot)))
                    desired.Add(slot, (bot, color));
            }
            // CS2 resolves duplicate colors within a team. Put the human on the
            // reserved player's color so it cannot displace a replay bot's color.
            if (_sequenceSeats.Count > 0)
            {
                foreach (var human in FindPlayerControllers().Where(IsHumanPlayerController))
                    if (human.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist &&
                        _sequenceSeats.TryGetValue(human.EntityHandle.Raw, out var id) &&
                        _sequenceSourceColors.TryGetValue(id, out var color))
                        desired[human.Slot] = (human, color);
            }
        }
        foreach (var slot in _replayTeamColors.Keys.Except(desired.Keys).ToArray())
            RestoreReplayTeamColor(slot);

        foreach (var (slot, (player, color)) in desired)
        {
            var current = player.CompTeammateColor;
            if (current == color)
                continue;
            var original = _replayTeamColors.TryGetValue(slot, out var saved) &&
                           saved.Handle == player.EntityHandle.Raw && current == saved.Applied
                ? saved.Original : current;
            _replayTeamColors[slot] = (player.EntityHandle.Raw, original, color);
            WriteTeamColor(player, color);
        }
    }

    private void RestoreReplayTeamColor(int slot)
    {
        if (!_replayTeamColors.Remove(slot, out var saved))
            return;
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is { IsValid: true } && player.EntityHandle.Raw == saved.Handle &&
            player.CompTeammateColor == saved.Applied)
            WriteTeamColor(player, saved.Original);
    }

    private static void WriteTeamColor(CCSPlayerController player, int color)
    {
        player.CompTeammateColor = color;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompTeammateColor");
    }

    // Inverse of demoparser's player_color mapping, which the archive stores as text.
    internal static int? ReplayTeamColorValue(string? color) => color switch
    {
        "blue" => 0, "green" => 1, "yellow" => 2, "orange" => 3, "purple" => 4, _ => null,
    };
}
