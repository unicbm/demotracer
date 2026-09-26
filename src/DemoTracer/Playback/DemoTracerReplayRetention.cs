/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace DemoTracer;

internal static class ReplayRetentionPriorityParser
{
    internal const int MaxPlayersPerTeam = 5;
    internal const int PermutationCount = 120;

    internal static bool TryParsePermutationCode(string value, out int code)
        => int.TryParse(
               value.Trim(),
               NumberStyles.None,
               CultureInfo.InvariantCulture,
               out code) &&
           code is >= 0 and < PermutationCount;

    internal static bool TryDecodePermutation(int code, out int[] indices)
    {
        indices = [];
        if (code is < 0 or >= PermutationCount)
            return false;

        var remaining = Enumerable.Range(0, MaxPlayersPerTeam).ToList();
        var decoded = new int[MaxPlayersPerTeam];
        for (var index = 0; index < decoded.Length; index++)
        {
            var divisor = Factorial(decoded.Length - index - 1);
            var position = code / divisor;
            code %= divisor;
            decoded[index] = remaining[position];
            remaining.RemoveAt(position);
        }

        indices = decoded;
        return true;
    }

    private static int Factorial(int value)
    {
        var result = 1;
        for (var factor = 2; factor <= value; factor++)
            result *= factor;
        return result;
    }

    internal static bool TryParseGroup(string value, out ulong[] steamIds, out string error)
    {
        steamIds = [];
        error = string.Empty;
        var normalized = value.Trim();
        if (normalized is "-" || normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > MaxPlayersPerTeam)
        {
            error = $"a retention group must contain 1-{MaxPlayersPerTeam} SteamID64 values";
            return false;
        }

        var parsed = new ulong[parts.Length];
        var seen = new HashSet<ulong>();
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length != 17 ||
                !ulong.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) ||
                steamId == 0)
            {
                error = $"invalid SteamID64 \"{parts[index]}\"";
                return false;
            }
            if (!seen.Add(steamId))
            {
                error = $"duplicate SteamID64 {steamId.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }
            parsed[index] = steamId;
        }

        steamIds = parsed;
        return true;
    }

    internal static int[] SelectPreferredIndices(IReadOnlyList<int> ranks, int availableSlots)
    {
        var count = Math.Min(Math.Max(availableSlots, 0), ranks.Count);
        if (count == ranks.Count)
            return Enumerable.Range(0, count).ToArray();
        return Enumerable.Range(0, ranks.Count)
            .OrderBy(index => ranks[index])
            .ThenBy(index => index)
            .Take(count)
            .Order()
            .ToArray();
    }

    internal static int ReservedBotQuota(int baseline, int pendingHumanJoins)
        => Math.Max(0, baseline - Math.Max(0, pendingHumanJoins));
}

public sealed partial class DemoTracerPlugin
{
    private const string ReplayRetentionBotQuotaConVarName = "bot_quota";
    private const int ReplayRetentionJoinMaxFrames = 16;
    private readonly Dictionary<ulong, int> _pendingReplayRetentionRanks = new();
    private readonly Dictionary<ulong, int> _activeReplayRetentionRanks = new();
    private int? _pendingReplayRetentionTCode;
    private int? _pendingReplayRetentionCtCode;
    private int? _activeReplayRetentionTCode;
    private int? _activeReplayRetentionCtCode;
    private readonly HashSet<int> _pendingHumanTeamChangeSlots = new();
    private readonly Dictionary<int, (int? UserId, CsTeam Team)> _nativeHumanTeamChanges = new();
    private int? _replayRetentionBotQuotaBaseline;

    [ConsoleCommand("dtr_retain", "dtr_retain <t-order-code> <ct-order-code>")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void RetainCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (command.ArgCount == 2 && command.GetArg(1).Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _pendingReplayRetentionRanks.Clear();
            _pendingReplayRetentionTCode = null;
            _pendingReplayRetentionCtCode = null;
            command.ReplyToCommand("[DTR OK] pending replay retention priority cleared");
            return;
        }
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_retain <t-order-code:0-119> <ct-order-code:0-119>");
            return;
        }

