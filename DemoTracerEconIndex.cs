using CounterStrikeSharp.API;
using System.Globalization;
using System.Text.Json;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const string DemoTracerEconIndexFileName = "demotracer-econ-index.v1.json";
    private readonly HashSet<(int WeaponDefIndex, uint PaintKit)> _validWeaponCosmeticPaints = new();
    private readonly HashSet<uint> _validPaintKits = new();
    private readonly HashSet<int> _validKnifeCosmeticItemDefs = new();
    private readonly HashSet<int> _validGloveCosmeticItemDefs = new();
    private readonly HashSet<uint> _validAgentCosmeticItemDefs = new();
    private readonly HashSet<uint> _validStickerIds = new();
    private readonly HashSet<uint> _validKeychainIds = new();
    private readonly HashSet<uint> _validMusicKitIds = new();
    private readonly HashSet<uint> _validScoreboardFlairItemDefs = new();
    private bool _demoTracerEconIndexLoaded;
    private string _demoTracerEconIndexSnapshot = "unknown";

    private void LoadDemoTracerEconIndex()
    {
        ClearDemoTracerEconIndex();
        AddBuiltInLegacyBodygroupPaints();

        var path = Path.Combine(ModuleDirectory, DemoTracerEconIndexFileName);
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
            _demoTracerEconIndexSnapshot = ReadEconIndexSnapshot(root);
            ReadPaintPairs(root, "weapon_paints", _validWeaponCosmeticPaints, normalizeWeaponDefIndex: true);
            ReadPaintPairs(root, "legacy_bodygroup_paints", _legacyCosmeticPaints, normalizeWeaponDefIndex: true);
            ReadUIntSet(root, "paint_kit_ids", _validPaintKits);
            ReadIntSet(root, "knife_defidx", _validKnifeCosmeticItemDefs);
            ReadIntSet(root, "glove_defidx", _validGloveCosmeticItemDefs);
            ReadUIntSet(root, "agent_defidx", _validAgentCosmeticItemDefs);
            ReadUIntSet(root, "sticker_ids", _validStickerIds);
            ReadUIntSet(root, "keychain_ids", _validKeychainIds);
            ReadUIntSet(root, "music_kit_ids", _validMusicKitIds);
            ReadUIntSet(root, "scoreboard_flair_defidx", _validScoreboardFlairItemDefs);
            _demoTracerEconIndexLoaded = _validWeaponCosmeticPaints.Count > 0 &&
                                         _validPaintKits.Count > 0 &&
                                         _validStickerIds.Count > 0;

            Server.PrintToConsole(
                $"dtr: loaded econ index snapshot={_demoTracerEconIndexSnapshot} weapon_paints={_validWeaponCosmeticPaints.Count} legacy_bodygroups={_legacyCosmeticPaints.Count} paints={_validPaintKits.Count} stickers={_validStickerIds.Count} charms={_validKeychainIds.Count} music={_validMusicKitIds.Count} flair={_validScoreboardFlairItemDefs.Count}");
        }
        catch (Exception ex)
        {
            ClearDemoTracerEconIndex();
            AddBuiltInLegacyBodygroupPaints();
            Server.PrintToConsole($"dtr: failed to load econ index; validation will fail closed: {ex.Message}");
        }
    }

    private void ClearDemoTracerEconIndex()
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
        _demoTracerEconIndexLoaded = false;
        _demoTracerEconIndexSnapshot = "unknown";
    }

    private void AddBuiltInLegacyBodygroupPaints()
    {
        foreach (var (weaponDefIndex, paintKit) in BuiltInLegacyCosmeticPaints)
            _legacyCosmeticPaints.Add((NormalizeWeaponDefIndex(weaponDefIndex), (uint)paintKit));
    }

    private static string ReadEconIndexSnapshot(JsonElement root)
    {
        if (root.TryGetProperty("source", out var source) &&
            source.TryGetProperty("snapshot_date", out var snapshot) &&
            snapshot.ValueKind == JsonValueKind.String)
        {
            return snapshot.GetString() ?? "unknown";
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
}
