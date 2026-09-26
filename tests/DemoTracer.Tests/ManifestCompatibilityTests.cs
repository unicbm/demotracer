/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DemoTracer.Tests;

public sealed class ManifestCompatibilityTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(12, 3, true)]
    [InlineData(18, 11, true)]
    [InlineData(19, 12, true)]
    [InlineData(11, 3, false)]
    [InlineData(20, 12, false)]
    [InlineData(19, 13, false)]
    public void PlaybackEntryAcceptsSupportedManifestAndDtrVersions(int abi, int format, bool supported)
    {
        var path = Path.Combine(Path.GetTempPath(), $"demotracer-manifest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                abi,
                format_version = format,
                map = "de_ancient",
                tick_rate = 64,
                files = new[] { new { path = "round01/player.dtr", side = "t", round = 1 } }
            }));
            var plugin = (DemoTracerPlugin)RuntimeHelpers.GetUninitializedObject(typeof(DemoTracerPlugin));
            var read = typeof(DemoTracerPlugin).GetMethod("TryReadManifest", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object?[] arguments = [path, null, null];

            Assert.Equal(supported, (bool)read.Invoke(plugin, arguments)!);
            var error = Assert.IsType<string>(arguments[2]);
            if (supported)
                Assert.Empty(error);
            else
                Assert.Contains("unsupported; expected", error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
