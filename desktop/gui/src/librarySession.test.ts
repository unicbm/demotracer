/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildArchiveSessionMeta,
  createManifestCache,
  EMPTY_LIBRARY_WORKSPACE,
  LIBRARY_SESSION_STORAGE_KEY,
  libraryWorkspaceReducer,
  readStoredLibrarySession,
} from "./librarySession.ts";
import type { DemoLibraryEntry, ManifestArchive } from "./types";

function round(round: number, sequenceLength: number): ManifestArchive["rounds"][number] {
  return {
    round,
    files: sequenceLength,
    tFiles: 1,
    ctFiles: sequenceLength - 1,
    cosmeticFiles: 0,
    stickerFiles: 0,
    charmFiles: 0,
    ticks: 128,
    subticks: 0,
    hifiEvents: 0,
    inventorySnapshots: 0,
    sequenceLength,
    available: true,
    commands: {
      goRound: "dtr_go round",
      goSequence: "dtr_go seq",
      round: "dtr_arm round",
      sequence: "dtr_arm seq",
    },
  };
}

function archive(): ManifestArchive {
  return {
    manifestPath: "C:\\Library\\match\\manifest.json",
    root: "C:\\Library\\match",
    demoPath: "match.dem",
    demoId: "match-aabbccdd",
    demoSha256: "aa".repeat(32),
    map: "de_mirage",
    tickRate: 64,
    abi: 17,
    formatVersion: 7,
    compatibility: "current",
    totalFiles: 3,
    playableFiles: 3,
    outputBytes: 1024,
    playable: true,
    rounds: [round(0, 2), round(1, 1)],
    issues: [],
    players: [],
    voice: { requested: false, sidecars: 0, rounds: [] },
    cosmetics: {
      requested: false,
      stickerRequested: false,
      charmRequested: false,
      files: 0,
      stickerFiles: 0,
      charmFiles: 0,
    },
    displayName: "match",
    metadataStatus: "current",
  };
}

describe("library workspace session", () => {
  it("does not repeat a demo filename that only differs by the .dem suffix", () => {
    assert.equal(
      buildArchiveSessionMeta(
        "g161-20260725233910922111563_de_dust2",
        "g161-20260725233910922111563_de_dust2.dem",
        "de_dust2",
        23,
        "回合",
      ),
      "de_dust2 · 23 回合",
    );
  });

  it("keeps a distinct source filename when the archive has a descriptive title", () => {
    assert.equal(
      buildArchiveSessionMeta("Falcons vs Astralis", "match-2026.dem", "de_mirage", 24, "rounds"),
      "match-2026.dem · de_mirage · 24 rounds",
    );
  });

  it("opens an archive in analysis and keeps it while navigating through the library", () => {
    const opened = libraryWorkspaceReducer(EMPTY_LIBRARY_WORKSPACE, { type: "open", archive: archive() });
    assert.equal(opened.activeSection, "analysis");
    const library = libraryWorkspaceReducer(opened, { type: "navigate", section: "library" });
    const returned = libraryWorkspaceReducer(library, { type: "navigate", section: "analysis" });

    assert.equal(returned.archive?.manifestPath, archive().manifestPath);
    assert.equal(returned.selectedRound, 0);
  });

  it("restores a valid round, player and command mode without reopening defaults", () => {
    const state = libraryWorkspaceReducer(EMPTY_LIBRARY_WORKSPACE, {
      type: "open",
      archive: archive(),
      restored: {
        manifestPath: archive().manifestPath,
        selectedRound: 1,
        selectedPlayer: { teamId: "a", playerIndex: 3 },
        commandMode: "round",
      },
    });

    assert.equal(state.selectedRound, 1);
    assert.deepEqual(state.selectedPlayer, { teamId: "a", playerIndex: 3 });
    assert.equal(state.commandMode, "round");
  });

  it("rejects malformed persisted sessions", () => {
    const storage = {
      getItem: (key: string) => key === LIBRARY_SESSION_STORAGE_KEY ? "{broken" : null,
    };
    assert.equal(readStoredLibrarySession(storage), null);
  });
});

