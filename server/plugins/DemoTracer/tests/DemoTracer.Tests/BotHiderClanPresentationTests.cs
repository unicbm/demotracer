/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;
using DemoTracerBotHiderApi;

namespace DemoTracer.Tests;

public sealed class BotHiderClanPresentationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tag\":\"old\"}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"tag\":null,\"id\":0}")]
    public void IncompleteManifestEvidenceDoesNotClearBase(string json)
        => Assert.Null(DemoTracerPlugin.NormalizeReplayClan(
            JsonSerializer.Deserialize<DemoTracerPlugin.ReplayClan>(json)));

    [Fact]
    public void ExplicitEmptyAndUnicodeArePreserved()
    {
        foreach (var pair in new[] { new BotHiderClan("", 0), new BotHiderClan("o'O 组\u0301", 4775497) })
        {
            var json = JsonSerializer.Serialize(new { tag = pair.Tag, id = pair.Id });
            Assert.Equal(pair, DemoTracerPlugin.NormalizeReplayClan(
                JsonSerializer.Deserialize<DemoTracerPlugin.ReplayClan>(json)));
        }
        Assert.False(DemoTracerBotHiderContract.IsValidClan(new("x\0y", 1)));
        Assert.False(DemoTracerBotHiderContract.IsValidClan(new(new string('组', 43), 1)));
        Assert.True(DemoTracerBotHiderContract.IsValidClan(new(new string('x', 127), uint.MaxValue)));
    }

}
