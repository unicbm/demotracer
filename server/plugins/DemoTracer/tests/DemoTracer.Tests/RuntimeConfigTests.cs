/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Text.Json;

namespace DemoTracer.Tests;

public sealed class RuntimeConfigTests
{
    [Theory]
    [InlineData("{\"preset\":\"full\",\"scoreboard\":true}")]
    [InlineData("\"obsolete\"")]
    public void RetiredMatchSettingsDoNotBlockCurrentConfiguration(string match)
    {
        var options = (JsonSerializerOptions)typeof(DemoTracerPlugin)
            .GetField("RuntimeConfigJsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var config = JsonSerializer.Deserialize<DemoTracerPlugin.DemoTracerRuntimeConfig>(
            $$$"""{"identity":"name","match":{{{match}}},"fidelity":{"projectiles":false}}""",
            options)!;

        Assert.Equal("name", config.Identity);
        Assert.False(config.Fidelity!.Projectiles);
        Assert.Null(config.UnsupportedAlign);
    }
}