describe("session manifest reads", () => {
  const pathFor = (map: number) => `C:/Library/bo5/map${map}/manifest.json`;

  it("reads each BO5 map once and switches back using the same loaded archive", async () => {
    const reads: string[] = [];
    const cache = createManifestCache(async (path) => {
      reads.push(path);
      return { ...archive(), manifestPath: path };
    });
    assert.deepEqual(reads, []);
    const loaded = [];
    for (let map = 1; map <= 5; map++) loaded.push(await cache.read(pathFor(map)));
    for (let map = 5; map >= 1; map--) {
      assert.equal(cache.get(pathFor(map)), loaded[map - 1]);
      assert.equal(await cache.read(pathFor(map)), loaded[map - 1]);
    }
    assert.equal(reads.length, 5);
    assert.equal(cache.get(pathFor(1).toUpperCase().replaceAll("/", "\\")), loaded[0]);
  });

  it("keeps visited maps when browsing beyond a single BO5", async () => {
    let reads = 0;
    const cache = createManifestCache(async (path) => {
      reads++;
      return { ...archive(), manifestPath: path };
    });
    for (let map = 1; map <= 20; map++) await cache.read(pathFor(map));
    for (let map = 1; map <= 20; map++) {
      assert.ok(cache.get(pathFor(map)));
      await cache.read(pathFor(map));
    }
    assert.equal(reads, 20);
  });

  it("keeps unchanged archives on a rescan and invalidates only changed or removed entries", async () => {
    const summary = (map: number): DemoLibraryEntry => ({
      root: `C:/Library/bo5/map${map}`,
      manifestPath: pathFor(map),
      demoPath: `map${map}.dem`,
      demoId: `map${map}`,
      demoSha256: String(map).repeat(64),
      map: "de_mirage",
      tickRate: 64,
      abi: 17,
      formatVersion: 10,
      compatibility: "current",
      modifiedAtMs: 1,
      rounds: 2,
      files: 3,
      players: [],
    });
    let reads = 0;
    const cache = createManifestCache(async (path) => {
      reads++;
      return { ...archive(), manifestPath: path };
    });
    const scan = [summary(1), summary(2)];
    cache.reconcileLibrary(scan);
    const first = await cache.read(pathFor(1));
    const second = await cache.read(pathFor(2));
    cache.reconcileLibrary(scan.map((entry) => ({ ...entry, players: [] })));
    assert.equal(cache.get(pathFor(1)), first);
    assert.equal(cache.get(pathFor(2)), second);
    assert.equal(reads, 2);

    cache.reconcileLibrary([scan[0], { ...scan[1], note: "updated" }]);
    assert.equal(cache.get(pathFor(1)), first);
    assert.equal(cache.get(pathFor(2)), undefined);
    await cache.read(pathFor(2));
    assert.equal(reads, 3);
    cache.reconcileLibrary([scan[0]]);
    assert.equal(cache.get(pathFor(1)), first);
    assert.equal(cache.get(pathFor(2)), undefined);
  });

  it("shares pending reads and does not restore a stale result after invalidation", async () => {
    let finishOldRead!: (archive: ManifestArchive) => void;
    let calls = 0;
    const fresh = { ...archive(), note: "updated" };
    const cache = createManifestCache(() => {
      calls++;
      return calls === 1
        ? new Promise<ManifestArchive>((resolve) => { finishOldRead = resolve; })
        : Promise.resolve(fresh);
    });
    const first = cache.read(pathFor(1));
    assert.equal(cache.read(pathFor(1)), first);
    cache.invalidate(pathFor(1));
    assert.equal(await cache.read(pathFor(1)), fresh);
    finishOldRead(archive());
    await first;
    assert.equal(cache.get(pathFor(1)), fresh);
    assert.equal(calls, 2);
    cache.invalidate();
    assert.equal(cache.get(pathFor(1)), undefined);
  });

  it("retries failed reads instead of caching errors", async () => {
    let calls = 0;
    const cache = createManifestCache(async () => {
      if (++calls === 1) throw new Error("missing replay");
      return archive();
    });
    await assert.rejects(cache.read(pathFor(1)), /missing replay/);
    assert.equal(cache.get(pathFor(1)), undefined);
    await cache.read(pathFor(1));
    assert.equal(calls, 2);
  });
});
