/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildArchiveSessionMeta,
  EMPTY_LIBRARY_WORKSPACE,
  LIBRARY_SESSION_STORAGE_KEY,
  libraryWorkspaceReducer,
  readStoredLibrarySession,
} from "./librarySession.ts";
import type { ManifestArchive } from "./types";

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
