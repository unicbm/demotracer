/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;
using DemoTracerApi;
using DemoTracerBotHiderApi;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private LoadRoundResult LoadRound(
        string manifestPath,
        int round,
        bool switchingTeamsAtRoundReset = false)
        => LoadRoundSelection(
            manifestPath,
            round,
            round,
            steamIdMatch: false,
            switchingTeamsAtRoundReset);

    private LoadRoundResult LoadPlayoffRound(
        string manifestPath,
        int tRound,
        int ctRound,
        bool switchingTeamsAtRoundReset = false)
        => LoadRoundSelection(
            manifestPath,
            tRound,
            ctRound,
            steamIdMatch: true,
            switchingTeamsAtRoundReset);

    private LoadRoundResult LoadRoundSelection(
        string manifestPath,
        int tRound,
        int ctRound,
        bool steamIdMatch,
        bool switchingTeamsAtRoundReset)
    {
        var replayStateReplaced = false;
        var companionLeaseTransitionStarted = false;
        var loadSucceeded = false;
        var selectionLabel = steamIdMatch
            ? $"playoff T=r{tRound}/CT=r{ctRound}"
            : $"round {tRound}";
        try
        {
            var resolvedManifestPath = ResolveReadableManifestPath(manifestPath);
            if (!TryGetPrefetchedManifest(resolvedManifestPath, out var manifest) &&
                !TryReadManifest(resolvedManifestPath, out manifest, out var readError))
                return LoadRoundResult.Fail($"dtr: failed to read manifest: {readError}");
            if (!CurrentMapMatchesManifest(manifest.Map, out var currentMap))
            {
                return LoadRoundResult.Fail(
                    $"dtr: map mismatch, server=\"{currentMap}\" manifest=\"{manifest.Map}\" path=\"{resolvedManifestPath}\"");
            }

            var manifestDir = Path.GetDirectoryName(resolvedManifestPath) ?? ".";
            var avatarOverrides = BuildAvatarOverrideMap(manifest.AvatarOverrides);
            var allTFiles = tRound < 0
                ? []
                : SortReplayFilesForScoreboard(
                    manifest.Files.Where(file => file.Round == tRound),
                    "t");
            var allCtFiles = ctRound < 0
                ? []
                : SortReplayFilesForScoreboard(
                    manifest.Files.Where(file => file.Round == ctRound),
                    "ct");
            if (allTFiles.Count == 0 && allCtFiles.Count == 0)
                return LoadRoundResult.Fail($"dtr: manifest has no files for {selectionLabel}");

            ResolveActiveReplayRetentionPermutation(allTFiles, allCtFiles);

            var roundMetadata = !steamIdMatch && tRound == ctRound
                ? manifest.Rounds.FirstOrDefault(item => item.Round == tRound)
                : null;
            var roundScoreboard = roundMetadata?.Scoreboard;

            var targets = FindReplayTargets();
            var tBots = targets
                .Where(bot => ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(
                    bot.Team,
                    switchingTeamsAtRoundReset) == CsTeam.Terrorist)
                .ToList();
            var ctBots = targets
                .Where(bot => ReplayTeamAssignmentPolicy.ResolveUpcomingTeam(
                    bot.Team,
                    switchingTeamsAtRoundReset) == CsTeam.CounterTerrorist)
                .ToList();

            if (!_partialReplayEnabled && (tBots.Count < allTFiles.Count || ctBots.Count < allCtFiles.Count))
            {
                return LoadRoundResult.Fail(
                    $"dtr: not enough bots, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            List<ReplayAssignment> tAssignments;
            List<ReplayAssignment> ctAssignments;
            if (steamIdMatch)
            {
                if (!TryBuildSteamMatchedReplayAssignments(allTFiles, tBots, out tAssignments, out var tMatchError))
                    return LoadRoundResult.Fail($"dtr: {selectionLabel} T SteamID match failed: {tMatchError}");
                if (!TryBuildSteamMatchedReplayAssignments(allCtFiles, ctBots, out ctAssignments, out var ctMatchError))
                    return LoadRoundResult.Fail($"dtr: {selectionLabel} CT SteamID match failed: {ctMatchError}");
            }
            else
            {
                tAssignments = BuildReplayAssignments(allTFiles, tBots);
                ctAssignments = BuildReplayAssignments(allCtFiles, ctBots);
            }
            if (tAssignments.Count == 0 && ctAssignments.Count == 0)
            {
                return LoadRoundResult.Fail(
                    $"dtr: no safe bot targets, need T={allTFiles.Count}/CT={allCtFiles.Count}, have T={tBots.Count}/CT={ctBots.Count}");
            }

            var skippedT = allTFiles.Count - tAssignments.Count;
            var skippedCt = allCtFiles.Count - ctAssignments.Count;

            BeginBotHiderPresentationTransition();
            BeginBotRandomizerCosmeticLeaseTransition();
            companionLeaseTransitionStarted = true;
            StopAndUnloadLoaded(clearArmedPlan: true, releaseBuffers: false);
            replayStateReplaced = true;
            _session.LoadedRoundScoreboard = roundScoreboard;
            var loaded = new List<string>();
            if (!LoadSide(
                    tAssignments,
                    manifestDir,
                    avatarOverrides,
                    includeScoreboardEvidence: !steamIdMatch,
                    loaded,
                    out var loadError))
                return FailLoadRoundAfterPartialLoad(selectionLabel, loadError);
            if (!LoadSide(
                    ctAssignments,
                    manifestDir,
                    avatarOverrides,
                    includeScoreboardEvidence: !steamIdMatch,
                    loaded,
                    out loadError))
                return FailLoadRoundAfterPartialLoad(selectionLabel, loadError);

            ConfigureLoadedTeamAvatarOverrides(
                manifestDir,
                allTFiles,
                allCtFiles,
                avatarOverrides);
            ScheduleHumanTeamAvatarOverrideReconciliation();

            var voice = steamIdMatch
                ? string.Empty
                : ConfigureLoadedAutoVoiceClip(
                    resolvedManifestPath,
                    tRound,
                    roundMetadata,
                    manifest.TickRate);
            var chat = steamIdMatch
                ? string.Empty
                : ConfigureLoadedAutoChat(tRound, roundMetadata, manifest.TickRate);
            var partial = skippedT > 0 || skippedCt > 0
                ? $" partial replay skipped T={skippedT}/CT={skippedCt}"
                : string.Empty;
            var voiceStatus = string.IsNullOrWhiteSpace(voice)
                ? string.Empty
                : $" voice={voice}";
            var chatStatus = string.IsNullOrWhiteSpace(chat)
                ? string.Empty
                : $" chat={chat}";
            ReleaseUnusedWarmReplayBuffers();
            RetainLoadedBotHiderPresentation();
            loadSucceeded = true;
            return LoadRoundResult.Success($"dtr: loaded {loaded.Count} replays for {selectionLabel}{partial}{voiceStatus}{chatStatus}: {string.Join(", ", loaded)}");
        }
        catch (Exception ex)
        {
            if (replayStateReplaced)
                StopAndUnloadLoaded();
            return LoadRoundResult.Fail($"dtr: load round failed: {ex.Message}");
        }
        finally
        {
            FinishReplayPrefetchRound();
            if (!loadSucceeded)
                ReleaseUnusedWarmReplayBuffers();
            if (companionLeaseTransitionStarted)
            {
                EndBotHiderPresentationTransition();
                EndBotRandomizerCosmeticLeaseTransition();
            }
        }
    }

    private LoadRoundResult FailLoadRoundAfterPartialLoad(string selectionLabel, string error)
    {
        StopAndUnloadLoaded();
        return LoadRoundResult.Fail($"dtr: failed while loading {selectionLabel}: {error}");
    }

    private bool LoadSide(
        IReadOnlyList<ReplayAssignment> assignments,
        string manifestDir,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides,
        bool includeScoreboardEvidence,
        List<string> loaded,
        out string error)
    {
        error = string.Empty;
        foreach (var assignment in assignments)
        {
            var file = assignment.File;
            var bot = assignment.Bot;
            var slot = bot.Slot;
            if (!IsReplaySlotStillSafe(slot))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} target is no longer a safe bot";
                return false;
            }

            if (!TryResolveChildPathUnderRoot(manifestDir, file.Path, out var recPath, out var pathError))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} {pathError}";
                return false;
            }

            ReplayFileMetadata replayMetadata;
            var prefetchStatus = TryTakePrefetchedReplay(recPath, out var prefetchedReplay);
            bool loadedReplay;
            switch (prefetchStatus)
            {
                case DtrReplayPrefetchTakeStatus.Success:
                    loadedReplay = BotControllerNative.LoadReplay(
                        slot,
                        prefetchedReplay,
                        out replayMetadata);
                    break;
                case DtrReplayPrefetchTakeStatus.Pending:
                    error = $"{file.Side}:slot{slot}:{file.PlayerName} replay prefetch is still pending";
                    return false;
                case DtrReplayPrefetchTakeStatus.Failed:
                    error = $"{file.Side}:slot{slot}:{file.PlayerName} replay prefetch failed validation";
                    return false;
                default:
                    // Explicit/manual load paths do not necessarily create a
                    // prefetch generation and retain their synchronous behavior.
                    loadedReplay = BotControllerNative.LoadReplayFromFile(
                        slot,
                        recPath,
                        out replayMetadata);
                    break;
            }
            if (!loadedReplay)
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} {recPath} ({BotControllerNative.LastLoadError})";
                return false;
            }

            _session.WarmReplayBufferSlots.Remove(slot);
            RememberLoadedSlot(slot);
            TrackLoadedReplay(
                slot,
                recPath,
                file.PlayerName,
                file.SteamId,
                file.FirstWeaponDefIndex ?? -1,
                file.PreloadWeaponDefIndices,
                file.Loadout,
                NormalizeMusicKitId(file.MusicKitId),
                file.ScoreboardFlair,
                file.Cosmetics,
                file.View,
                includeScoreboardEvidence ? file.Scoreboard : null,
                manifestTeam: ReplayTeamFromManifestSide(file.Side),
                replayMetadata: replayMetadata,
                retentionRank: assignment.RetentionRank);
            if (!BotControllerNative.SetBuySkip(slot))
            {
                error = $"{file.Side}:slot{slot}:{file.PlayerName} failed to suppress native buying";
                return false;
            }
            TryApplyReplayIdentity(slot, file, manifestDir, avatarOverrides);
            loaded.Add($"{file.Side}:slot{slot}:{file.PlayerName}");
        }
        return true;
    }

    private List<ReplayAssignment> BuildReplayAssignments(
        IReadOnlyList<ManifestFile> files,
        IReadOnlyList<CCSPlayerController> bots)
    {
        var count = Math.Min(files.Count, bots.Count);
        var assignments = new List<ReplayAssignment>(count);
        var rankedFiles = files
            .Select((file, index) => new
            {
                File = file,
                Index = index,
                Rank = ResolveReplayRetentionRank(file.SteamId, index + 1),
            })
            .ToArray();
        var selectedFiles = ReplayRetentionPriorityParser
            .SelectPreferredIndices(rankedFiles.Select(item => item.Rank).ToArray(), count)
            .Select(index => rankedFiles[index])
            .ToArray();
        for (var i = 0; i < selectedFiles.Length; i++)
        {
            assignments.Add(new ReplayAssignment(
                selectedFiles[i].File,
                bots[i],
                selectedFiles[i].Rank));
        }
        return assignments;
    }

    private bool TryBuildSteamMatchedReplayAssignments(
        IReadOnlyList<ManifestFile> files,
        IReadOnlyList<CCSPlayerController> bots,
        out List<ReplayAssignment> assignments,
        out string error)
    {
        assignments = new List<ReplayAssignment>(bots.Count);
        error = string.Empty;
        var fileGroupsBySteamId = files
            .Where(file => file.SteamId != 0)
            .GroupBy(file => file.SteamId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var assignedSteamIds = new HashSet<ulong>();

        foreach (var bot in bots.OrderBy(candidate => candidate.Slot))
        {
            ulong steamId = 0;
            if (_session.LoadedReplays.TryGetValue(bot.Slot, out var loaded))
                steamId = loaded.SteamId;
            else if (_retainedBotHiderPresentation.TryGetValue(bot.Slot, out var retained))
                steamId = retained.SteamId;

            if (steamId == 0)
            {
                error = $"slot={bot.Slot} has no retained DTR SteamID evidence";
                return false;
            }
            if (!assignedSteamIds.Add(steamId))
            {
                error = $"SteamID {steamId} is assigned to more than one replay target";
                return false;
            }
            if (!fileGroupsBySteamId.TryGetValue(steamId, out var matchingFiles))
            {
                error = $"slot={bot.Slot} SteamID={steamId} has no source-side replay";
                return false;
            }
            if (matchingFiles.Length != 1)
            {
                error = $"slot={bot.Slot} SteamID={steamId} has {matchingFiles.Length} ambiguous source-side replays";
                return false;
            }

            var file = matchingFiles[0];
            var fallbackRank = files
                .Select((candidate, index) => (candidate, rank: index + 1))
                .First(item => ReferenceEquals(item.candidate, file))
                .rank;
            assignments.Add(new ReplayAssignment(
                file,
                bot,
                ResolveReplayRetentionRank(file.SteamId, fallbackRank)));
        }
        return true;
    }

    private static CsTeam? ReplayTeamFromManifestSide(string side)
        => side.Equals("t", StringComparison.OrdinalIgnoreCase)
            ? CsTeam.Terrorist
            : side.Equals("ct", StringComparison.OrdinalIgnoreCase)
                ? CsTeam.CounterTerrorist
                : null;

    private static List<ManifestFile> SortReplayFilesForScoreboard(
        IEnumerable<ManifestFile> files,
        string side)
    {
        return files
            .Where(file => file.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => ReplayPlayerColorSortOrder(file.Scoreboard?.PlayerColor))
            .ThenBy(file => file.Scoreboard?.PlayerUserId ?? int.MaxValue)
            .ThenBy(file => file.Scoreboard?.PlayerEntityId ?? int.MaxValue)
            .ThenBy(file => file.SteamId)
            .ToList();
    }

    private static Dictionary<ulong, ManifestAvatarOverride> BuildAvatarOverrideMap(
        IReadOnlyList<ManifestAvatarOverride> avatarOverrides)
    {
        var map = new Dictionary<ulong, ManifestAvatarOverride>();
        foreach (var avatar in avatarOverrides)
        {
            if (avatar.SteamId == 0)
                continue;

            map.TryAdd(avatar.SteamId, avatar);
        }
        return map;
    }

    private static int ReplayPlayerColorSortOrder(string? playerColor)
        => NormalizeReplayPlayerColor(playerColor) switch
        {
            "yellow" => 0,
            "blue" => 1,
            "purple" => 2,
            "green" => 3,
            "orange" => 4,
            _ => 100,
        };

    private static int ReplayPlayerColorSchemaIndex(string? playerColor)
        => NormalizeReplayPlayerColor(playerColor) switch
        {
            "blue" => 0,
            "green" => 1,
            "yellow" => 2,
            "orange" => 3,
            "purple" => 4,
            _ => -1,
        };

    private static string? NormalizeReplayPlayerColor(string? playerColor)
    {
        var normalized = playerColor?.Trim().ToLowerInvariant();
        return normalized is "blue" or "green" or "yellow" or "orange" or "purple"
            ? normalized
            : null;
    }

    private void TryApplyReplayIdentity(
        int slot,
        ManifestFile file,
        string manifestDir,
        IReadOnlyDictionary<ulong, ManifestAvatarOverride> avatarOverrides)
    {
        if (_replayIdentityMode == ReplayIdentityMode.Off)
            return;

        ManifestAvatarOverride? avatarOverride = null;
        var hasAvatarOverride =
            file.SteamId != 0 &&
            avatarOverrides.TryGetValue(file.SteamId, out avatarOverride);
        var avatarCommandPath = string.Empty;
        var avatarOverrideReady = false;
        if (hasAvatarOverride && avatarOverride != null &&
            _replayIdentityMode == ReplayIdentityMode.Avatar)
        {
            avatarOverrideReady = TryPrepareReplayAvatarOverride(
                file.SteamId,
                manifestDir,
                avatarOverride,
                out avatarCommandPath,
                out var avatarError);
            if (!avatarOverrideReady)
            {
                Server.PrintToConsole(
                    $"dtr: replay avatar skipped slot={slot} player={file.PlayerName} sid={file.SteamId} fallback=steam: {avatarError}");
            }
        }

        var writeSteamId = _replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar;
        if (writeSteamId && file.SteamId == 0)
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: missing steam_id");
            return;
        }

        if (!_botHiderBridge.IsAvailable())
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: BotHider unavailable");
            return;
        }

        if (!_botHiderBridge.IsManagedBot(slot))
        {
            Server.PrintToConsole(
                $"dtr: replay identity skipped slot={slot} player={file.PlayerName}: not a BotHider managed bot");
            return;
        }

        if (writeSteamId)
        {
            if (avatarOverrideReady && avatarOverride != null)
            {
                ScheduleReplayAvatarOverride(
                    slot,
                    file,
                    file.SteamId,
                    avatarOverride,
                    avatarCommandPath);
            }
        }
        if (writeSteamId && _replayIdentityMode == ReplayIdentityMode.Avatar)
        {
            Server.PrintToConsole(
                avatarOverrideReady
                    ? $"dtr: replay identity lease pending slot={slot} player={file.PlayerName} sid={file.SteamId} avatar=override"
                    : $"dtr: replay identity lease pending slot={slot} player={file.PlayerName} sid={file.SteamId} avatar=steam");
        }
        else
        {
            Server.PrintToConsole(
                writeSteamId
                    ? $"dtr: replay identity lease pending slot={slot} player={file.PlayerName} sid={file.SteamId}"
                    : $"dtr: replay identity lease pending slot={slot} player={file.PlayerName}");
        }
    }

    private bool ReplayIdentityShouldApplyScoreboardFlair()
        => _replayIdentityMode is ReplayIdentityMode.Steam or ReplayIdentityMode.Avatar;

    private void ScheduleReplayAvatarOverride(
        int slot,
        ManifestFile file,
        ulong avatarSteamId,
        ManifestAvatarOverride avatar,
        string commandPath)
    {
        if (file.SteamId == 0)
            return;

        var generation = CurrentReplayIdentityGeneration(slot);
        var steamId = file.SteamId;
        var playerName = file.PlayerName;
        Server.NextFrame(() =>
            TryApplyReplayAvatarOverride(
                slot,
                steamId,
                avatarSteamId,
                playerName,
                avatar,
                commandPath,
                generation));
    }

    private void TryApplyReplayAvatarOverride(
        int slot,
        ulong steamId,
        ulong avatarSteamId,
        string playerName,
        ManifestAvatarOverride avatar,
        string commandPath,
        long generation)
    {
        if (steamId == 0 ||
            !IsReplayIdentityGenerationCurrent(slot, generation))
        {
            return;
        }

        if (!_session.LoadedReplays.TryGetValue(slot, out var replay) ||
            replay.SteamId != steamId ||
            !IsReplaySlotStillSafe(slot) ||
            !_botHiderBridge.IsManagedBot(slot))
        {
            return;
        }

        // ServerAvatarOverrides is keyed only by SteamID. Never publish a
        // replay avatar unless this exact bot currently owns that exact
        // SteamID through the BotHider presentation lease; a live human may
        // legitimately own the same SteamID when the lease is rejected.
        if (!HasActiveBotHiderReplayIdentity(slot, steamId))
        {
            Server.PrintToConsole(
                $"dtr: replay avatar skipped slot={slot} player={playerName} sid={steamId}: exact identity lease inactive");
            return;
        }

        Server.ExecuteCommand(BuildAvatarOverrideCommand(
            avatarSteamId,
            commandPath,
            slot));
        ScheduleBotHiderAvatarIdentityReassert();
        Server.PrintToConsole(
            $"dtr: replay avatar queued slot={slot} player={playerName} sid={steamId} avatar_sid={avatarSteamId} path={avatar.Path} cache={commandPath}");
    }

    private bool TryPrepareReplayAvatarOverride(
        ulong steamId,
        string manifestDir,
        ManifestAvatarOverride avatar,
        out string commandPath,
        out string error)
    {
        commandPath = string.Empty;
        error = string.Empty;

        var format = avatar.Format.Trim();
        var pngFormat =
            (format.Length == 0 && avatar.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ||
            format.Equals("png", StringComparison.OrdinalIgnoreCase);
        if (!pngFormat)
        {
            error = $"unsupported format={avatar.Format}";
            return false;
        }

        if (!TryResolveChildPathUnderRoot(manifestDir, avatar.Path, out var avatarPath, out error))
            return false;
        if (!File.Exists(avatarPath))
        {
            error = $"missing {avatar.Path}";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(avatarPath);
            if (bytes.Length == 0)
            {
                error = "avatar PNG is empty";
                return false;
            }
            if (bytes.Length > AvatarOverrideMaxBytes)
            {
                error = "avatar PNG must be 16 KiB or smaller";
                return false;
            }
            if (!bytes.AsSpan().StartsWith(AvatarPngSignature))
            {
                error = "avatar file is not a PNG";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"avatar PNG validation failed: {ex.Message}";
            return false;
        }

        return TryPrepareAvatarOverrideCommandPath(
            steamId,
            avatarPath,
            avatar,
            out commandPath,
            out error);
    }

    private bool TryPrepareAvatarOverrideCommandPath(
        ulong steamId,
        string sourcePath,
        ManifestAvatarOverride avatar,
        out string commandPath,
        out string error)
    {
        commandPath = string.Empty;
        error = string.Empty;

        try
        {
            var pluginDir = ModuleDirectory;
            if (string.IsNullOrWhiteSpace(pluginDir))
                pluginDir = Path.GetDirectoryName(ModulePath);
            if (string.IsNullOrWhiteSpace(pluginDir))
                pluginDir = ".";

            var cacheDir = Path.Combine(pluginDir, AvatarOverrideCacheDirectoryName);
            Directory.CreateDirectory(cacheDir);

            var normalizedManifestPath = avatar.Path.Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(normalizedManifestPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                error = "avatar cache filename is empty";
                return false;
            }

            var contentHash = AvatarContentHashKey(sourcePath, avatar.Sha256);
            var pathHash = ShortSha256Hex($"{steamId}\n{avatar.Path}\n{contentHash}");
            var safeStem = SanitizeAvatarCacheStem(Path.GetFileNameWithoutExtension(fileName));
            var cachedName = $"{steamId}_{pathHash}_{safeStem}.png";
            var cachedPath = Path.Combine(cacheDir, cachedName);
            var sourceInfo = new FileInfo(sourcePath);
            var shouldCopy =
                !File.Exists(cachedPath) ||
                new FileInfo(cachedPath).Length != sourceInfo.Length;
            if (shouldCopy)
                File.Copy(sourcePath, cachedPath, overwrite: true);

            commandPath = cachedPath.Replace('\\', '/');
            return true;
        }
        catch (Exception ex)
        {
            error = $"avatar cache failed: {ex.Message}";
            return false;
        }
    }

    private static string AvatarContentHashKey(string sourcePath, string manifestSha256)
    {
        var normalized = NormalizeSha256(manifestSha256);
        if (normalized.Length >= 16)
            return normalized[..16];

        using var stream = File.OpenRead(sourcePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (Uri.IsHexDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static string ShortSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SanitizeAvatarCacheStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "avatar";

        var builder = new StringBuilder(Math.Min(value.Length, 48));
        foreach (var c in value)
        {
            if (builder.Length >= 48)
                break;
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return builder.Length == 0 ? "avatar" : builder.ToString();
    }

}
