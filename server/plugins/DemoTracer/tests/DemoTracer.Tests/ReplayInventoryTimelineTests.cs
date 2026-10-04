/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ReplayInventoryTimelineTests
{
    private static ReplayInventorySnapshot At(uint tick, int[] weapons, int[] acquired,
        int armor = 0, bool helmet = false, bool kit = false, byte gear = 0) => new()
        {
            TickIndex = tick, ArmorValue = armor, HasHelmet = helmet, HasDefuser = kit, GearAcquired = gear,
            WeaponDefCounts = weapons.GroupBy(def => def).Select(group => new ReplayInventoryItemCount
                { WeaponDefIndex = group.Key, Count = group.Count(), Acquired = acquired.Contains(group.Key) }).ToArray(),
        };

    [Theory]
    [InlineData(0, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(200, true)]
    public void StartUsesOnlyEquipmentAlreadyAcquired(uint cursor, bool bought)
    {
        var timeline = new ReplayInventoryTimeline([At(0, weapons: [4], acquired: [4]), At(100, [4, 7], [7], 100, true, true, 7)]);
        timeline.Start(cursor);
        Assert.Equal(bought, timeline.PendingWeapons.ContainsKey(7));
        Assert.Equal(bought ? 100 : 0, timeline.Armor);
        Assert.Equal(bought, timeline.Helmet);
        Assert.Equal(bought, timeline.Defuser);
        Assert.False(timeline.Advance(cursor));
    }

    [Fact]
    public void PurchaseDoesNotRefillOtherConsumedItemsOrRepairCombatDamage()
    {
        var timeline = new ReplayInventoryTimeline([
            At(0, [7, 43, 43], [7, 43], 100, true, false, 3),
            At(10, [7, 43, 43, 45], [45], 60, true),
            At(20, [7, 43, 43, 45], [], 60, true, true, 4),
        ]);
        timeline.Start(0);
        timeline.PendingWeapons.Clear(); // Initial grant completed; server then consumes a flash.
        timeline.ClearGear();
        Assert.True(timeline.Advance(10));
        Assert.Equal(new Dictionary<int, int> { [45] = 1 }, timeline.PendingWeapons);
        Assert.Null(timeline.Armor);
        Assert.Null(timeline.Helmet);
        timeline.PendingWeapons.Clear();
        Assert.True(timeline.Advance(20));
        Assert.Empty(timeline.PendingWeapons);
        Assert.Null(timeline.Armor); // A kit purchase does not restore the recorded armor.
        Assert.True(timeline.Defuser);
    }

    [Fact]
    public void DroppedItemsCancelUnfinishedGrantsAndCanBeAcquiredAgain()
    {
        var timeline = new ReplayInventoryTimeline([
            At(0, weapons: [7, 43, 43], acquired: [7, 43]), At(10, weapons: [43], acquired: []),
            At(20, weapons: [7, 43], acquired: [7]),
        ]);
        timeline.Start(0);
        timeline.ClearGear();
        Assert.True(timeline.Advance(10));
        Assert.False(timeline.PendingWeapons.ContainsKey(7));
        Assert.Equal(1, timeline.PendingWeapons[43]);
        timeline.PendingWeapons.Clear();
        Assert.True(timeline.Advance(20));
        Assert.Equal(new Dictionary<int, int> { [7] = 1 }, timeline.PendingWeapons);
    }

    [Fact]
    public void MissingInitialEvidenceNeverUsesAFutureSnapshotAndRestartResetsProgress()
    {
        var timeline = new ReplayInventoryTimeline([At(50, [7], [7], 100, true, true, 7)]);
        timeline.Start(0);
        Assert.Empty(timeline.PendingWeapons);
        Assert.Null(timeline.Armor);
        Assert.True(timeline.Advance(50));
        Assert.Equal(1, timeline.PendingWeapons[7]);
        timeline.Start(0);
        Assert.Empty(timeline.PendingWeapons);
        Assert.Null(timeline.Armor);
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(1, -1, 1)]
    [InlineData(0, 2, 0)]
    public void UtilityCountUsesAmmoForStackedFlashbangs(int entities, int ammo, int expected)
        => Assert.Equal(expected, DemoTracerPlugin.ObservedUtilityCount(entities, ammo));
}
