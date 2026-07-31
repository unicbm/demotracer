using CounterStrikeSharp.API;
using System.Globalization;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const string Cs2LibEconIndexFileName = "cs2-lib-econ-index.v1.json";
    private readonly HashSet<(int WeaponDefIndex, uint PaintKit)> _validWeaponCosmeticPaints = new();
    private readonly HashSet<uint> _validPaintKits = new();
    private readonly HashSet<int> _validKnifeCosmeticItemDefs = new();
    private readonly HashSet<int> _validGloveCosmeticItemDefs = new();
    private readonly HashSet<uint> _validAgentCosmeticItemDefs = new();
    private readonly HashSet<uint> _validStickerIds = new();
    private readonly HashSet<uint> _validKeychainIds = new();
    private readonly HashSet<uint> _validMusicKitIds = new();
    private readonly HashSet<uint> _validScoreboardFlairItemDefs = new();
    private static ReplayEquipmentCatalog ReplayEquipment = LoadAdjacentReplayEquipment();
    private bool _cs2LibEconIndexLoaded;
    private string _cs2LibEconIndexVersion = "unknown";

    private void LoadCs2LibEconIndex()
    {
        ClearCs2LibEconIndex();

        var path = Path.Combine(ModuleDirectory, Cs2LibEconIndexFileName);
        if (!File.Exists(path))
        {
            Server.PrintToConsole(
                $"dtr: econ index not found; cosmetic/music/flair validation will fail closed path={path}");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            _cs2LibEconIndexVersion = ReadEconIndexVersion(root);
            if (_cs2LibEconIndexVersion == "unknown")
                throw new InvalidDataException("econ index is not a recognized @ianlucas/cs2-lib projection");
            ReadPaintPairs(root, "weapon_paints", _validWeaponCosmeticPaints, normalizeWeaponDefIndex: true);
            ReadPaintPairs(root, "legacy_bodygroup_paints", _legacyCosmeticPaints, normalizeWeaponDefIndex: true);
            ReadUIntSet(root, "paint_kit_ids", _validPaintKits);
            ReplayEquipment = ParseReplayEquipment(root);
            ReadIntSet(root, "knife_defidx", _validKnifeCosmeticItemDefs);
            ReadIntSet(root, "glove_defidx", _validGloveCosmeticItemDefs);
            ReadUIntSet(root, "agent_defidx", _validAgentCosmeticItemDefs);
            ReadUIntSet(root, "sticker_ids", _validStickerIds);
            ReadUIntSet(root, "keychain_ids", _validKeychainIds);
            ReadUIntSet(root, "music_kit_ids", _validMusicKitIds);
            ReadUIntSet(root, "scoreboard_flair_defidx", _validScoreboardFlairItemDefs);
            _cs2LibEconIndexLoaded = _validWeaponCosmeticPaints.Count > 0 &&
                                     _validPaintKits.Count > 0 &&
                                     ReplayEquipment.ByClassName.Count > 0 &&
                                     _validStickerIds.Count > 0;

            Server.PrintToConsole(
                $"dtr: loaded cs2-lib econ index version={_cs2LibEconIndexVersion} equipment={ReplayEquipment.ByClassName.Count} weapon_paints={_validWeaponCosmeticPaints.Count} legacy_bodygroups={_legacyCosmeticPaints.Count} paints={_validPaintKits.Count} stickers={_validStickerIds.Count} charms={_validKeychainIds.Count} music={_validMusicKitIds.Count} flair={_validScoreboardFlairItemDefs.Count}");
        }
        catch (Exception ex)
        {
            ClearCs2LibEconIndex();
            Server.PrintToConsole($"dtr: failed to load cs2-lib econ index; validation will fail closed: {ex.Message}");
        }
    }

    private void ClearCs2LibEconIndex()
    {
        _validWeaponCosmeticPaints.Clear();
        _validPaintKits.Clear();
        _validKnifeCosmeticItemDefs.Clear();
        _validGloveCosmeticItemDefs.Clear();
        _validAgentCosmeticItemDefs.Clear();
        _validStickerIds.Clear();
        _validKeychainIds.Clear();
        _validMusicKitIds.Clear();
        _validScoreboardFlairItemDefs.Clear();
        _legacyCosmeticPaints.Clear();
        ReplayEquipment = ReplayEquipmentCatalog.Empty;
        _cs2LibEconIndexLoaded = false;
        _cs2LibEconIndexVersion = "unknown";
    }

    private static string ReadEconIndexVersion(JsonElement root)
    {
        if (root.TryGetProperty("source", out var source) &&
            source.TryGetProperty("package", out var package) &&
            package.ValueKind == JsonValueKind.String &&
            package.GetString() == "@ianlucas/cs2-lib" &&
            source.TryGetProperty("version", out var version) &&
            version.ValueKind == JsonValueKind.String)
        {
            return version.GetString() ?? "unknown";
        }
        return "unknown";
    }

    private static void ReadPaintPairs(
        JsonElement root,
        string propertyName,
        HashSet<(int WeaponDefIndex, uint PaintKit)> output,
        bool normalizeWeaponDefIndex)
    {
        if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.EnumerateArray())
        {
            if (!TryReadIntProperty(value, "weapon_defidx", out var weaponDefIndex) ||
                !TryReadUIntProperty(value, "paint_kit", out var paintKit) ||
                paintKit == 0)
            {
                continue;
            }

            output.Add((normalizeWeaponDefIndex ? NormalizeWeaponDefIndex(weaponDefIndex) : weaponDefIndex, paintKit));
        }
    }

    private static ReplayEquipmentCatalog LoadAdjacentReplayEquipment()
    {
        try
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(DemoTracerPlugin).Assembly.Location);
            var path = Path.Combine(assemblyDirectory ?? string.Empty, Cs2LibEconIndexFileName);
            if (!File.Exists(path))
                return ReplayEquipmentCatalog.Empty;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return ParseReplayEquipment(document.RootElement);
        }
        catch
        {
            return ReplayEquipmentCatalog.Empty;
        }
    }

    private static ReplayEquipmentCatalog ParseReplayEquipment(JsonElement root)
    {
        if (!root.TryGetProperty("replay_equipment", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("econ index is missing replay_equipment");
        }

        var byClassName = new Dictionary<string, ReplayEquipmentDefinition>(
            StringComparer.OrdinalIgnoreCase);
        var byDefIndex = new Dictionary<int, ReplayEquipmentDefinition>();
        foreach (var value in values.EnumerateArray())
        {
            if (!TryReadIntProperty(value, "weapon_defidx", out var weaponDefIndex) ||
                !value.TryGetProperty("class_name", out var classNameValue) ||
                classNameValue.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("replay_slot", out var replaySlotValue) ||
                replaySlotValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("econ index contains invalid replay equipment");
            }

            var className = classNameValue.GetString();
            var replaySlot = replaySlotValue.GetString() switch
            {
                "primary" => ReplayWeaponSlot.Primary,
                "secondary" => ReplayWeaponSlot.Secondary,
                "utility" => ReplayWeaponSlot.Utility,
                "c4" => ReplayWeaponSlot.C4,
                "taser" => ReplayWeaponSlot.Taser,
                "knife" => ReplayWeaponSlot.Knife,
                _ => ReplayWeaponSlot.Other
            };
            var definition = new ReplayEquipmentDefinition(weaponDefIndex, className ?? string.Empty, replaySlot);
            if (string.IsNullOrWhiteSpace(className) ||
                replaySlot == ReplayWeaponSlot.Other ||
                !byClassName.TryAdd(className, definition) ||
                !byDefIndex.TryAdd(weaponDefIndex, definition))
            {
                throw new InvalidDataException("econ index contains duplicate or unsupported replay equipment");
            }
        }
        return new ReplayEquipmentCatalog(byClassName, byDefIndex);
    }

    private static void ReadIntSet(JsonElement root, string propertyName, HashSet<int> output)
    {
        if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.EnumerateArray())
        {
            if (TryReadInt(value, out var parsed))
                output.Add(parsed);
        }
    }

    private static void ReadUIntSet(JsonElement root, string propertyName, HashSet<uint> output)
    {
        if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.EnumerateArray())
        {
            if (TryReadUInt(value, out var parsed) && parsed > 0)
                output.Add(parsed);
        }
    }

    private static bool TryReadIntProperty(JsonElement value, string propertyName, out int parsed)
    {
        parsed = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(propertyName, out var property) &&
               TryReadInt(property, out parsed);
    }

    private static bool TryReadUIntProperty(JsonElement value, string propertyName, out uint parsed)
    {
        parsed = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(propertyName, out var property) &&
               TryReadUInt(property, out parsed);
    }

    private static bool TryReadInt(JsonElement value, out int parsed)
    {
        parsed = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out parsed),
            JsonValueKind.String => int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed),
            _ => false
        };
    }

    private static bool TryReadUInt(JsonElement value, out uint parsed)
    {
        parsed = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetUInt32(out parsed),
            JsonValueKind.String => uint.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed),
            _ => false
        };
    }

    private bool IsKnownWeaponCosmeticPaint(int weaponDefIndex, uint paintKit)
        => _validWeaponCosmeticPaints.Contains((NormalizeWeaponDefIndex(weaponDefIndex), paintKit));

    private bool IsKnownPaintKit(uint paintKit)
        => _validPaintKits.Contains(paintKit);

    private bool IsKnownKnifeCosmeticItemDefIndex(int itemDefIndex)
        => _validKnifeCosmeticItemDefs.Contains(itemDefIndex);

    private bool IsKnownGloveCosmeticItemDefIndex(int itemDefIndex)
        => _validGloveCosmeticItemDefs.Contains(itemDefIndex);

    private bool IsKnownAgentCosmeticItemDefIndex(uint itemDefIndex)
        => _validAgentCosmeticItemDefs.Contains(itemDefIndex);

    private bool IsKnownStickerId(uint stickerId)
        => _validStickerIds.Contains(stickerId);

    private bool IsKnownKeychainId(uint keychainId)
        => _validKeychainIds.Contains(keychainId);

    private bool IsKnownMusicKitId(int musicKitId)
        => musicKitId > 0 && _validMusicKitIds.Contains((uint)musicKitId);

    private bool IsKnownScoreboardFlairItemDefIndex(uint itemDefIndex)
        => itemDefIndex == 0 || _validScoreboardFlairItemDefs.Contains(itemDefIndex);

    private readonly record struct ReplayEquipmentDefinition(
        int WeaponDefIndex,
        string ClassName,
        ReplayWeaponSlot Slot);

    private sealed record ReplayEquipmentCatalog(
        IReadOnlyDictionary<string, ReplayEquipmentDefinition> ByClassName,
        IReadOnlyDictionary<int, ReplayEquipmentDefinition> ByDefIndex)
    {
        public static ReplayEquipmentCatalog Empty { get; } = new(
            new Dictionary<string, ReplayEquipmentDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, ReplayEquipmentDefinition>());
    }
}
