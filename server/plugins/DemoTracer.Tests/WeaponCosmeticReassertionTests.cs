/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class WeaponCosmeticReassertionTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    public void SilencedWeaponPaintCacheRequiresLiveEconState(int weaponDefIndex)
    {
        Assert.True(DemoTracerPlugin.WeaponPaintStateMatches(
            actualWeaponDefIndex: weaponDefIndex,
            actualPaintKit: 711,
            actualSeed: 420,
            actualWear: 0.125f,
            expectedWeaponDefIndex: weaponDefIndex,
            expectedPaintKit: 711,
            expectedSeed: 420,
            expectedWear: 0.125f));

        Assert.False(DemoTracerPlugin.WeaponPaintStateMatches(
            actualWeaponDefIndex: weaponDefIndex,
            actualPaintKit: 0,
            actualSeed: 420,
            actualWear: 0.125f,
            expectedWeaponDefIndex: weaponDefIndex,
            expectedPaintKit: 711,
            expectedSeed: 420,
            expectedWear: 0.125f));
    }

    [Theory]
    [InlineData(16, 711, 420, 0.125f)]
    [InlineData(60, 0, 420, 0.125f)]
    [InlineData(60, 711, 0, 0.125f)]
    [InlineData(60, 711, 420, 0.5f)]
    public void AnyOverwrittenPaintFieldInvalidatesTheCache(
        int actualWeaponDefIndex,
        int actualPaintKit,
        int actualSeed,
        float actualWear)
    {
        Assert.False(DemoTracerPlugin.WeaponPaintStateMatches(
            actualWeaponDefIndex,
            actualPaintKit,
            actualSeed,
            actualWear,
            expectedWeaponDefIndex: 60,
            expectedPaintKit: 711,
            expectedSeed: 420,
            expectedWear: 0.125f));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void PaintModelSelectionUsesCatalogLegacyEvidenceForEveryWeapon(
        bool usesLegacyModel,
        int expectedBodygroup)
    {
        Assert.Equal(
            expectedBodygroup,
            DemoTracerPlugin.WeaponPaintBodygroupValue(usesLegacyModel));
    }
}
