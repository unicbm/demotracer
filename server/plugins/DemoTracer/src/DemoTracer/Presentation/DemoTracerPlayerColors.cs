/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private void RepairMissingReplayPlayerColors()
    {
        if (_session.RoundSpawnsPending || _session.LoadedSlots.Count == 0)
            return;

        var players = FindTeamPlayers();
        var colors = new Dictionary<int, int>();
        foreach (var player in players)
        {
            try
            {
                colors[player.Slot] = player.CompTeammateColor;
            }
            catch
            {
                // Missing schema/controller evidence is not an unassigned color.
                return;
            }
        }

        foreach (var player in players)
        {
            var slot = player.Slot;
            if (!_session.ReplaySlots.IsOwned(slot) ||
                !_session.LoadedReplays.TryGetValue(slot, out var replay) ||
                !IsReplayTargetBot(player) ||
                !ReplayTeamAssignmentPolicy.LiveTeamMatches(replay.ManifestTeam, player.Team))
                continue;

            var occupied = players.Where(teammate => teammate.Team == player.Team && teammate.Slot != slot)
                .Select(teammate => colors[teammate.Slot]).ToHashSet();
            var replacement = ReplayPlayerColorPolicy.ChooseMissingColor(
                colors[slot], ReplayPlayerColorSchemaIndex(replay.PlayerColor), occupied);
            if (replacement is not { } color || !TryApplyReplayPlayerColor(player, color))
                continue;

            colors[slot] = color;
            Server.PrintToConsole(
                $"dtr: missing teammate color repaired slot={slot} team={player.Team} color={color} recorded={replay.PlayerColor}");
        }
    }

    private static bool TryApplyReplayPlayerColor(CCSPlayerController player, int colorIndex)
    {
        if (player.Handle == IntPtr.Zero)
            return false;

        try
        {
            Schema.SetSchemaValue(player.Handle, "CCSPlayerController", "m_iCompTeammateColor", colorIndex);
            TryPublishReplayPlayerColor(player);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryPublishReplayPlayerColor(CCSPlayerController player)
    {
        try
        {
            if (Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompTeammateColor"))
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompTeammateColor");
        }
        catch
        {
            // Some builds publish teammate colors through the engine instead.
        }
    }
}

internal static class ReplayPlayerColorPolicy
{
    internal static int? ChooseMissingColor(int currentColor, int recordedColor, IReadOnlySet<int> occupied)
    {
        // Keep existing server colors, including humans'. Only a replay with
        // positive color evidence may fill a vacancy left by a departing player.
        if (currentColor is >= 0 and < 5 || recordedColor is < 0 or >= 5)
            return null;
        if (!occupied.Contains(recordedColor))
            return recordedColor;
        for (var color = 0; color < 5; color++)
        {
            if (!occupied.Contains(color))
                return color;
        }
        return null;
    }
}