        var firstArg = command.GetArg(1);
        var secondArg = command.GetArg(2);
        var firstLooksLikeCode = int.TryParse(
            firstArg,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _);
        var secondLooksLikeCode = int.TryParse(
            secondArg,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _);
        if (firstLooksLikeCode || secondLooksLikeCode)
        {
            if (!ReplayRetentionPriorityParser.TryParsePermutationCode(firstArg, out var tCode) ||
                !ReplayRetentionPriorityParser.TryParsePermutationCode(secondArg, out var ctCode))
            {
                command.ReplyToCommand("[DTR ERR] compact retention requires both T and CT order codes in the range 0-119");
                return;
            }

            _pendingReplayRetentionRanks.Clear();
            _pendingReplayRetentionTCode = tCode;
            _pendingReplayRetentionCtCode = ctCode;
            command.ReplyToCommand(
                $"[DTR OK] compact retention priority queued for the next manifest plan (T={tCode}, CT={ctCode})");
            return;
        }

        if (!ReplayRetentionPriorityParser.TryParseGroup(firstArg, out var firstGroup, out var firstError))
        {
            command.ReplyToCommand($"[DTR ERR] team A retention priority: {firstError}");
            return;
        }
        if (!ReplayRetentionPriorityParser.TryParseGroup(secondArg, out var secondGroup, out var secondError))
        {
            command.ReplyToCommand($"[DTR ERR] team B retention priority: {secondError}");
            return;
        }

        var next = new Dictionary<ulong, int>();
        if (!TryAddRetentionGroup(next, firstGroup, out var duplicate) ||
            !TryAddRetentionGroup(next, secondGroup, out duplicate))
        {
            command.ReplyToCommand($"[DTR ERR] SteamID64 {duplicate.ToString(CultureInfo.InvariantCulture)} appears in both retention groups");
            return;
        }

