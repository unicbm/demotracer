/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API;
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
    private ReplayEquipmentCatalog _replayEquipment = ReplayEquipmentCatalog.Empty;
    private bool _cs2LibEconIndexLoaded;
    private string _cs2LibEconIndexVersion = "unknown";

    private void LoadCs2LibEconIndex()
    {
        ClearCs2LibEconIndex();

        var path = Path.Combine(ModuleDirectory, Cs2LibEconIndexFileName);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var source = root.GetProperty("source");
            if (source.GetProperty("package").GetString() != "@ianlucas/cs2-lib")
                throw new InvalidDataException("econ index is not a recognized @ianlucas/cs2-lib projection");
            _cs2LibEconIndexVersion = source.GetProperty("version").GetString()!;
            _replayEquipment = ReplayEquipmentCatalog.Parse(root);
            ReadPaintPairs(root, "weapon_paints", _validWeaponCosmeticPaints);
            ReadPaintPairs(root, "legacy_bodygroup_paints", _legacyCosmeticPaints);
            ReadUIntSet(root, "paint_kit_ids", _validPaintKits);
            ReadIntSet(root, "knife_defidx", _validKnifeCosmeticItemDefs);
            ReadIntSet(root, "glove_defidx", _validGloveCosmeticItemDefs);
            ReadUIntSet(root, "agent_defidx", _validAgentCosmeticItemDefs);
            ReadUIntSet(root, "sticker_ids", _validStickerIds);
            ReadUIntSet(root, "keychain_ids", _validKeychainIds);
            ReadUIntSet(root, "music_kit_ids", _validMusicKitIds);
            ReadUIntSet(root, "scoreboard_flair_defidx", _validScoreboardFlairItemDefs);
            _cs2LibEconIndexLoaded = _validWeaponCosmeticPaints.Count > 0 &&
                                     _validPaintKits.Count > 0 &&
                                     _replayEquipment.ByClassName.Count > 0 &&
                                     _validStickerIds.Count > 0;

            Server.PrintToConsole(
                $"dtr: loaded cs2-lib econ index version={_cs2LibEconIndexVersion} equipment={_replayEquipment.ByClassName.Count} weapon_paints={_validWeaponCosmeticPaints.Count} legacy_bodygroups={_legacyCosmeticPaints.Count} paints={_validPaintKits.Count} stickers={_validStickerIds.Count} charms={_validKeychainIds.Count} music={_validMusicKitIds.Count} flair={_validScoreboardFlairItemDefs.Count}");
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
        _replayEquipment = ReplayEquipmentCatalog.Empty;
        _cs2LibEconIndexLoaded = false;
        _cs2LibEconIndexVersion = "unknown";
    }

    private void ReadPaintPairs(
        JsonElement root,
        string propertyName,
        HashSet<(int WeaponDefIndex, uint PaintKit)> output)
        => output.UnionWith(root.GetProperty(propertyName).EnumerateArray().Select(value => (
            NormalizeWeaponDefIndex(value.GetProperty("weapon_defidx").GetInt32()),
            value.GetProperty("paint_kit").GetUInt32())));

    private static void ReadIntSet(JsonElement root, string propertyName, HashSet<int> output)
        => output.UnionWith(root.GetProperty(propertyName).EnumerateArray().Select(value => value.GetInt32()));

    private static void ReadUIntSet(JsonElement root, string propertyName, HashSet<uint> output)
        => output.UnionWith(root.GetProperty(propertyName).EnumerateArray().Select(value => value.GetUInt32()));

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
