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
  mergeProfessionalPlayerIdentities,
  parseProfessionalPlayerCatalog,
  resolveProfessionalPlayerFromCatalog,
} from "./professionalPlayersCatalog.ts";
import { parseProSteamIdCatalogJsonl, resolveProSteamIdFromCatalog } from "./proSteamIdCatalog.ts";

const validCatalog = JSON.parse(readFileSync(
  new URL("./data/professional-players.v2.json", import.meta.url),
  "utf8",
)) as Record<string, unknown> & { players: unknown[] };
const registryCatalog = parseProSteamIdCatalogJsonl(readFileSync(
  new URL("./data/cs2-pro-steamid-lib.v1.jsonl", import.meta.url),
  "utf8",
));

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

test("demo-verified catalog resolves SteamID identity and optional HLTV profile", () => {
  const catalog = parseProfessionalPlayerCatalog(validCatalog);
  const identity = resolveProfessionalPlayerFromCatalog(
    catalog,
    "76561198386265483",
  );

  assert.equal(identity?.handle, "donk");
  assert.equal(identity?.evidenceResult, "confirmed");
  assert.equal(identity?.hltv?.playerId, 21167);
  assert.equal(identity?.hltv?.realName, "Danil Kryshkovets");
  assert.deepEqual(identity?.hltv?.country, { name: "Russia", code: "RU" });
  assert.equal(
    resolveProfessionalPlayerFromCatalog(catalog, "76561199024583803")?.hltv,
    null,
  );
  assert.equal(
    resolveProfessionalPlayerFromCatalog(catalog, "76561198000000000"),
    null,
  );
});

test("catalog uses demo-corrected SteamID64 for known bad upstream records", () => {
  const catalog = parseProfessionalPlayerCatalog(validCatalog);
  const magixx = resolveProfessionalPlayerFromCatalog(
    catalog,
    "76561199063238565",
  );

  assert.equal(magixx?.handle, "magixx");
  assert.equal(magixx?.evidenceResult, "corrected");
  assert.equal(
    resolveProfessionalPlayerFromCatalog(catalog, "76561197961134282"),
    null,
  );
});

test("catalog validation rejects duplicate SteamID64 records", () => {
  assert.throws(
    () => parseProfessionalPlayerCatalog({
      ...validCatalog,
      players: [...validCatalog.players, validCatalog.players[0]],
    }),
    /duplicate SteamID64/,
  );
});

test("catalog validation rejects an HLTV URL for a different Player ID", () => {
  const player = structuredClone(
    validCatalog.players.find((candidate) => (
      (candidate as { steamId?: string }).steamId === "76561198386265483"
    )),
  ) as {
    hltv: { source: { url: string } };
  };
  player.hltv.source.url = "https://www.hltv.org/player/1/not-donk";

  assert.throws(
    () => parseProfessionalPlayerCatalog({
      ...validCatalog,
      players: [player],
    }),
    /must identify HLTV Player ID 21167/,
  );
});

test("combined resolver expands coverage while preserving stronger demo identity", () => {
  const demoCatalog = parseProfessionalPlayerCatalog(validCatalog);
  const resolveCombined = (steamId: string) => mergeProfessionalPlayerIdentities(
    resolveProfessionalPlayerFromCatalog(demoCatalog, steamId),
    resolveProSteamIdFromCatalog(registryCatalog, steamId),
    registryCatalog,
  );
  const registryOnly = resolveCombined("76561197960268122");
  const overlapWithHandleDifference = resolveCombined("76561198375857603");

  assert.equal(registryOnly?.handle, "James Bardolph");
  assert.equal(registryOnly?.evidenceResult, "curated");
  assert.equal(registryOnly?.registry?.country, "United Kingdom");
  assert.equal(overlapWithHandleDifference?.handle, "FraGuTy");
  assert.ok(overlapWithHandleDifference?.aliases.includes("guty"));
  assert.equal(overlapWithHandleDifference?.evidenceResult, "confirmed");
});
