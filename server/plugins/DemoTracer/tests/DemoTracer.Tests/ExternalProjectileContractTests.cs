/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;
using DemoTracer;

namespace DemoTracer.Tests;

public sealed class ExternalProjectileContractTests
{
    [Fact]
    public void ExternalProjectileMarkerMatchesThePublishedPlaybackContract()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "playback-contract.v1.json")));
        Assert.Equal(DemoTracerPlugin.ExternalProjectileName,
            contract.RootElement.GetProperty("demotracer").GetProperty("external_projectile_entity_name").GetString());
    }
}
