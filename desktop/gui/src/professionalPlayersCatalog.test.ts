/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  completedAge,
  formattedBirthDateWithAge,
  formattedProfessionalRoles,
} from "./professionalPlayersCatalog.ts";
import { buildProfessionalPlayerRuntime } from "../scripts/runtime-catalogs.ts";

test("completed age changes exactly on the player's birthday", () => {
  assert.equal(completedAge("2007-01-25", new Date(2026, 0, 24)), 18);
  assert.equal(completedAge("2007-01-25", new Date(2026, 0, 25)), 19);
  assert.equal(completedAge("2007-01-25", new Date(2026, 6, 26)), 19);
  assert.equal(completedAge("2007-02-30", new Date(2026, 6, 26)), null);
  assert.equal(completedAge("2030-01-01", new Date(2026, 6, 26)), null);
  assert.equal(
    formattedBirthDateWithAge("2001-02-14", "en", new Date(2026, 6, 27)),
    "February 14, 2001 (age 25)",
  );
  assert.equal(
    formattedBirthDateWithAge("2001-02-14", "zh", new Date(2026, 6, 27)),
    "2001年2月14日 （25 岁）",
  );
});

test("professional roles use concise CS labels without duplicates", () => {
  assert.equal(formattedProfessionalRoles(["awp", "rifle"]), "AWPer · Rifler");
  assert.equal(formattedProfessionalRoles(["rifle", "Rifler", "igl"]), "Rifler · IGL");
  assert.equal(formattedProfessionalRoles([]), null);
});

test("runtime catalog preserves demo identity and registry details", () => {
  const catalog = buildProfessionalPlayerRuntime(
    JSON.parse(readFileSync(new URL("./data/professional-players.v2.json", import.meta.url), "utf8")),
    readFileSync(new URL("./data/cs2-pro-steamid-lib.v1.jsonl", import.meta.url), "utf8"),
  );
  const donk = catalog["76561198386265483"];
  assert.equal(donk.handle, "donk");
  assert.equal(donk.realName, "Danil Kryshkovets");
  assert.equal(donk.countryCode, "RU");
  assert.equal(catalog["76561199063238565"].handle, "magixx");
  assert.equal(catalog["76561197960268122"].handle, "James Bardolph");
  assert.equal(catalog["76561197960268122"].country, "United Kingdom");
  assert.equal(catalog["76561198375857603"].handle, "FraGuTy");
  assert.ok(catalog["76561198375857603"].searchText.includes("guty"));
});
