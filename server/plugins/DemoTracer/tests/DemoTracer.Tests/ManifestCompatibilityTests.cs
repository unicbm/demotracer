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
    [Fact]
    public void AutomaticVoiceUsesOnlyTheArchiveRoundSidecar()
    {
        var root = Directory.CreateTempSubdirectory("demotracer-voice-");
        try
        {
            var voice = Directory.CreateDirectory(Path.Combine(root.FullName, "voice"));
            File.WriteAllText(Path.Combine(voice.FullName, "voice_round01.dtv"), "DTRVOICE");
            var resolve = typeof(DemoTracerPlugin).GetMethod("TryResolveVoiceSidecarForRound",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            object?[] arguments = [Path.Combine(root.FullName, "manifest.json"), 1, null];
            Assert.False((bool)resolve.Invoke(null, arguments)!);

            var clip = Path.Combine(voice.FullName, "round01.dtv");
            File.WriteAllText(clip, "DTRVOICE");
            Assert.True((bool)resolve.Invoke(null, arguments)!);
            Assert.Equal(clip, arguments[2]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(12, 3, false)]
    [InlineData(18, 11, false)]
    [InlineData(19, 12, true)]
    [InlineData(11, 3, false)]
    [InlineData(20, 13, false)]
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
