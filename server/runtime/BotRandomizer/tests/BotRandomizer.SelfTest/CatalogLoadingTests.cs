/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;
using System.Text.Json.Nodes;
using BotRandomizer;

internal static class CatalogLoadingTests
{
    internal static void Run(string path, CosmeticCatalog original, CharmPlacementCatalog placements)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["source"]!.AsObject().Remove("proDemo");
        foreach (var preference in document["knifeFinishPreferences"]!.AsArray())
            preference!.AsObject().Remove("observations");
        var temporary = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temporary, document.ToJsonString());
            var runtimeOnly = CosmeticCatalog.Load(temporary);
            var before = new CosmeticRoller(original, placements, new Random(20260926));
            var after = new CosmeticRoller(runtimeOnly, placements, new Random(20260926));
            for (var i = 0; i < 64; i++)
            {
                var team = (byte)(2 + i % 2);
                var expected = before.RollLoadout(team);
                var actual = after.RollLoadout(team);
                foreach (var weapon in original.Weapons)
                {
                    before.GetOrCreateWeapon(expected, weapon.DefIndex);
                    after.GetOrCreateWeapon(actual, weapon.DefIndex);
                }
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                    throw new InvalidOperationException("Removing research metadata changed randomized cosmetics.");
            }

            document["weapons"]!.AsArray().Clear();
            File.WriteAllText(temporary, document.ToJsonString());
            try { CosmeticCatalog.Load(temporary); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Missing runtime item data must still be rejected.");
        }
        finally { File.Delete(temporary); }
    }
}
