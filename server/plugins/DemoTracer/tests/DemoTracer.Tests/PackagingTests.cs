/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class PackagingTests
{
    [Theory]
    [InlineData("AGPL", "GNU AFFERO GENERAL PUBLIC LICENSE")]
    [InlineData("ThirdParty", "Copyright (c) 2021 Oleg Stepanischev")]
    [InlineData("Upstream", "github.com")]
    public void AttributionTravelsInsideThePluginAssembly(string name, string expected)
    {
        using var stream = typeof(DemoTracerPlugin).Assembly
            .GetManifestResourceStream($"DemoTracer.Licenses.{name}.txt");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Contains(expected, reader.ReadToEnd());
    }
}
