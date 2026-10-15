/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace DemoTracer.Tests;

public class PlayedDemoManifestTests
{
    [Fact]
    public void PlaybackReadsTheExistingDemoContentHash()
    {
        var type = typeof(DemoTracerPlugin).GetNestedType("ConversionManifest", BindingFlags.NonPublic)!;
        var hash = new string('a', 64);
        var manifest = JsonSerializer.Deserialize($$"""{"demo_sha256":"{{hash}}"}""", type, DemoTracerPlugin.ManifestJsonOptions)!;
        Assert.Equal(hash, type.GetProperty("DemoSha256")!.GetValue(manifest));
        var legacy = JsonSerializer.Deserialize("{}", type, DemoTracerPlugin.ManifestJsonOptions)!;
        Assert.Equal(string.Empty, type.GetProperty("DemoSha256")!.GetValue(legacy));
    }
}
