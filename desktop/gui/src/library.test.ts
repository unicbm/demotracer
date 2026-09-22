/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import {
  demoLibraryTimestamp,
  isReusableDemoArchive,
  librarySeriesForManifest,
  normalizeSourceLinkNoteDismissed,
} from "./library.ts";
import type { DemoLibraryEntry } from "./types.ts";

function seriesEntry(
  manifestPath: string,
  seriesId: string | null,
  order = 1,
): DemoLibraryEntry {
  return {
    manifestPath,
    series: seriesId ? {
      id: seriesId,
      order,
      mapCount: 3,
      evidence: "hltvFilenameExactRoster",
    } : null,
  } as DemoLibraryEntry;
}

test("the source-link hint uses a persistent explicit acknowledgement", () => {
  assert.equal(normalizeSourceLinkNoteDismissed("true"), true);
  assert.equal(normalizeSourceLinkNoteDismissed(true), true);
  assert.equal(normalizeSourceLinkNoteDismissed("false"), false);
  assert.equal(normalizeSourceLinkNoteDismissed(null), false);
});

test("library series navigation resolves the active series and sorts by MAP index", () => {
  const entries = [
    seriesEntry("C:\\archive\\map-3\\manifest.json", "series-a", 3),
    seriesEntry("C:\\archive\\map-1\\manifest.json", "series-a", 1),
    seriesEntry("C:\\archive\\map-2\\manifest.json", "series-a", 2),
    seriesEntry("C:\\archive\\single\\manifest.json", null),
  ];

  assert.deepEqual(
    librarySeriesForManifest(entries, "c:/archive/map-2/manifest.json")
      .map((entry) => entry.series?.order),
    [1, 2, 3],
  );
  assert.deepEqual(
    librarySeriesForManifest(entries, "C:\\archive\\single\\manifest.json"),
    [],
  );
});

test("only a healthy archive suppresses re-importing the same demo", () => {
  const healthy = {
    metadataStatus: "current",
    compatibility: "current",
    sourceAvailable: true,
    rounds: 22,
    files: 220,
  } as DemoLibraryEntry;

  assert.equal(isReusableDemoArchive(healthy), true);
  assert.equal(isReusableDemoArchive({ ...healthy, metadataStatus: "stale" }), false);
  assert.equal(isReusableDemoArchive({ ...healthy, compatibility: "unsupported" }), false);
  assert.equal(isReusableDemoArchive({ ...healthy, sourceAvailable: false }), false);
  assert.equal(isReusableDemoArchive({ ...healthy, files: 0 }), false);
});

test("library time prefers parsed match time while preserving legacy archive dates", () => {
  const legacy = {
    sourceModifiedAtMs: 1_750_469_653_000,
    modifiedAtMs: 1_750_500_000_000,
  } as DemoLibraryEntry;

  assert.equal(demoLibraryTimestamp(legacy), 1_750_469_653_000);
  assert.equal(
    demoLibraryTimestamp({ ...legacy, playedAt: "2026-07-22T18:19:28Z" }),
    Date.parse("2026-07-22T18:19:28Z"),
  );
  assert.equal(demoLibraryTimestamp({ ...legacy, playedAt: "invalid" }), 1_750_469_653_000);
  assert.equal(
    demoLibraryTimestamp({ ...legacy, sourceModifiedAtMs: null }),
    1_750_500_000_000,
  );
});
