/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { buildProfessionalPlayerRuntime } from "../scripts/runtime-catalogs.ts";
import {
  mergeProfessionalPlayerIdentities,
  parseProfessionalPlayerCatalog,
  resolveProfessionalPlayerFromCatalog,
} from "./professionalPlayersCatalog.ts";
import { parseProSteamIdCatalogJsonl } from "./proSteamIdCatalog.ts";

test("the runtime projection preserves every player's visible identity and searchable names", () => {
  const demoSource = JSON.parse(readFileSync(new URL("./data/professional-players.v2.json", import.meta.url), "utf8"));
  const registrySource = readFileSync(new URL("./data/cs2-pro-steamid-lib.v1.jsonl", import.meta.url), "utf8");
  const runtime = JSON.parse(JSON.stringify(buildProfessionalPlayerRuntime(demoSource, registrySource)));
  const demo = parseProfessionalPlayerCatalog(demoSource);
  const registry = parseProSteamIdCatalogJsonl(registrySource);
  const ids = new Set([...demo.players.keys(), ...registry.players.keys()]);
  assert.equal(Object.keys(runtime).length, ids.size);
  for (const steamId of ids) {
    const old = mergeProfessionalPlayerIdentities(
      resolveProfessionalPlayerFromCatalog(demo, steamId),
      registry.players.get(steamId) ?? null,
      registry,
    )!;
    const current = runtime[steamId];
    assert.equal(current.handle, old.handle, steamId);
    assert.equal(current.realName, old.registry?.nameLatin ?? old.registry?.nameNative ?? old.hltv?.realName ?? null, steamId);
    assert.equal(current.country, old.registry?.country ?? old.hltv?.country?.name ?? null, steamId);
    assert.equal(current.countryCode, old.registry?.countryCode ?? old.hltv?.country?.code ?? null, steamId);
    assert.equal(current.birthDate, old.registry?.birthDate ?? null, steamId);
    assert.deepEqual(current.roles, old.registry?.roles ?? [], steamId);
    for (const name of [old.handle, ...old.aliases, old.registry?.nameNative, old.registry?.nameLatin,
      old.registry?.country, old.hltv?.registeredHandle, old.hltv?.realName].filter(Boolean)) {
      assert.ok(current.searchText.includes(name!.toLowerCase()), `${steamId}: ${name}`);
    }
    assert.equal("mappingSources" in current, false);
  }
});
