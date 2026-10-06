/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class ConversionManifest
    {
        public int FormatVersion { get; set; }
        public int Abi { get; set; }
        public string Map { get; set; } = string.Empty;
        public float TickRate { get; set; }
        public List<ManifestFile> Files { get; set; } = new();
        public List<ManifestRound> Rounds { get; set; } = new();
        public List<ManifestAvatarOverride> AvatarOverrides { get; set; } = new();
    }

    private sealed class ManifestRound
    {
        public int Round { get; set; }
        public int RecordingStartTick { get; set; }
        public int StartTick { get; set; }
        public int EndTick { get; set; }
        public int OriginalEndTick { get; set; }
        public int? BombPlantedTick { get; set; }
        public int FreezePrerollTicks { get; set; }
        public float? BombPlantedSecondsAfterLive { get; set; }
        public float DurationSeconds { get; set; }
        public bool PistolRound { get; set; }
        public ManifestTeamEconomy? TEconomy { get; set; }
        public ManifestTeamEconomy? CtEconomy { get; set; }
        public List<ReplayChatMessage> ChatMessages { get; set; } = new();
    }

    private sealed class ManifestTeamEconomy
    {
        public string Class { get; set; } = "unknown";
    }

    private sealed class ManifestFile
    {
        public string Path { get; set; } = string.Empty;
        public int Round { get; set; }
        public string Side { get; set; } = string.Empty;
        public ulong SteamId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public ReplayClan? Clan { get; set; }
        public int? FirstWeaponDefIndex { get; set; }
        public int[]? PreloadWeaponDefIndices { get; set; }
        public ReplayLoadoutSnapshot? Loadout { get; set; }
        public uint? MusicKitId { get; set; }
        public ReplayScoreboardFlair? ScoreboardFlair { get; set; }
        public ReplayCosmetics? Cosmetics { get; set; }
        public ReplayView? View { get; set; }
        // The archive groups identity and analysis fields under scoreboard.
        // Playback only consumes identity; match totals remain engine-owned.
        [JsonPropertyName("scoreboard")]
        public ReplayPlayerIdentity? PlayerIdentity { get; set; }
    }

    private sealed class ManifestAvatarOverride
    {
        public ulong SteamId { get; set; }
        public string Format { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int Bytes { get; set; }
    }

    internal sealed class ReplayClan
    {
        public string? Tag { get; set; }
        public uint? Id { get; set; }
    }

    private sealed class ReplayLoadoutSnapshot
    {
        public int[]? WeaponDefIndices { get; set; }
        public uint ArmorValue { get; set; }
        public bool HasHelmet { get; set; }
        public bool HasDefuser { get; set; }
    }

    private sealed class ReplayView
    {
        public string? CrosshairCode { get; set; }
        public ReplayViewmodel? Viewmodel { get; set; }
    }

    internal sealed class ReplayViewmodel
    {
        public bool? LeftHanded { get; set; }
        public float? Fov { get; set; }
        public float? OffsetX { get; set; }
        public float? OffsetY { get; set; }
        public float? OffsetZ { get; set; }
    }

    private sealed class ReplayScoreboardFlair
    {
        public uint ItemDefIndex { get; set; }
    }

    private sealed class ReplayPlayerIdentity
    {
        public int? PlayerUserId { get; set; }
        public int? PlayerEntityId { get; set; }
        public string? PlayerColor { get; set; }
    }

    private sealed class ReplayChatMessage
    {
        public int Tick { get; set; }
        public ulong SenderSteamId { get; set; }
        public string? SenderName { get; set; }
        public string Scope { get; set; } = "all";
        public string Text { get; set; } = string.Empty;
    }

    private sealed class ReplayCosmetics
    {
        public List<ReplayWeaponCosmetic> Weapons { get; set; } = new();
        public ReplayItemCosmetic? Knife { get; set; }
        public ReplayItemCosmetic? Glove { get; set; }
        public ReplayAgentCosmetic? Agent { get; set; }
    }

    private sealed class ReplayWeaponCosmetic
    {
        public int WeaponDefIndex { get; set; }
        public uint PaintKit { get; set; }
        public uint Seed { get; set; }
        public float Wear { get; set; }
        public int? Quality { get; set; }
        public int? StattrakCounter { get; set; }
        public ulong? OriginalOwnerSteamId { get; set; }
        public uint? ItemAccountId { get; set; }
        public ulong? ItemId { get; set; }
        public string? CustomName { get; set; }
        public List<ReplayWeaponSticker> Stickers { get; set; } = new();
        public List<ReplayWeaponCharm> Charms { get; set; } = new();
    }

    private sealed class ReplayWeaponSticker
    {
        public int Slot { get; set; }
        public uint StickerId { get; set; }
        public float Wear { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float? Scale { get; set; }
        public float? Rotation { get; set; }
    }

    private sealed class ReplayWeaponCharm
    {
        public int Slot { get; set; }
        public uint CharmId { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public uint? Seed { get; set; }
        public uint? Highlight { get; set; }
        public uint? StickerId { get; set; }
    }

    private sealed class ReplayItemCosmetic
    {
        public int? ItemDefIndex { get; set; }
        public uint PaintKit { get; set; }
        public uint Seed { get; set; }
        public bool? SeedKnown { get; set; }
        public float Wear { get; set; }
        public ulong? OriginalOwnerSteamId { get; set; }
        public uint? ItemAccountId { get; set; }
        public ulong? ItemId { get; set; }
        public string? CustomName { get; set; }
    }

    private sealed class ReplayAgentCosmetic
    {
        public uint ItemDefIndex { get; set; }
        public string ModelPath { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    private bool TryReadManifest(
        string manifestPath,
        out ConversionManifest manifest,
        out string error)
    {
        manifest = new ConversionManifest();
        error = string.Empty;

        try
        {
            var fullPath = ResolveReadableManifestPath(manifestPath);
            manifest = ReadManifest(fullPath);
            ValidateConversionManifest(fullPath, manifest);
            return true;
        }
        catch (FileNotFoundException)
        {
            error = $"file does not exist: {manifestPath}";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = $"directory does not exist: {manifestPath}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ValidateConversionManifest(string manifestPath, ConversionManifest manifest)
    {
        manifest.Files ??= new List<ManifestFile>();
        manifest.Rounds ??= new List<ManifestRound>();
        manifest.AvatarOverrides ??= new List<ManifestAvatarOverride>();
        ValidateManifestAbi(manifest.Abi);
        if (string.IsNullOrWhiteSpace(manifest.Map))
            throw new InvalidDataException("manifest map is required");

        if (manifest.FormatVersion != BotControllerNative.RecFormatVersion)
            throw new InvalidDataException(
                $"manifest format_version {manifest.FormatVersion} unsupported; expected {BotControllerNative.RecFormatVersion}; reconvert the demo with the current GUI");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        for (var i = 0; i < manifest.Files.Count; i++)
            ValidateManifestFile(manifest.Files[i], i, manifestDir, paths);
        ValidateManifestAvatarOverrides(manifest.AvatarOverrides, manifestDir);
    }

    private static void ValidateManifestFile(
        ManifestFile? file,
        int index,
        string manifestDir,
        HashSet<string> paths)
    {
        if (file == null)
            throw new InvalidDataException($"manifest file {index} is null");
        if (string.IsNullOrWhiteSpace(file.Path))
            throw new InvalidDataException($"manifest file {index} path is required");
        if (!file.Path.EndsWith(".dtr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"manifest file {index} path must point to .dtr: {file.Path}");
        if (!TryResolveChildPathUnderRoot(manifestDir, file.Path, out var fullPath, out var pathError))
            throw new InvalidDataException($"manifest file {index} {pathError}");
        if (!paths.Add(fullPath))
            throw new InvalidDataException($"duplicate manifest file path: {file.Path}");
        if (string.IsNullOrWhiteSpace(file.Side) ||
            !file.Side.Equals("t", StringComparison.OrdinalIgnoreCase) &&
            !file.Side.Equals("ct", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"manifest file {index} side must be t or ct: {file.Side}");
        }
    }

    private static void ValidateManifestAvatarOverrides(
        IReadOnlyList<ManifestAvatarOverride> avatarOverrides,
        string manifestDir)
    {
        var steamIds = new HashSet<ulong>();
        for (var i = 0; i < avatarOverrides.Count; i++)
        {
            var avatar = avatarOverrides[i];
            if (avatar == null)
                throw new InvalidDataException($"manifest avatar override {i} is null");
            if (avatar.SteamId == 0)
                throw new InvalidDataException($"manifest avatar override {i} steam_id is required");
            if (!steamIds.Add(avatar.SteamId))
                throw new InvalidDataException($"duplicate manifest avatar override steam_id: {avatar.SteamId}");
            if (string.IsNullOrWhiteSpace(avatar.Path))
                throw new InvalidDataException($"manifest avatar override {i} path is required");
            if (!TryResolveChildPathUnderRoot(manifestDir, avatar.Path, out _, out var pathError))
                throw new InvalidDataException($"manifest avatar override {i} {pathError}");
        }
    }

    private static void ValidateManifestAbi(int abi)
    {
        if (abi != ManifestAbiVersion)
        {
            throw new InvalidDataException(
                $"manifest abi {abi} unsupported; expected {ManifestAbiVersion}; reconvert the demo with the current GUI");
        }
    }

    private static bool ManifestContainsSourceRound(
        ConversionManifest manifest,
        int sourceRound,
        out string error)
    {
        error = string.Empty;
        var rounds = manifest.Files
            .Select(file => file.Round)
            .Distinct()
            .Order()
            .ToArray();
        if (rounds.Contains(sourceRound))
            return true;

        error = $"[DTR ERR] source_round={sourceRound} was not found in manifest. [DTR HINT] Available source rounds: {string.Join(", ", rounds)}.";
        return false;
    }

    private static bool TryResolveChildPathUnderRoot(
        string rootDir,
        string childPath,
        out string fullPath,
        out string error)
        => TryResolveRelativePathUnderRoot(rootDir, rootDir, childPath, out fullPath, out error);

    private static bool TryResolveRelativePathUnderRoot(
        string rootDir,
        string baseDir,
        string childPath,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(childPath))
        {
            error = "manifest child path is empty";
            return false;
        }
        if (Path.IsPathRooted(childPath))
        {
            error = $"manifest child path must be relative: {childPath}";
            return false;
        }

        var root = Path.GetFullPath(rootDir);
        var basePath = Path.GetFullPath(baseDir);
        fullPath = Path.GetFullPath(Path.Combine(basePath, childPath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal))
        {
            error = $"manifest child path escapes manifest directory: {childPath}";
            fullPath = string.Empty;
            return false;
        }

        return true;
    }

    private static string ResolveReadableManifestPath(string manifestPath)
    {
        var path = manifestPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(path) || File.Exists(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, Server.GameDirectory);
    }

    private static ConversionManifest ReadManifest(string manifestPath)
        => JsonSerializer.Deserialize<ConversionManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions)
           ?? throw new InvalidDataException($"manifest JSON is empty: {manifestPath}");
}
