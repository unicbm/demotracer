/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class CosmeticDefinitionTests
{
    private static readonly ReplayEquipmentCatalog Catalog = ReplayEquipmentCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "cs2-lib-econ-index.v1.json"));

    [Fact]
    public void ZeusIsAWeaponCosmeticDefinition()
    {
        Assert.True(Catalog.IsWeaponCosmeticCategory(31));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(49)]
    public void EquipmentWithoutWeaponPaintsIsNotAWeaponCosmeticDefinition(int weaponDefIndex)
    {
        Assert.False(Catalog.IsWeaponCosmeticCategory(weaponDefIndex));
    }
}