        _pendingReplayRetentionRanks.Clear();
        _pendingReplayRetentionTCode = null;
        _pendingReplayRetentionCtCode = null;
        foreach (var pair in next)
            _pendingReplayRetentionRanks[pair.Key] = pair.Value;
        command.ReplyToCommand(
            $"[DTR OK] retention priority queued for the next manifest plan ({next.Count} players)");
    }

    private static bool TryAddRetentionGroup(
        IDictionary<ulong, int> destination,
        IReadOnlyList<ulong> steamIds,
        out ulong duplicate)
    {
        for (var index = 0; index < steamIds.Count; index++)
        {
            var steamId = steamIds[index];
            if (destination.ContainsKey(steamId))
            {
                duplicate = steamId;
                return false;
            }
            destination[steamId] = index + 1;
        }

        duplicate = 0;
        return true;
    }

    private void ActivatePendingReplayRetentionPriority()
    {
        _activeReplayRetentionRanks.Clear();
        _activeReplayRetentionTCode = _pendingReplayRetentionTCode;
        _activeReplayRetentionCtCode = _pendingReplayRetentionCtCode;
        foreach (var pair in _pendingReplayRetentionRanks)
            _activeReplayRetentionRanks[pair.Key] = pair.Value;
        _pendingReplayRetentionRanks.Clear();
        _pendingReplayRetentionTCode = null;
        _pendingReplayRetentionCtCode = null;
    }

    private void ResolveActiveReplayRetentionPermutation(
        IReadOnlyList<ManifestFile> tFiles,
        IReadOnlyList<ManifestFile> ctFiles)
    {
        if (!_activeReplayRetentionTCode.HasValue || !_activeReplayRetentionCtCode.HasValue)
            return;

        var next = new Dictionary<ulong, int>();
        var resolved = TryAddDecodedRetentionSide(
                           next,
                           tFiles,
                           _activeReplayRetentionTCode.Value) &&
                       TryAddDecodedRetentionSide(
                           next,
                           ctFiles,
                           _activeReplayRetentionCtCode.Value);
        _activeReplayRetentionTCode = null;
        _activeReplayRetentionCtCode = null;
        if (!resolved)
        {
            Server.PrintToConsole(
                "dtr: compact retention ignored because the selected source sides are not a complete unique 5v5 roster");
            return;
        }

        _activeReplayRetentionRanks.Clear();
        foreach (var pair in next)
            _activeReplayRetentionRanks[pair.Key] = pair.Value;
    }

    private static bool TryAddDecodedRetentionSide(
        IDictionary<ulong, int> destination,
        IReadOnlyList<ManifestFile> files,
        int code)
    {
        var canonical = files
            .Select(file => file.SteamId)
            .Order()
            .ToArray();
        if (canonical.Length != ReplayRetentionPriorityParser.MaxPlayersPerTeam ||
            canonical.Any(steamId => steamId == 0) ||
            canonical.Distinct().Count() != canonical.Length ||
            !ReplayRetentionPriorityParser.TryDecodePermutation(code, out var indices))
        {
            return false;
        }

        for (var rank = 0; rank < indices.Length; rank++)
        {
            if (!destination.TryAdd(canonical[indices[rank]], rank + 1))
                return false;
        }
        return true;
    }

    private void ClearReplayRetentionPriority(bool clearPending)
    {
        _activeReplayRetentionRanks.Clear();
        _activeReplayRetentionTCode = null;
        _activeReplayRetentionCtCode = null;
        if (clearPending)
        {
            _pendingReplayRetentionRanks.Clear();
            _pendingReplayRetentionTCode = null;
            _pendingReplayRetentionCtCode = null;
        }
        RestoreReplayRetentionBotQuota();
        _pendingHumanTeamChangeSlots.Clear();
        _nativeHumanTeamChanges.Clear();
    }

    private int ResolveReplayRetentionRank(ulong steamId, int fallbackRank)
    {
        if (steamId != 0 && _activeReplayRetentionRanks.TryGetValue(steamId, out var rank))
            return rank;
        return Math.Clamp(fallbackRank, 1, ReplayRetentionPriorityParser.MaxPlayersPerTeam);
    }

    private void RegisterReplayRetentionJoinHook()
        => AddCommandListener("jointeam", OnJoinTeamForReplayRetention, HookMode.Pre);

    private void UnregisterReplayRetentionJoinHook()
        => RemoveCommandListener("jointeam", OnJoinTeamForReplayRetention, HookMode.Pre);

    private HookResult OnJoinTeamForReplayRetention(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } ||
            player.IsBot ||
            _botHiderBridge.IsManagedBot(player.Slot) ||
            command.ArgCount < 2 ||
            !TryParseJoinTeam(command.GetArg(1), out var destination))
        {
            return HookResult.Continue;
        }

        if (_nativeHumanTeamChanges.Remove(player.Slot, out var nativeChange) &&
            nativeChange.UserId == player.UserId &&
            nativeChange.Team == destination)
        {
            return HookResult.Continue;
        }

        if (player.Team == destination)
            return HookResult.Continue;

        if (_pendingHumanTeamChangeSlots.Contains(player.Slot))
            return HookResult.Handled;

        var snapshot = BuildTickPlayerSnapshot();
        // Team capacity is controller-based. Requiring a live pawn undercounts
        // bots while they are dead or between CT/T spawn transitions, which can
        // leave the engine reporting a full team after this hook continued.
        var destinationPlayers = snapshot.Controllers.Count(candidate =>
            candidate is { IsValid: true } &&
            candidate.UserId.HasValue &&
            candidate.Team == destination);
        if (destinationPlayers < StandardTeamSize)
            return HookResult.Continue;

        var candidates = BuildKickCandidates(snapshot)
            .Where(candidate => candidate.Team == destination && candidate.UserId.HasValue)
            .OrderByDescending(candidate => candidate.RetentionRank)
            .ThenByDescending(candidate => candidate.Slot)
            .ToList();
        if (candidates.Count == 0)
            return HookResult.Continue;

        var evicted = candidates[0];
        var joiningSlot = player.Slot;
        var joiningUserId = player.UserId;
        if (!TryReserveReplayRetentionBotQuota(joiningSlot, out var quotaError))
        {
            Server.PrintToConsole($"dtr: retained human join unavailable: {quotaError}");
            return HookResult.Continue;
        }

        if (!TryReleaseAndDisconnectReplayCandidate(evicted, "human_join_retention"))
        {
            FinishReplayRetentionBotQuotaReservation(joiningSlot);
            return HookResult.Continue;
        }

        var evictedUserId = evicted.UserId!.Value;
        Server.NextFrame(() => CompleteRetainedHumanTeamChange(
            joiningSlot,
            joiningUserId,
            destination,
            evicted.Slot,
            evictedUserId,
            elapsedFrames: 1));
        Server.PrintToConsole(
            $"dtr: retained human join team={destination}; released replay slot={evicted.Slot} keep_rank={evicted.RetentionRank}");
        return HookResult.Handled;
    }

    private bool TryReleaseAndDisconnectReplayCandidate(DtrKickCandidate candidate, string reason)
    {
        if (!TryReleaseReplayCandidate(
                candidate,
                reason,
                out var controller,
                out _,
                out _))
        {
            return false;
        }

        controller.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        return true;
    }

    private void CompleteRetainedHumanTeamChange(
        int slot,
        int? expectedUserId,
        CsTeam destination,
        int evictedSlot,
        int evictedUserId,
        int elapsedFrames)
    {
        if (!_pendingHumanTeamChangeSlots.Contains(slot))
            return;

        var current = Utilities.GetPlayerFromSlot(slot);
        if (current is not { IsValid: true } ||
            current.IsBot ||
            current.UserId != expectedUserId ||
            current.Team == destination)
        {
            FinishReplayRetentionBotQuotaReservation(slot);
            return;
        }

        var snapshot = BuildTickPlayerSnapshot();
        var evictedControllerStillPresent = snapshot.Controllers.Any(candidate =>
            candidate is { IsValid: true } &&
            candidate.Slot == evictedSlot &&
            candidate.UserId == evictedUserId);
        var destinationPlayers = snapshot.Controllers.Count(candidate =>
            candidate is { IsValid: true } &&
            candidate.UserId.HasValue &&
            candidate.Team == destination);
        if (evictedControllerStillPresent || destinationPlayers >= StandardTeamSize)
        {
            if (elapsedFrames < ReplayRetentionJoinMaxFrames)
            {
                Server.NextFrame(() => CompleteRetainedHumanTeamChange(
                    slot,
                    expectedUserId,
                    destination,
                    evictedSlot,
                    evictedUserId,
                    elapsedFrames + 1));
                return;
            }

            FinishReplayRetentionBotQuotaReservation(slot);
            Server.PrintToConsole(
                $"dtr: retained human join aborted slot={slot} team={destination}: evicted controller did not release");
            return;
        }

        try
        {
            // The original jointeam path owns bot_auto_vacate, team limits, and
            // population accounting. DTR only chooses and releases the replay
            // bot that yields its place.
            _nativeHumanTeamChanges[slot] = (expectedUserId, destination);
            current.ExecuteClientCommandFromServer(
                $"jointeam {((int)destination).ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"dtr: retained human join failed slot={slot} team={destination}: {ex.Message}");
        }
        finally
        {
            _nativeHumanTeamChanges.Remove(slot);
            FinishReplayRetentionBotQuotaReservation(slot);
        }
    }

    private bool TryReserveReplayRetentionBotQuota(int joiningSlot, out string error)
    {
        error = string.Empty;
        var botQuota = CounterStrikeSharp.API.Modules.Cvars.ConVar.Find(ReplayRetentionBotQuotaConVarName);
        if (botQuota == null)
        {
            error = $"{ReplayRetentionBotQuotaConVarName} was not found";
            return false;
        }

        if (_replayRetentionBotQuotaBaseline == null)
            _replayRetentionBotQuotaBaseline = botQuota.GetPrimitiveValue<int>();

        _pendingHumanTeamChangeSlots.Add(joiningSlot);
        botQuota.SetValue(ReplayRetentionPriorityParser.ReservedBotQuota(
            _replayRetentionBotQuotaBaseline.Value,
            _pendingHumanTeamChangeSlots.Count));
        return true;
    }

    private void FinishReplayRetentionBotQuotaReservation(int joiningSlot)
    {
        if (!_pendingHumanTeamChangeSlots.Remove(joiningSlot) ||
            !_replayRetentionBotQuotaBaseline.HasValue)
        {
            return;
        }

        var botQuota = CounterStrikeSharp.API.Modules.Cvars.ConVar.Find(ReplayRetentionBotQuotaConVarName);
        if (botQuota != null)
        {
            botQuota.SetValue(ReplayRetentionPriorityParser.ReservedBotQuota(
                _replayRetentionBotQuotaBaseline.Value,
                _pendingHumanTeamChangeSlots.Count));
        }

        if (_pendingHumanTeamChangeSlots.Count == 0)
            _replayRetentionBotQuotaBaseline = null;
    }

    private void RestoreReplayRetentionBotQuota()
    {
        if (!_replayRetentionBotQuotaBaseline.HasValue)
            return;

        var botQuota = CounterStrikeSharp.API.Modules.Cvars.ConVar.Find(ReplayRetentionBotQuotaConVarName);
        botQuota?.SetValue(_replayRetentionBotQuotaBaseline.Value);
        _replayRetentionBotQuotaBaseline = null;
    }

    private static bool TryParseJoinTeam(string value, out CsTeam team)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "2":
            case "t":
            case "terrorist":
                team = CsTeam.Terrorist;
                return true;
            case "3":
            case "ct":
            case "counterterrorist":
                team = CsTeam.CounterTerrorist;
                return true;
            default:
                team = CsTeam.None;
                return false;
        }
    }
}
