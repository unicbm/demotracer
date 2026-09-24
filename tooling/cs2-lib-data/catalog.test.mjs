/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { CS2_ITEMS } from "@ianlucas/cs2-lib";
import { source, integrity, catalogPath, econPath } from "./source.mjs";

const read = path => JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
const catalog = JSON.parse(readFileSync(catalogPath, "utf8"));
const econ = JSON.parse(readFileSync(econPath, "utf8"));
const policy = read("randomizer-policy.json");
const ids = (type, key) => new Set(CS2_ITEMS.filter(i => i.type === type && i.variantIndex > 0).map(key));
const pair = i => `${i.definitionIndex}:${i.variantIndex}`;

test("both runtime catalogs identify the same pinned source", () => {
  for (const document of [catalog, econ]) {
    assert.equal(document.source.version, source.version);
    assert.equal(document.source.integrity, integrity);
  }
  assert.equal(catalog.source.commit, source.commit);
  assert.deepEqual(catalog.source.proDemo, policy.proDemo);
  assert.deepEqual(catalog.knifeFinishPreferences, policy.knifeFinishPreferences);
});

test("random pools cover every upstream variant without duplicates", () => {
  for (const [type, entries] of [
    ["weapon", catalog.weapons.flatMap(w => w.paints.map(p => `${w.defIndex}:${p.paintKit}`))],
    ["melee", catalog.knives.flatMap(w => w.paints.map(p => `${w.defIndex}:${p.paintKit}`))],
    ["glove", catalog.gloves.map(p => `${p.defIndex}:${p.paintKit}`)],
    ["sticker", catalog.stickerKits.map(p => p.defIndex)],
    ["keychain", catalog.keychainDefinitions],
  ]) {
    assert.equal(new Set(entries).size, entries.length, `${type} duplicates`);
    assert.deepEqual(new Set(entries), ids(type, ["weapon", "melee", "glove"].includes(type) ? pair : i => i.variantIndex));
  }
  assert.deepEqual(new Set(catalog.musicKits), new Set([...ids("musickit", i => i.variantIndex)].filter(id => !policy.excludedMusicKits.includes(id))));
});

test("randomized items are accepted by the replay validator projection", () => {
  assert.deepEqual(new Set(catalog.stickerKits.map(s => s.defIndex)), new Set(econ.sticker_ids));
  assert.deepEqual(new Set(catalog.keychainDefinitions), new Set(econ.keychain_ids));
  assert.deepEqual(new Set(catalog.weapons.flatMap(w => w.paints.map(p => `${w.defIndex}:${p.paintKit}`))), new Set(econ.weapon_paints.map(p => `${p.weapon_defidx}:${p.paint_kit}`)));
  const legacy = new Set(econ.legacy_bodygroup_paints.map(p => `${p.weapon_defidx}:${p.paint_kit}`));
  for (const w of catalog.weapons) for (const p of w.paints) {
    assert.equal(p.legacy, legacy.has(`${w.defIndex}:${p.paintKit}`));
  }
  assert.equal(new Set(catalog.stickerKits.map(s => s.category)).size, catalog.stickerCategories.length);
});

test("Ranked and Champion finishes retain their material class", () => {
  assert.equal(catalog.stickerKits.find(s => s.defIndex === 11813)?.finish, "gold");
  assert.equal(catalog.stickerKits.find(s => s.defIndex === 5877)?.finish, "glitter");
  assert.equal(catalog.stickerKits.find(s => s.defIndex === 5878)?.finish, "holo");
});
