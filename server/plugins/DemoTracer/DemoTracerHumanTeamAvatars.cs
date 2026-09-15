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
    private void ConfigureLoadedTeamAvatarOverrides(
        string manifestDirectory,
        IReadOnlyList<ManifestFile> terroristFiles,
        IReadOnlyList<ManifestFile> counterTerroristFiles,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides)
    {
        _session.TeamAvatarOverrides.Clear();
        TryAddLoadedTeamAvatarOverride(
            CsTeam.Terrorist,
            manifestDirectory,
            terroristFiles,
            avatarOverrides);
        TryAddLoadedTeamAvatarOverride(
            CsTeam.CounterTerrorist,
            manifestDirectory,
            counterTerroristFiles,
            avatarOverrides);
    }

    private void TryAddLoadedTeamAvatarOverride(
        CsTeam team,
        string manifestDirectory,
        IReadOnlyList<ManifestFile> files,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides)
    {
        var teamSteamIds = files
            .Select(file => file.SteamId)
            .Where(steamId => steamId != 0)
            .Distinct()
            .ToArray();
        var candidates = teamSteamIds
            .Where(avatarOverrides.ContainsKey)
            .Select(steamId => avatarOverrides[steamId])
            .ToArray();
        var contentKey = ReplayTeamAvatarPolicy.FindSharedContentKey(
            teamSteamIds,
            candidates.Select(avatar => new ReplayTeamAvatarEvidence(
                avatar.SteamId,
                TeamAvatarContentKey(avatar))));
        if (contentKey == null)
            return;

        var avatar = candidates.First(candidate =>
            string.Equals(
                TeamAvatarContentKey(candidate),
                contentKey,
                StringComparison.OrdinalIgnoreCase));
        _session.TeamAvatarOverrides[team] = new LoadedTeamAvatarOverride(
            manifestDirectory,
            avatar,
            contentKey);
    }

    private static string TeamAvatarContentKey(ManifestAvatarOverride avatar)
    {
        var sha256 = NormalizeSha256(avatar.Sha256);
        if (sha256.Length >= 16)
            return $"sha256:{sha256}";

        var path = avatar.Path.Trim().Replace('\\', '/');
        return path.Length == 0 ? string.Empty : $"path:{path}";
    }

    private void ScheduleHumanTeamAvatarOverrideReconciliation()
        => Server.NextFrame(ReconcileHumanTeamAvatarOverrides);

    private void ReconcileHumanTeamAvatarOverrides()
    {
        if (_replayIdentityMode != ReplayIdentityMode.Avatar)
        {
            ClearHumanTeamAvatarOverrides("identity_disabled");
            return;
        }

        var humans = FindPlayerControllers()
            .Where(IsHumanAvatarOverrideCandidate)
            .ToArray();
        var liveSlots = humans.Select(player => player.Slot).ToHashSet();
        foreach (var slot in _session.HumanTeamAvatarOverrides.Keys
                     .Where(slot => !liveSlots.Contains(slot))
                     .ToArray())
        {
            ClearHumanTeamAvatarOverrideForSlot(slot, "client_missing");
        }

        foreach (var player in humans)
            ReconcileHumanTeamAvatarOverride(player);
    }

    private bool IsHumanAvatarOverrideCandidate(CCSPlayerController player)
        => player is { IsValid: true } &&
           !player.IsHLTV &&
           !player.IsBot &&
           !_botHiderBridge.IsManagedBot(player.Slot);

    private void ReconcileHumanTeamAvatarOverride(CCSPlayerController player)
    {
        var slot = player.Slot;
        var steamId = NormalizeOptionalULong(player.SteamID);
        if (!steamId.HasValue ||
            !_session.TeamAvatarOverrides.TryGetValue(player.Team, out var teamAvatar))
        {
            ClearHumanTeamAvatarOverrideForSlot(slot, "team_without_overlay");
            return;
        }

        var userId = player.UserId;
        if (_session.HumanTeamAvatarOverrides.TryGetValue(slot, out var applied) &&
            applied.UserId == userId &&
            applied.SteamId == steamId.Value &&
            string.Equals(applied.ContentKey, teamAvatar.ContentKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_session.HumanTeamAvatarOverrides.TryGetValue(slot, out applied) &&
            applied.SteamId != steamId.Value)
        {
            ClearHumanTeamAvatarOverrideForSlot(slot, "slot_reused");
        }

        if (!TryPrepareReplayAvatarOverride(
                teamAvatar.ManifestDirectory,
                teamAvatar.Avatar,
                out var png,
                out var error))
        {
            ClearHumanTeamAvatarOverrideForSlot(slot, "overlay_unavailable");
            Server.PrintToConsole(
                $"dtr: human team avatar skipped slot={slot} sid={steamId.Value} team={player.Team}: {error}");
            return;
        }

        if (!BotControllerNative.TryPublishAvatarOverride(steamId.Value, png, out error))
        {
            Server.PrintToConsole($"dtr: human team avatar publish failed slot={slot}: {error}");
            return;
        }
        _session.HumanTeamAvatarOverrides[slot] = new AppliedHumanTeamAvatarOverride(
            userId,
            steamId.Value,
            teamAvatar.ContentKey);
        Server.PrintToConsole(
            $"dtr: human team avatar published slot={slot} sid={steamId.Value} team={player.Team} path={teamAvatar.Avatar.Path}");
    }

    private void ClearHumanTeamAvatarOverrideForSlot(int slot, string reason)
    {
        if (!_session.HumanTeamAvatarOverrides.TryGetValue(slot, out var applied))
            return;

        if (!BotControllerNative.TryClearAvatarOverride(applied.SteamId, out var error))
        {
            Server.PrintToConsole($"dtr: human team avatar clear failed slot={slot}: {error}");
            return;
        }
        _session.HumanTeamAvatarOverrides.Remove(slot);
        Server.PrintToConsole(
            $"dtr: human team avatar cleared slot={slot} sid={applied.SteamId} reason={reason}");
    }

    private void ClearHumanTeamAvatarOverrides(string reason)
    {
        foreach (var slot in _session.HumanTeamAvatarOverrides.Keys.ToArray())
            ClearHumanTeamAvatarOverrideForSlot(slot, reason);
    }

    private void ClearLoadedTeamAvatarOverrides(string reason)
    {
        ClearHumanTeamAvatarOverrides(reason);
        BotControllerNative.ClearAvatarOverrides();
        _session.TeamAvatarOverrides.Clear();
    }

}
