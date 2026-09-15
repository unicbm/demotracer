/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotHiderImpl;
using DemoTracerBotHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderSlotReleaseTests
{
    [Fact]
    public void OneBotLeavingPreservesOtherDemoIdentitiesAndHeartbeat()
    {
        using var client = new NativePresentationClient();
        var service = new BotHiderPresentationService(client);
        var retained = new BotHiderPresentationOverride
        {
            Slot = 2, Incarnation = 22, PlayerName = "demo teammate", SteamId = 1234
        };
        var lease = new BotHiderPresentationService.PresentationLease(
            "token", "demotracer", new()
            {
                [1] = new() { Slot = 1, Incarnation = 11, PlayerName = "leaving bot" },
                [2] = retained
            }, DateTime.UtcNow);
        service.AddLease(lease);

        service.HandleClientDisconnect(1);

        Assert.Null(service.GetPresentationOverride(1, 11));
        Assert.Same(retained, service.GetPresentationOverride(2, 22));
        Assert.Null(service.GetPresentationOverride(2, 23));
        Assert.Equal(2, lease.Overrides.Count); // Preserve the original atomic request snapshot.
        Assert.True(service.HeartbeatPresentationLease("token"));

        // Repeated disconnects and unrelated human disconnects must not remove
        // the surviving slot or revoke its owner's token either.
        service.HandleClientDisconnect(1);
        service.HandleClientDisconnect(5);
        Assert.Same(retained, service.GetPresentationOverride(2, 22));
        Assert.True(service.HeartbeatPresentationLease("token"));

        service.HandleClientDisconnect(2);
        Assert.Null(service.GetPresentationOverride(2, 22));
        Assert.False(service.HeartbeatPresentationLease("token"));
    }

    [Fact]
    public void MapBoundaryStillRevokesWholeLeaseAfterPartialSlotRemoval()
    {
        using var client = new NativePresentationClient();
        var service = new BotHiderPresentationService(client);
        service.AddLease(new("token", "demotracer", new()
        {
            [1] = new() { Slot = 1, Incarnation = 11, PlayerName = "first" },
            [2] = new() { Slot = 2, Incarnation = 22, PlayerName = "second" }
        }, DateTime.UtcNow));

        service.HandleClientDisconnect(1);
        Assert.True(service.HeartbeatPresentationLease("token"));
        service.ResetForMapBoundary();
        Assert.False(service.HeartbeatPresentationLease("token"));
    }
}
