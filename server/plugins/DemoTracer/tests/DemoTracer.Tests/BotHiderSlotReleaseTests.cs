/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DtrHider;
using DtrHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderSlotReleaseTests
{
    [Fact]
    public void LeaseChangesPreservePublishedControllerUntilIncarnationChanges()
    {
        using var client = new NativePresentationClient();
        using var owner = new CancellationTokenSource();
        var service = new BotHiderPresentationService(client, _ => { });
        service.ObserveSlot(1, 7, 0x8005, 20);
        var slots = (BotHiderPresentationService.SlotState[])typeof(BotHiderPresentationService)
            .GetField("_slots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        slots[1].PublishedController = 0x8005;
        service.AddLease(new("token", "demotracer", new()
        {
            [1] = new() { Slot = 1, Incarnation = slots[1].Incarnation, PlayerName = "new name" }
        }, owner.Token));

        Assert.False(slots[1].NeedsCrosshairPublication(0x8005));
        owner.Cancel();
        Assert.False(slots[1].NeedsCrosshairPublication(0x8005));
        service.ObserveSlot(1, 8, 0x10005, 21);
        Assert.True(slots[1].NeedsCrosshairPublication(0x10005));
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("native_slot_lost")]
    [InlineData("map")]
    [InlineData("replacement")]
    [InlineData("dispose")]
    public void ExpiredClanOwnershipDoesNotAccessTheEngine(string boundary)
    {
        using var client = new NativePresentationClient();
        var service = new BotHiderPresentationService(client);
        service.ObserveSlot(1, 7, 0x8005, 20);
        var slots = (BotHiderPresentationService.SlotState[])typeof(BotHiderPresentationService)
            .GetField("_slots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        var clan = slots[1].Clan = new ClanPresentationState();
        var current = new BotHiderClan("base", 0);
        clan.Apply(new("demo team", 42), () => current, value => current = value, () => { });

        // No CSS engine is loaded here. Any attempt to resolve the expired
        // controller (as the old restoration path did) makes this test fail.
        switch (boundary)
        {
            case "disconnect": service.HandleClientDisconnect(1); break;
            case "native_slot_lost": Assert.False(service.IsManagedBot(1)); break;
            case "map": service.ResetForMapBoundary(); break;
            case "replacement": service.ObserveSlot(1, 8, 0x10005, 21); break;
            case "dispose": service.Dispose(); break;
        }

        Assert.Null(slots[1].Clan);
        Assert.Equal(new BotHiderClan("demo team", 42), current);
    }

    [Fact]
    public void OneBotLeavingPreservesOtherDemoIdentitiesUntilOwnerCancellation()
    {
        using var client = new NativePresentationClient();
        using var owner = new CancellationTokenSource();
        var publications = new List<int>();
        var service = new BotHiderPresentationService(client, publications.Add);
        var retained = new BotHiderPresentationOverride
        {
            Slot = 2, Incarnation = 22, PlayerName = "demo teammate", SteamId = 1234
        };
        var lease = new BotHiderPresentationService.PresentationLease(
            "token", "demotracer", new()
            {
                [1] = new() { Slot = 1, Incarnation = 11, PlayerName = "leaving bot" },
                [2] = retained
            }, owner.Token);
        service.AddLease(lease);

        service.HandleClientDisconnect(1);

        Assert.Null(service.GetPresentationOverride(1, 11));
        Assert.Same(retained, service.GetPresentationOverride(2, 22));
        Assert.Null(service.GetPresentationOverride(2, 23));
        Assert.Equal(2, lease.Overrides.Count); // Preserve the original atomic request snapshot.

        // Repeated disconnects and unrelated human disconnects must not remove
        // the surviving slot or revoke its owner's token either.
        service.HandleClientDisconnect(1);
        service.HandleClientDisconnect(5);
        Assert.Same(retained, service.GetPresentationOverride(2, 22));
        owner.Cancel();
        Assert.Null(service.GetPresentationOverride(2, 22));
        Assert.Equal(new[] { 2 }, publications);
        owner.Cancel();
        Assert.Equal(new[] { 2 }, publications);
    }

    [Fact]
    public void MapBoundaryStillRevokesWholeLeaseAfterPartialSlotRemoval()
    {
        using var client = new NativePresentationClient();
        using var owner = new CancellationTokenSource();
        using var nextOwner = new CancellationTokenSource();
        var publications = 0;
        var service = new BotHiderPresentationService(client, _ => publications++);
        service.AddLease(new("token", "demotracer", new()
        {
            [1] = new() { Slot = 1, Incarnation = 11, PlayerName = "first" },
            [2] = new() { Slot = 2, Incarnation = 22, PlayerName = "second" }
        }, owner.Token));

        service.HandleClientDisconnect(1);
        Assert.NotNull(service.GetPresentationOverride(2, 22));
        service.ResetForMapBoundary();
        Assert.Null(service.GetPresentationOverride(2, 22));
        service.AddLease(new("new-token", "new-owner", new()
        {
            [2] = new() { Slot = 2, Incarnation = 33, PlayerName = "new map" }
        }, nextOwner.Token));
        owner.Cancel();
        Assert.Equal(0, publications);
        Assert.NotNull(service.GetPresentationOverride(2, 33));
        nextOwner.Cancel();
        Assert.Equal(1, publications);
        Assert.Null(service.GetPresentationOverride(2, 33));
    }

    [Theory]
    [InlineData(7, 0x10005u, 20ul)]
    [InlineData(8, 0x8005u, 20ul)]
    [InlineData(7, 0x8005u, 21ul)]
    public void ControllerUserOrNativeIncarnationChangeRevokesOnlyThatSlot(int userId, uint controller, ulong nativeIncarnation)
    {
        using var client = new NativePresentationClient();
        using var owner = new CancellationTokenSource();
        var service = new BotHiderPresentationService(client, _ => { });
        service.ObserveSlot(1, 7, 0x8005, 20);
        service.AddLease(new("token", "demotracer", new()
        {
            [1] = new() { Slot = 1, Incarnation = 1, PlayerName = "first" },
            [2] = new() { Slot = 2, Incarnation = 22, PlayerName = "second" }
        }, owner.Token));
        service.ObserveSlot(1, 7, 0x8005, 20);
        Assert.NotNull(service.GetPresentationOverride(1, 1));
        service.ObserveSlot(1, userId, controller, nativeIncarnation);
        Assert.Null(service.GetPresentationOverride(1, 1));
        Assert.NotNull(service.GetPresentationOverride(2, 22));
        owner.Cancel();
        Assert.Null(service.GetPresentationOverride(2, 22));
    }
}
