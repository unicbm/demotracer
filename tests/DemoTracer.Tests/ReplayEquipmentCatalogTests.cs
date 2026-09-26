/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayEquipmentCatalogTests
{
    private static readonly ReplayEquipmentCatalog Catalog = ReplayEquipmentCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "cs2-lib-econ-index.v1.json"));

    [Fact]
    public void SidearmVariantsRemainDistinctDefinitions()
    {
        Assert.Equal("weapon_hkp2000", Catalog.ByDefIndex[32].ClassName);
        Assert.Equal("weapon_usp_silencer", Catalog.ByDefIndex[61].ClassName);
        Assert.Equal(32, Catalog.ByClassName["weapon_hkp2000"].WeaponDefIndex);
        Assert.Equal(61, Catalog.ByClassName["weapon_usp_silencer"].WeaponDefIndex);
    }

    [Theory]
    [InlineData("weapon_hkp2000", 61, "weapon_usp_silencer")]
    [InlineData("weapon_m4a1", 60, "weapon_m4a1_silencer")]
    [InlineData("weapon_usp_silencer", 32, "weapon_hkp2000")]
    [InlineData("weapon_m4a1_silencer", 16, "weapon_m4a1")]
    [InlineData("weapon_hkp2000", 0, "weapon_hkp2000")]
    public void ObservedEconDefinitionWinsOverTransientEntityClass(
        string designerName,
        int itemDefinitionIndex,
        string expectedClassName)
    {
        Assert.Equal(
            expectedClassName,
            Catalog.ResolveObservedClassName(designerName, itemDefinitionIndex));
    }

    [Fact]
    public void KnifeVariantsNormalizeOnlyAfterTheCatalogIsLoaded()
    {
        Assert.Equal(42, Catalog.NormalizeWeaponDefIndex(515));
        Assert.Equal(61, Catalog.NormalizeWeaponDefIndex(61));
        Assert.Equal(515, ReplayEquipmentCatalog.Empty.NormalizeWeaponDefIndex(515));
    }

    [Fact]
    public void MissingReplayEquipmentFailsClosed()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");

        Assert.Throws<InvalidDataException>(() =>
            ReplayEquipmentCatalog.Parse(document.RootElement));
    }

    [Fact]
    public void DuplicateDefinitionsFailClosed()
    {
        const string json = """
            {
              "replay_equipment": [
                { "weapon_defidx": 32, "class_name": "weapon_hkp2000", "replay_slot": "secondary" },
                { "weapon_defidx": 32, "class_name": "weapon_usp_silencer", "replay_slot": "secondary" }
              ]
            }
            """;
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<InvalidDataException>(() =>
            ReplayEquipmentCatalog.Parse(document.RootElement));
    }
}
