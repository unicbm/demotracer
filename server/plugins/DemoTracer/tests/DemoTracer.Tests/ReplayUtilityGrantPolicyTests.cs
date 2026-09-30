/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/


namespace DemoTracer.Tests;

public sealed class ReplayUtilityGrantPolicyTests
{
    private static readonly ReplayEquipmentCatalog Catalog = ReplayEquipmentCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "cs2-lib-econ-index.v1.json"));

    [Theory]
    [InlineData("item_pickup", 45, 961u, 1ul, null, "weapon_smokegrenade")]
    [InlineData("item_transfer", 43, 961u, 1ul, null, "weapon_flashbang")]
    [InlineData("ITEM_PICKUP", null, 961u, 1ul, "decoy_grenade", "weapon_decoy")]
    [InlineData("item_pickup", 45, 959u, 1ul, null, null)]
    [InlineData("item_pickup", 45, 960u, 1ul, null, null)]
    [InlineData("item_pickup", 36, 961u, 1ul, null, null)]
    [InlineData("item_drop", 45, 961u, 1ul, null, null)]
    [InlineData("item_pickup", 45, 961u, 2ul, null, null)]
    [InlineData("bomb_pickup", 49, 961u, 1ul, null, null)]
    public void LegacyEventsCompileToResolvedUtilityOnly(string kind, int? def, uint tick,
        ulong target, string? itemName, string? expected)
    {
        var source = new ReplayHifiEvent { TickIndex = tick, Tick = 2000, Kind = kind,
            TargetSteamId = target, WeaponDefIndex = def, ItemName = itemName, TargetCountAfter = 2 };
        var plan = ReplayUtilityGrantPolicy.Compile([source], 1, 960, Catalog);
        if (expected == null) Assert.Empty(plan);
        else Assert.Equal(new ReplayUtilityGrant(tick, expected, 2, 2000), Assert.Single(plan));
        source.ItemName = "weapon_c4"; // The execution plan no longer depends on mutable evidence.
        source.TargetCountAfter = 64;
        if (expected != null) Assert.Equal(2, plan[0].TargetCount);
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(1, -1, 1)]
    [InlineData(0, 2, 0)]
    public void UtilityCountUsesAmmoForStackedFlashbangs(int entities, int ammo, int expected)
        => Assert.Equal(expected, ReplayUtilityGrantPolicy.ObservedUtilityCount(entities, ammo));
}
