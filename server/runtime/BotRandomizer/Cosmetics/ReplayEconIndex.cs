/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;

namespace BotRandomizer;

/// <summary>
/// Canonical allow-list for demo-backed cosmetic evidence. This is deliberately
/// separate from <see cref="CosmeticCatalog"/>, whose collections are curated
/// random-roll pools and may omit valid engine items such as default music kits.
/// </summary>
internal sealed class ReplayEconIndex
{
    private readonly HashSet<(ushort WeaponDefIndex, uint PaintKit)> _weaponPaints = [];
    private readonly HashSet<(ushort WeaponDefIndex, uint PaintKit)> _legacyWeaponPaints = [];
    private readonly HashSet<uint> _paintKits = [];
    private readonly HashSet<ushort> _knifeDefinitions = [];
    private readonly HashSet<ushort> _gloveDefinitions = [];
    private readonly HashSet<uint> _agentDefinitions = [];
    private readonly HashSet<uint> _stickerIds = [];
    private readonly HashSet<uint> _keychainIds = [];
    private readonly HashSet<int> _musicKitIds = [];

    private ReplayEconIndex()
    {
    }

    internal string SourceVersion { get; private set; } = string.Empty;
    internal int WeaponPaintCount => _weaponPaints.Count;
    internal int MusicKitCount => _musicKitIds.Count;

    internal static ReplayEconIndex Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var result = new ReplayEconIndex
        {
            SourceVersion = ReadSourceVersion(root)
        };
        if (string.IsNullOrWhiteSpace(result.SourceVersion))
            throw new InvalidDataException("replay econ index is not an @ianlucas/cs2-lib projection");

        ReadPaintPairs(root, "weapon_paints", result._weaponPaints);
        ReadPaintPairs(root, "legacy_bodygroup_paints", result._legacyWeaponPaints);
        ReadUIntSet(root, "paint_kit_ids", result._paintKits);
        ReadUShortSet(root, "knife_defidx", result._knifeDefinitions);
        ReadUShortSet(root, "glove_defidx", result._gloveDefinitions);
        ReadUIntSet(root, "agent_defidx", result._agentDefinitions);
        ReadUIntSet(root, "sticker_ids", result._stickerIds);
        ReadUIntSet(root, "keychain_ids", result._keychainIds);
        ReadIntSet(root, "music_kit_ids", result._musicKitIds);

        if (result._weaponPaints.Count == 0 ||
            result._paintKits.Count == 0 ||
            result._knifeDefinitions.Count == 0 ||
            result._gloveDefinitions.Count == 0 ||
            result._stickerIds.Count == 0 ||
            result._musicKitIds.Count == 0)
        {
            throw new InvalidDataException("replay econ index is incomplete");
        }
        return result;
    }

    internal bool TryGetWeaponPaint(ushort weaponDefIndex, uint paintKit, out bool legacy)
    {
        legacy = _legacyWeaponPaints.Contains((weaponDefIndex, paintKit));
        return _weaponPaints.Contains((weaponDefIndex, paintKit));
    }

    internal bool IsPaintKit(uint paintKit) => paintKit > 0 && _paintKits.Contains(paintKit);
    internal bool IsKnifeDefinition(ushort defIndex) => _knifeDefinitions.Contains(defIndex);
    internal bool IsGloveDefinition(ushort defIndex) => _gloveDefinitions.Contains(defIndex);
    internal bool IsAgentDefinition(uint defIndex) => _agentDefinitions.Contains(defIndex);
    internal bool IsSticker(uint stickerId) => stickerId > 0 && _stickerIds.Contains(stickerId);
    internal bool IsKeychain(uint keychainId) => keychainId > 0 && _keychainIds.Contains(keychainId);
    internal bool IsMusicKit(int musicKitId) => musicKitId > 0 && _musicKitIds.Contains(musicKitId);

    private static string ReadSourceVersion(JsonElement root)
    {
        if (root.TryGetProperty("source", out var source) &&
            source.TryGetProperty("package", out var package) &&
            package.ValueKind == JsonValueKind.String &&
            package.GetString() == "@ianlucas/cs2-lib" &&
            source.TryGetProperty("version", out var version) &&
            version.ValueKind == JsonValueKind.String)
        {
            return version.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static void ReadPaintPairs(
        JsonElement root,
        string propertyName,
        HashSet<(ushort WeaponDefIndex, uint PaintKit)> output)
    {
        if (!TryGetArray(root, propertyName, out var values))
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (!TryReadIntProperty(value, "weapon_defidx", out var weaponDefIndex) ||
                weaponDefIndex is <= 0 or > ushort.MaxValue ||
                !TryReadUIntProperty(value, "paint_kit", out var paintKit) ||
                paintKit == 0)
            {
                continue;
            }
            output.Add(((ushort)weaponDefIndex, paintKit));
        }
    }

    private static void ReadUShortSet(JsonElement root, string propertyName, HashSet<ushort> output)
    {
        if (!TryGetArray(root, propertyName, out var values))
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetInt32(out var parsed) && parsed is > 0 and <= ushort.MaxValue)
                output.Add((ushort)parsed);
        }
    }

    private static void ReadUIntSet(JsonElement root, string propertyName, HashSet<uint> output)
    {
        if (!TryGetArray(root, propertyName, out var values))
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetUInt32(out var parsed) && parsed > 0)
                output.Add(parsed);
        }
    }

    private static void ReadIntSet(JsonElement root, string propertyName, HashSet<int> output)
    {
        if (!TryGetArray(root, propertyName, out var values))
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetInt32(out var parsed) && parsed > 0)
                output.Add(parsed);
        }
    }

    private static bool TryGetArray(JsonElement root, string propertyName, out JsonElement values)
        => root.TryGetProperty(propertyName, out values) && values.ValueKind == JsonValueKind.Array;

    private static bool TryReadIntProperty(JsonElement value, string propertyName, out int parsed)
    {
        parsed = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt32(out parsed);
    }

    private static bool TryReadUIntProperty(JsonElement value, string propertyName, out uint parsed)
    {
        parsed = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(propertyName, out var property) &&
               property.TryGetUInt32(out parsed);
    }
}
