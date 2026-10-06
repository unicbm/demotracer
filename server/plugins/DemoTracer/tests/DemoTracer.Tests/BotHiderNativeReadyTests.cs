/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using DtrHider;

namespace DemoTracer.Tests;

public sealed class BotHiderNativeReadyTests
{
    [Theory]
    [InlineData(true, true, false, -1, false, true)]
    [InlineData(true, false, false, -1, false, true)]
    [InlineData(false, false, true, 0, false, true)]
    [InlineData(false, true, true, 0, false, false)]
    [InlineData(false, false, true, 1, false, false)]
    [InlineData(false, false, true, 0, true, false)]
    [InlineData(false, false, false, 0, false, false)]
    public void NativeReadyAcceptsOnlyServerOrValidListenHost(bool console,
        bool dedicated, bool valid, int slot, bool bot, bool accepted)
        => Assert.Equal(accepted, DtrHiderPlugin.CanAcceptNativeReady(
            console, dedicated, valid, slot, bot));
}
