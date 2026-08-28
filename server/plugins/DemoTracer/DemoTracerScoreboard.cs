/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private ReplayScoreboardFlair? NormalizeReplayScoreboardFlair(ReplayScoreboardFlair? flair)
    {
        if (flair == null)
            return null;

        return new ReplayScoreboardFlair
        {
            ItemDefIndex = IsKnownScoreboardFlairItemDefIndex(flair.ItemDefIndex) ? flair.ItemDefIndex : 0
        };
    }

    private static ReplayPlayerScoreboard NormalizeReplayScoreboard(ReplayPlayerScoreboard? scoreboard)
    {
        if (scoreboard == null)
            return new ReplayPlayerScoreboard();

        return new ReplayPlayerScoreboard
        {
            PlayerUserId = scoreboard.PlayerUserId,
            PlayerEntityId = scoreboard.PlayerEntityId,
            PlayerColor = NormalizeReplayPlayerColor(scoreboard.PlayerColor),
            Score = scoreboard.Score,
            Kills = NormalizeScoreboardCount(scoreboard.Kills),
            Deaths = NormalizeScoreboardCount(scoreboard.Deaths),
            Assists = NormalizeScoreboardCount(scoreboard.Assists),
            MVPs = NormalizeScoreboardCount(scoreboard.MVPs)
        };
    }

    private static int? NormalizeScoreboardCount(int? value)
    {
        if (!value.HasValue || value.Value < 0 || value.Value > 1000)
            return null;
        return value.Value;
    }

    private static bool HasScoreboardEvidence(ReplayPlayerScoreboard scoreboard)
        => scoreboard.PlayerColor != null ||
           scoreboard.Score.HasValue ||
           scoreboard.Kills.HasValue ||
           scoreboard.Deaths.HasValue ||
           scoreboard.Assists.HasValue ||
           scoreboard.MVPs.HasValue;

    private void ResetScoreboardAlignState(bool resetCounters = false)
    {
        _session.ScoreboardSyncedSlots.Clear();
        if (resetCounters)
        {
            _scoreboardAppliedCount = 0;
            _scoreboardSkippedCount = 0;
        }
    }

    private string FormatScoreboardStatusCounts()
        => $"scoreboard_evidence={CountLoadedScoreboardEvidence()} scoreboard_applied={_scoreboardAppliedCount} scoreboard_skipped={_scoreboardSkippedCount}";

    private int CountLoadedScoreboardEvidence()
        => _session.LoadedReplays.Values.Count(replay => HasScoreboardEvidence(replay.Scoreboard));

    private void ApplyLoadedReplayScoreboards()
    {
        if (!_scoreboardAlignEnabled)
            return;

        ApplyLoadedRoundScoreboard();
        foreach (var slot in _session.LoadedSlots)
        {
            if (_session.ScoreboardSyncedSlots.Contains(slot))
                continue;
            if (!_session.LoadedReplays.TryGetValue(slot, out var replay))
                continue;
            ApplyReplayPlayerScoreboardForSlot(slot, replay.Scoreboard);
        }
    }

    private void ApplyLoadedRoundScoreboard()
    {
        if (!_scoreboardAlignEnabled)
            return;

        var scoreboard = _session.LoadedRoundScoreboard;
        if (scoreboard == null)
            return;

        var wroteAny =
            TryApplyRoundTeamName("mp_teamname_1", scoreboard.CtTeamName) |
            TryApplyRoundTeamName("mp_teamname_2", scoreboard.TTeamName);
        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
        {
            if (team is not { IsValid: true })
                continue;

            if ((int)team.TeamNum == 2)
            {
                team.Score = scoreboard.TScore;
                wroteAny = true;
                TrySetScoreboardStateChanged(team, "CTeam", "m_iScore");
            }
            else if ((int)team.TeamNum == 3)
            {
                team.Score = scoreboard.CtScore;
                wroteAny = true;
                TrySetScoreboardStateChanged(team, "CTeam", "m_iScore");
            }
        }

        if (!wroteAny)
            _scoreboardSkippedCount++;
    }

    private static bool TryApplyRoundTeamName(string cvar, string? name)
    {
        var normalized = NormalizeRoundTeamName(name);
        if (normalized == null)
            return false;

        Server.ExecuteCommand($"{cvar} \"{EscapeConsoleString(normalized)}\"");
        return true;
    }

    private static string? NormalizeRoundTeamName(string? name)
    {
        var normalized = name?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private void ApplyReplayPlayerScoreboardForSlot(int slot, ReplayPlayerScoreboard scoreboard)
    {
        if (!_scoreboardAlignEnabled)
            return;

        if (!HasScoreboardEvidence(scoreboard))
        {
            _session.ScoreboardSyncedSlots.Add(slot);
            return;
        }

        if (!IsReplaySlotStillSafe(slot))
        {
            _scoreboardSkippedCount++;
            return;
        }

        var player = FindTeamPlayers().FirstOrDefault(candidate => candidate.Slot == slot);
        if (player is not { IsValid: true } || !IsReplayTargetBot(player))
        {
            _scoreboardSkippedCount++;
            return;
        }

        try
        {
            var colorApplied = TryGetReplayPlayerColorIndex(scoreboard.PlayerColor, out var playerColor) &&
                TryApplyReplayPlayerColor(player, playerColor);
            if (scoreboard.Score.HasValue)
            {
                player.Score = scoreboard.Score.Value;
                TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_iScore");
            }
            if (scoreboard.MVPs.HasValue)
            {
                player.MVPs = scoreboard.MVPs.Value;
                TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_iMVPs");
            }

            var tracking = player.ActionTrackingServices;
            if (tracking != null)
            {
                if (scoreboard.Kills.HasValue)
                    tracking.MatchStats.Kills = scoreboard.Kills.Value;
                if (scoreboard.Deaths.HasValue)
                    tracking.MatchStats.Deaths = scoreboard.Deaths.Value;
                if (scoreboard.Assists.HasValue)
                    tracking.MatchStats.Assists = scoreboard.Assists.Value;

                // CounterStrikeSharp exposes m_vecPerRoundStats as NetworkedVector<CSPerRoundStats_t>,
                // but indexing networked vectors currently supports only CHandle<T> elements, so
                // keep K/D/A on the supported match totals.
                TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_pActionTrackingServices");
            }

            _session.ScoreboardSyncedSlots.Add(slot);
            _scoreboardAppliedCount++;
            Server.PrintToConsole(
                $"dtr: match scoreboard applied slot={slot} player={player.PlayerName} color={(colorApplied ? scoreboard.PlayerColor : "-")} score={FormatScoreboardValue(scoreboard.Score)} k={FormatScoreboardValue(scoreboard.Kills)} d={FormatScoreboardValue(scoreboard.Deaths)} a={FormatScoreboardValue(scoreboard.Assists)} mvp={FormatScoreboardValue(scoreboard.MVPs)}");
        }
        catch (Exception ex)
        {
            _scoreboardSkippedCount++;
            Server.PrintToConsole($"dtr: match scoreboard skipped slot={slot}: {ex.Message}");
        }
    }

    private static string FormatScoreboardValue(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

    private static bool TryGetReplayPlayerColorIndex(string? playerColor, out int colorIndex)
    {
        colorIndex = ReplayPlayerColorSchemaIndex(playerColor);
        return colorIndex >= 0 && colorIndex < 5;
    }

    private static bool TryApplyReplayPlayerColor(CCSPlayerController player, int colorIndex)
    {
        if (player.Handle == IntPtr.Zero)
            return false;

        try
        {
            Schema.SetSchemaValue(player.Handle, "CCSPlayerController", "m_iCompTeammateColor", colorIndex);
            TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_iCompTeammateColor");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetScoreboardStateChanged(
        CBaseEntity entity,
        string className,
        string fieldName,
        int extraOffset = 0)
    {
        try
        {
            if (!Schema.IsSchemaFieldNetworked(className, fieldName))
                return;
            Utilities.SetStateChanged(entity, className, fieldName, extraOffset);
        }
        catch
        {
            // Scoreboard fields vary across game/CSS builds. The schema value
            // write is still useful when the engine owns publication.
        }
    }
}
