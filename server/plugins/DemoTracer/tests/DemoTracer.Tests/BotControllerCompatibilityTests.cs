/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Runtime.InteropServices;

namespace DemoTracer.Tests;

public sealed class BotControllerCompatibilityTests
{
    [Fact]
    public void ReplayLayoutsMatchNativeAbi()
    {
        Assert.Equal(92, Marshal.SizeOf<NativeMovementSnapshot>());
        Assert.Equal(228, Marshal.SizeOf<NativeReplayTick>());
        Assert.Equal(192, Marshal.OffsetOf<NativeReplayTick>(nameof(NativeReplayTick.EventFlags)).ToInt32());
        Assert.Equal(224, Marshal.OffsetOf<NativeReplayTick>(nameof(NativeReplayTick.EventDropVelocityZ)).ToInt32());
        Assert.Equal(28, Marshal.SizeOf<NativeSubtickMove>());
        Assert.Equal(68, Marshal.SizeOf<NativeReplayCommandFrame>());
        Assert.Equal(48, Marshal.SizeOf<NativeReplayMovementExtra>());
    }
}
