/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class BotControllerRuntimeReadyTests
{
    [Fact]
    public void ControllerLibraryIsResolvedFromTheGameServerModule()
    {
        string csgo = Path.Combine(Path.GetTempPath(), "CS2 path with spaces", "game", "csgo");
        Assert.Equal(Path.Combine(csgo, "addons", "dtr-controller", "bin", "win64", "dtr-controller.dll"),
            BotControllerNative.ControllerLibraryPath(Path.Combine(csgo, "bin", "win64", "server.dll")));
    }

    [Fact]
    public void MatchingAbiDoesNotAdmitAnUninitializedLibrary()
    {
        var info = new BotControllerAbiInfo { AbiMajor = BotControllerNative.ExpectedAbiVersion };
        Assert.False(BotControllerNative.IsRuntimeReady(info));

        // The old DLL advertised its compiled-in capabilities even before
        // Metamod initialization, with only the buy hook reported as absent.
        info.Capabilities = BotControllerNative.RequiredCapabilityMask & ~(1UL << 6);
        Assert.False(BotControllerNative.IsRuntimeReady(info));

        info.Capabilities = BotControllerNative.RequiredCapabilityMask;
        Assert.True(BotControllerNative.IsRuntimeReady(info));
        info.AbiMajor--;
        Assert.False(BotControllerNative.IsRuntimeReady(info));
    }
}
