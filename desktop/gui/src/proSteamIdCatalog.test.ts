/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  parseProSteamIdCatalogJsonl,
  resolveProSteamIdFromCatalog,
} from "./proSteamIdCatalog.ts";

const source = readFileSync(
  new URL("./data/cs2-pro-steamid-lib.v1.jsonl", import.meta.url),
  "utf8",
);
const [metadataLine, playerLine] = source.split("\n", 2);

function registry(...players: unknown[]): string {
  const metadata = JSON.parse(metadataLine);
  metadata._meta.records = players.length;
  return [metadata, ...players].map((value) => JSON.stringify(value)).join("\n");
}

test("attributed registry snapshot resolves a curated professional identity", () => {
  const catalog = parseProSteamIdCatalogJsonl(source);
  const donk = resolveProSteamIdFromCatalog(catalog, "76561198386265483");

  assert.equal(donk?.handle, "donk");
  assert.equal(donk?.nameLatin, "Danil Kryshkovets");
  assert.equal(donk?.countryCode, "RU");
  assert.equal(donk?.mappingSources[0]?.identityEvidence, "curated");
  assert.match(donk?.mappingSources[0]?.url ?? "", /^https:\/\/liquipedia\.net\//);
});

test("registry parser rejects duplicate SteamID64 records", () => {
  const player = JSON.parse(playerLine);
  assert.throws(() => parseProSteamIdCatalogJsonl(registry(player, player)), /duplicate SteamID64/);
});

test("registry parser requires Liquipedia revision attribution", () => {
  const player = JSON.parse(playerLine);
  delete player.mappingSources[0].revisionTimestamp;
  assert.throws(() => parseProSteamIdCatalogJsonl(registry(player)), /revision ID and timestamp/);
});

test("registry parser rejects impossible calendar dates", () => {
  const player = JSON.parse(playerLine);
  player.birthDate = "2025-02-29";
  assert.throws(() => parseProSteamIdCatalogJsonl(registry(player)), /valid calendar date/);
});

test("registry parser rejects swallowed template fields in ESEA values", () => {
  const player = JSON.parse(playerLine);
  player.externalIds.esea = "|faceitdb=not-an-esea-id";
  assert.throws(() => parseProSteamIdCatalogJsonl(registry(player)), /externalIds\.esea contains template-field syntax/);
});
