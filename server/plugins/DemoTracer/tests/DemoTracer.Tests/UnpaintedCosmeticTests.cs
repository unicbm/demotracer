/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DemoTracer.Tests;

public sealed class UnpaintedCosmeticTests
{
    [Fact]
    public void ManifestNormalizationPreservesVanillaDetailsAndRejectsInvalidTextureValues()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
        void Field(string name, object value) => typeof(DemoTracerPlugin).GetField(name, flags)!.SetValue(plugin, value);
        Field("_replayEquipment", ReplayEquipmentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "cs2-lib-econ-index.v1.json")));
        Field("_validPaintKits", new HashSet<uint>());
        Field("_validKnifeCosmeticItemDefs", new HashSet<int> { 519 });
        Field("_validStickerIds", new HashSet<uint> { 4793 });
        Field("_validKeychainIds", new HashSet<uint>());
        var type = typeof(DemoTracerPlugin).GetNestedType("ReplayCosmetics", BindingFlags.NonPublic)!;
        var input = JsonSerializer.Deserialize("""
            {
              "Weapons": [
                { "WeaponDefIndex": 7, "PaintKit": 0, "Seed": 0, "Wear": 0,
                  "ItemId": 123, "Stickers": [{ "Slot": 0, "StickerId": 4793, "Wear": 0 }] },
                { "WeaponDefIndex": 9, "PaintKit": 0, "Seed": 0, "Wear": -0.08 },
                { "WeaponDefIndex": 4, "PaintKit": 0, "Seed": 3, "Wear": 0 }
              ],
              "Knife": { "ItemDefIndex": 519, "PaintKit": 0, "Seed": 0, "Wear": 0, "CustomName": "Vanilla" },
              "Glove": { "ItemDefIndex": 5030, "PaintKit": 0, "Seed": 0, "Wear": 0 }
            }
            """, type)!;
        var normalized = typeof(DemoTracerPlugin).GetMethod("NormalizeReplayCosmetics", flags)!.Invoke(plugin, [input])!;
        var result = JsonSerializer.SerializeToElement(normalized, type);
        var weapon = Assert.Single(result.GetProperty("Weapons").EnumerateArray());
        Assert.Equal(7, weapon.GetProperty("WeaponDefIndex").GetInt32());
        Assert.Equal(4793, Assert.Single(weapon.GetProperty("Stickers").EnumerateArray()).GetProperty("StickerId").GetInt32());
        Assert.Equal("Vanilla", result.GetProperty("Knife").GetProperty("CustomName").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("Glove").ValueKind);

        var weaponType = typeof(DemoTracerPlugin).GetNestedType("ReplayWeaponCosmetic", BindingFlags.NonPublic)!;
        var normalizedWeapon = JsonSerializer.Deserialize(weapon.GetRawText(), weaponType)!;
        var accepts = typeof(DemoTracerPlugin).GetMethod("HasCompleteAuthoritativePaintEvidence", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)accepts.Invoke(null, [normalizedWeapon])!);
    }
}
