using System.Text.Json.Serialization;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private sealed class ConversionManifest
    {
        [JsonPropertyName("format_version")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("dtr_format_version")]
        public int DtrFormatVersion { get; set; }

        [JsonPropertyName("abi")]
        public int Abi { get; set; }

        [JsonPropertyName("map")]
        public string Map { get; set; } = string.Empty;

        [JsonPropertyName("tick_rate")]
        public float TickRate { get; set; }

        [JsonPropertyName("files")]
        public List<ManifestFile> Files { get; set; } = new();

        [JsonPropertyName("rounds")]
        public List<ManifestRound> Rounds { get; set; } = new();

        [JsonPropertyName("avatar_overrides")]
        public List<ManifestAvatarOverride> AvatarOverrides { get; set; } = new();

        public int EffectiveDtrFormatVersion => DtrFormatVersion != 0 ? DtrFormatVersion : FormatVersion;
    }

    private sealed class ManifestRound
    {
        [JsonPropertyName("round")]
        public int Round { get; set; }

        [JsonPropertyName("recording_start_tick")]
        public int RecordingStartTick { get; set; }

        [JsonPropertyName("start_tick")]
        public int StartTick { get; set; }

        [JsonPropertyName("end_tick")]
        public int EndTick { get; set; }

        [JsonPropertyName("original_end_tick")]
        public int OriginalEndTick { get; set; }

        [JsonPropertyName("bomb_planted_tick")]
        public int? BombPlantedTick { get; set; }

        [JsonPropertyName("freeze_preroll_ticks")]
        public int FreezePrerollTicks { get; set; }

        [JsonPropertyName("bomb_planted_seconds_after_live")]
        public float? BombPlantedSecondsAfterLive { get; set; }

        [JsonPropertyName("duration_seconds")]
        public float DurationSeconds { get; set; }

        [JsonPropertyName("pistol_round")]
        public bool PistolRound { get; set; }

        [JsonPropertyName("t_economy")]
        public ManifestTeamEconomy? TEconomy { get; set; }

        [JsonPropertyName("ct_economy")]
        public ManifestTeamEconomy? CtEconomy { get; set; }

        [JsonPropertyName("scoreboard")]
        public ReplayRoundScoreboard? Scoreboard { get; set; }

        [JsonPropertyName("chat_messages")]
        public List<ReplayChatMessage> ChatMessages { get; set; } = new();
    }

    private sealed class ManifestTeamEconomy
    {
        [JsonPropertyName("class")]
        public string Class { get; set; } = "unknown";
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("round")]
        public int Round { get; set; }

        [JsonPropertyName("side")]
        public string Side { get; set; } = string.Empty;

        [JsonPropertyName("steam_id")]
        public ulong SteamId { get; set; }

        [JsonPropertyName("player_name")]
        public string PlayerName { get; set; } = string.Empty;

        [JsonPropertyName("first_weapon_def_index")]
        public int? FirstWeaponDefIndex { get; set; }

        [JsonPropertyName("preload_weapon_def_indices")]
        public int[]? PreloadWeaponDefIndices { get; set; }

        [JsonPropertyName("loadout")]
        public ReplayLoadoutSnapshot? Loadout { get; set; }

        [JsonPropertyName("music_kit_id")]
        public uint? MusicKitId { get; set; }

        [JsonPropertyName("scoreboard_flair")]
        public ReplayScoreboardFlair? ScoreboardFlair { get; set; }

        [JsonPropertyName("cosmetics")]
        public ReplayCosmetics? Cosmetics { get; set; }

        [JsonPropertyName("view")]
        public ReplayView? View { get; set; }

        [JsonPropertyName("scoreboard")]
        public ReplayPlayerScoreboard? Scoreboard { get; set; }
    }

    private sealed class ManifestAvatarOverride
    {
        [JsonPropertyName("steam_id")]
        public ulong SteamId { get; set; }

        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("bytes")]
        public int Bytes { get; set; }
    }

    private sealed class ReplayLoadoutSnapshot
    {
        [JsonPropertyName("weapon_def_indices")]
        public int[]? WeaponDefIndices { get; set; }

        [JsonPropertyName("armor_value")]
        public uint ArmorValue { get; set; }

        [JsonPropertyName("has_helmet")]
        public bool HasHelmet { get; set; }

        [JsonPropertyName("has_defuser")]
        public bool HasDefuser { get; set; }
    }

    private sealed class ReplayView
    {
        [JsonPropertyName("crosshair_code")]
        public string? CrosshairCode { get; set; }

        [JsonPropertyName("viewmodel")]
        public ReplayViewmodel? Viewmodel { get; set; }
    }

    private sealed class ReplayViewmodel
    {
        [JsonPropertyName("left_handed")]
        public bool? LeftHanded { get; set; }

        [JsonPropertyName("fov")]
        public float? Fov { get; set; }

        [JsonPropertyName("offset_x")]
        public float? OffsetX { get; set; }

        [JsonPropertyName("offset_y")]
        public float? OffsetY { get; set; }

        [JsonPropertyName("offset_z")]
        public float? OffsetZ { get; set; }
    }

    private sealed class ReplayScoreboardFlair
    {
        [JsonPropertyName("item_def_index")]
        public uint ItemDefIndex { get; set; }
    }

    private sealed class ReplayRoundScoreboard
    {
        [JsonPropertyName("t_score")]
        public int TScore { get; set; }

        [JsonPropertyName("ct_score")]
        public int CtScore { get; set; }

        [JsonPropertyName("t_team_name")]
        public string? TTeamName { get; set; }

        [JsonPropertyName("ct_team_name")]
        public string? CtTeamName { get; set; }
    }

    private sealed class ReplayPlayerScoreboard
    {
        [JsonPropertyName("player_user_id")]
        public int? PlayerUserId { get; set; }

        [JsonPropertyName("player_entity_id")]
        public int? PlayerEntityId { get; set; }

        [JsonPropertyName("player_color")]
        public string? PlayerColor { get; set; }

        [JsonPropertyName("score")]
        public int? Score { get; set; }

        [JsonPropertyName("kills")]
        public int? Kills { get; set; }

        [JsonPropertyName("deaths")]
        public int? Deaths { get; set; }

        [JsonPropertyName("assists")]
        public int? Assists { get; set; }

        [JsonPropertyName("mvps")]
        public int? MVPs { get; set; }
    }

    private sealed class ReplayChatMessage
    {
        [JsonPropertyName("tick")]
        public int Tick { get; set; }

        [JsonPropertyName("sender_steam_id")]
        public ulong SenderSteamId { get; set; }

        [JsonPropertyName("sender_name")]
        public string? SenderName { get; set; }

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "all";

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    private sealed class ReplayCosmetics
    {
        [JsonPropertyName("weapons")]
        public List<ReplayWeaponCosmetic> Weapons { get; set; } = new();

        [JsonPropertyName("knife")]
        public ReplayItemCosmetic? Knife { get; set; }

        [JsonPropertyName("glove")]
        public ReplayItemCosmetic? Glove { get; set; }

        [JsonPropertyName("agent")]
        public ReplayAgentCosmetic? Agent { get; set; }
    }

    private sealed class ReplayWeaponCosmetic
    {
        [JsonPropertyName("weapon_def_index")]
        public int WeaponDefIndex { get; set; }

        [JsonPropertyName("paint_kit")]
        public uint PaintKit { get; set; }

        [JsonPropertyName("seed")]
        public uint Seed { get; set; }

        [JsonPropertyName("wear")]
        public float Wear { get; set; }

        [JsonPropertyName("quality")]
        public int? Quality { get; set; }

        [JsonPropertyName("stattrak_counter")]
        public int? StattrakCounter { get; set; }

        [JsonPropertyName("original_owner_steam_id")]
        public ulong? OriginalOwnerSteamId { get; set; }

        [JsonPropertyName("item_account_id")]
        public uint? ItemAccountId { get; set; }

        [JsonPropertyName("item_id")]
        public ulong? ItemId { get; set; }

        [JsonPropertyName("custom_name")]
        public string? CustomName { get; set; }

        [JsonPropertyName("stickers")]
        public List<ReplayWeaponSticker> Stickers { get; set; } = new();

        [JsonPropertyName("charms")]
        public List<ReplayWeaponCharm> Charms { get; set; } = new();
    }

    private sealed class ReplayWeaponSticker
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("sticker_id")]
        public uint StickerId { get; set; }

        [JsonPropertyName("wear")]
        public float Wear { get; set; }

        [JsonPropertyName("offset_x")]
        public float OffsetX { get; set; }

        [JsonPropertyName("offset_y")]
        public float OffsetY { get; set; }

        [JsonPropertyName("scale")]
        public float? Scale { get; set; }

        [JsonPropertyName("rotation")]
        public float? Rotation { get; set; }
    }

    private sealed class ReplayWeaponCharm
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("charm_id")]
        public uint CharmId { get; set; }

        [JsonPropertyName("offset_x")]
        public float OffsetX { get; set; }

        [JsonPropertyName("offset_y")]
        public float OffsetY { get; set; }

        [JsonPropertyName("offset_z")]
        public float OffsetZ { get; set; }

        [JsonPropertyName("seed")]
        public uint? Seed { get; set; }

        [JsonPropertyName("highlight")]
        public uint? Highlight { get; set; }

        [JsonPropertyName("sticker_id")]
        public uint? StickerId { get; set; }
    }

    private sealed class ReplayItemCosmetic
    {
        [JsonPropertyName("item_def_index")]
        public int? ItemDefIndex { get; set; }

        [JsonPropertyName("paint_kit")]
        public uint PaintKit { get; set; }

        [JsonPropertyName("seed")]
        public uint Seed { get; set; }

        [JsonPropertyName("seed_known")]
        public bool? SeedKnown { get; set; }

        [JsonPropertyName("wear")]
        public float Wear { get; set; }

        [JsonPropertyName("original_owner_steam_id")]
        public ulong? OriginalOwnerSteamId { get; set; }

        [JsonPropertyName("item_account_id")]
        public uint? ItemAccountId { get; set; }

        [JsonPropertyName("item_id")]
        public ulong? ItemId { get; set; }

        [JsonPropertyName("custom_name")]
        public string? CustomName { get; set; }
    }

    private sealed class ReplayAgentCosmetic
    {
        [JsonPropertyName("item_def_index")]
        public uint ItemDefIndex { get; set; }

        [JsonPropertyName("model_path")]
        public string ModelPath { get; set; } = string.Empty;

        [JsonPropertyName("name")]
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

        var formatVersion = manifest.EffectiveDtrFormatVersion;
        if (formatVersion != 0)
        {
            var minVersion = (int)BotControllerNative.MinRecFormatVersion;
            var maxVersion = (int)BotControllerNative.RecFormatVersion;
            if (formatVersion < minVersion || formatVersion > maxVersion)
            {
                throw new InvalidDataException(
                    $"manifest format_version {formatVersion} unsupported; expected {minVersion}..{maxVersion}");
            }
        }

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
        if (abi == 0)
            return;

        if (abi < MinManifestAbiVersion || abi > MaxManifestAbiVersion)
        {
            throw new InvalidDataException(
                $"manifest abi {abi} unsupported; expected {MinManifestAbiVersion}..{MaxManifestAbiVersion}");
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

    private string ResolveReadableManifestPath(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new FileNotFoundException("manifest path is empty", manifestPath);

        var normalized = manifestPath.Replace('/', Path.DirectorySeparatorChar);
        var candidates = new List<string>();
        if (Path.IsPathRooted(normalized))
        {
            candidates.Add(Path.GetFullPath(normalized));
        }
        else
        {
            candidates.Add(Path.GetFullPath(normalized));
            foreach (var gameDir in CandidateGameDirectories())
                candidates.Add(Path.GetFullPath(Path.Combine(gameDir, normalized)));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = string.Empty;
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate))
                continue;
            first = string.IsNullOrEmpty(first) ? candidate : first;
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("manifest file not found", string.IsNullOrEmpty(first) ? manifestPath : first);
    }

    private IEnumerable<string> CandidateGameDirectories()
    {
        foreach (var root in CandidatePathRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var current = new DirectoryInfo(root);
            while (current != null)
            {
                if (string.Equals(current.Name, "csgo", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(current.FullName, "addons")))
                {
                    yield return current.FullName;
                    yield break;
                }

                if (string.Equals(current.Name, "addons", StringComparison.OrdinalIgnoreCase) &&
                    current.Parent != null)
                {
                    yield return current.Parent.FullName;
                    yield break;
                }

                current = current.Parent;
            }
        }
    }

    private IEnumerable<string> CandidatePathRoots()
    {
        yield return ModuleDirectory;
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static ConversionManifest ReadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return DeserializeManifestJson<ConversionManifest>(
            manifestPath,
            json,
            "manifest");
    }

    private static T DeserializeManifestJson<T>(string path, string json, string manifestKind)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ManifestJsonOptions)
                   ?? throw new InvalidDataException($"{manifestKind} JSON is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{manifestKind} contains invalid JSON: {path} ({ex.Message})",
                ex);
        }
    }
}
