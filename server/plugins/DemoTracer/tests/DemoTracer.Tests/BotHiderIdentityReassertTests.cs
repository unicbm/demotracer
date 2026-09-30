/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DtrHider;

namespace DemoTracer.Tests;

public sealed class BotHiderIdentityReassertTests
{
    [Fact]
    public void NewPresentationPublishesCrosshairEvenWhenValueMatches()
    {
        var state = new BotHiderPresentationService.SlotState();
        Assert.True(state.NeedsCrosshairPublication(0x8005));
    }

    [Fact]
    public void ReusedControllerIndexWithNewSerialPublishesCrosshair()
    {
        var state = new BotHiderPresentationService.SlotState { PublishedController = 0x8005 };
        Assert.True(state.NeedsCrosshairPublication(0x10005));
    }

    [Fact]
    public void StableAppliedPresentationDoesNotRepublishCrosshair()
    {
        var state = new BotHiderPresentationService.SlotState { PublishedController = 0x8005 };
        Assert.False(state.NeedsCrosshairPublication(0x8005));
        state.CrosshairPending = true;
        Assert.True(state.NeedsCrosshairPublication(0x8005));
    }
}
