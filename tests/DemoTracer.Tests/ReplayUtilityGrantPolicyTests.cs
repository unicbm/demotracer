/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayUtilityGrantPolicyTests
{
    private const ulong ReplaySteamId = 76561198000000001UL;
    private static readonly ReplayEquipmentCatalog Catalog = ReplayEquipmentCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "cs2-lib-econ-index.v1.json"));

    [Theory]
    [InlineData("item_pickup", 45)]
    [InlineData("item_transfer", 43)]
    public void MatchingUtilityAcquisitionsAreGranted(string kind, int weaponDefIndex)
    {
        var replayEvent = new ReplayHifiEvent
        {
            TickIndex = 961,
            Kind = kind,
            TargetSteamId = ReplaySteamId,
            WeaponDefIndex = weaponDefIndex,
            TargetCountAfter = 1
        };

        Assert.True(ReplayUtilityGrantPolicy.ShouldQueue(
            replayEvent,
            ReplaySteamId,
            inventoryBaselineTickIndex: 960,
            Catalog));
    }

    [Theory]
    [InlineData(959)]
    [InlineData(960)]
    public void AcquisitionsAlreadyRepresentedByTheLiveStartLoadoutAreRejected(uint tickIndex)
    {
        var replayEvent = new ReplayHifiEvent
        {
            TickIndex = tickIndex,
            Kind = "item_pickup",
            TargetSteamId = ReplaySteamId,
            WeaponDefIndex = 46,
            TargetCountAfter = 1
        };

        Assert.False(ReplayUtilityGrantPolicy.ShouldQueue(
            replayEvent,
            ReplaySteamId,
            inventoryBaselineTickIndex: 960,
            Catalog));
    }

    [Fact]
    public void LiveWeaponPurchaseIsNotHandledByUtilityGrantPath()
    {
        var replayEvent = new ReplayHifiEvent
        {
            TickIndex = 961,
            Kind = "item_pickup",
            TargetSteamId = ReplaySteamId,
            WeaponDefIndex = 36,
            TargetCountAfter = 1
        };

        Assert.False(ReplayUtilityGrantPolicy.ShouldQueue(
            replayEvent,
            ReplaySteamId,
            inventoryBaselineTickIndex: 960,
            Catalog));
    }

    [Theory]
    [InlineData("item_drop", 45, 76561198000000001UL)]
    [InlineData("item_pickup", 45, 76561198000000002UL)]
    public void NonAcquisitionsAndOtherPlayersAreRejected(
        string kind,
        int weaponDefIndex,
        ulong targetSteamId)
    {
        var replayEvent = new ReplayHifiEvent
        {
            TickIndex = 961,
            Kind = kind,
            TargetSteamId = targetSteamId,
            WeaponDefIndex = weaponDefIndex,
            TargetCountAfter = 1
        };

        Assert.False(ReplayUtilityGrantPolicy.ShouldQueue(
            replayEvent,
            ReplaySteamId,
            inventoryBaselineTickIndex: 960,
            Catalog));
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(1, -1, 1)]
    [InlineData(0, 2, 0)]
    public void UtilityCountUsesAmmoForStackedFlashbangs(
        int entityCount,
        int ammoCount,
        int expected)
    {
        Assert.Equal(
            expected,
            ReplayUtilityGrantPolicy.ObservedUtilityCount(entityCount, ammoCount));
    }
}
