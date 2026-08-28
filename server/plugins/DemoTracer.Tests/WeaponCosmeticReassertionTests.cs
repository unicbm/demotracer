/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;

namespace DemoTracer.Tests;

public sealed class WeaponCosmeticReassertionTests
{
    [Theory]
    [InlineData("TryApplyWeaponCosmetic")]
    [InlineData("TryApplyKnifeCosmetic")]
    [InlineData("TryApplyGloveCosmetic")]
    [InlineData("TryApplyAgentCosmetic")]
    [InlineData("ApplyReplayMusicKit")]
    [InlineData("HookCosmeticGiveNamedItem")]
    [InlineData("ReassertReplayKnifeSubclass")]
    public void DemoTracerDoesNotContainACosmeticEntityWriter(string retiredMethod)
    {
        var method = typeof(DemoTracerPlugin).GetMethod(
            retiredMethod,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Null(method);
    }
}
