/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizer;

internal static class EconIdentityTests
{
    internal static void Run()
    {
        var plain = new ReplayEconIdentity(null, null, null, null, null, null);
        Require(plain.ResolveQuality(3) == 3, "plain knife keeps knife quality");
        Require(plain.ResolveQuality(4) == 4, "plain gun keeps gun quality");

        foreach (var counter in new[] { 0, 42 })
        {
            var stattrak = plain with { StattrakCounter = counter };
            Require(stattrak.ResolveQuality(3) == 9,
                $"StatTrak knife with counter {counter} keeps quality through preparation and retry");
            Require(stattrak.ResolveQuality(4) == 9,
                $"StatTrak gun with counter {counter} gets StatTrak quality");
        }

        foreach (var quality in new[] { 0, 3, 4, 9, 12 })
        {
            var explicitQuality = plain with { Quality = quality, StattrakCounter = 0 };
            Require(explicitQuality.ResolveQuality(3) == quality && explicitQuality.ResolveQuality(4) == quality,
                $"explicit quality {quality} takes precedence over StatTrak and item defaults");
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"Econ identity test failed: {label}");
    }
}
