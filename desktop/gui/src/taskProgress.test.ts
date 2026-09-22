/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import { collectTaskWarning, emptyProgress, nextTaskProgress } from "./taskProgress.ts";
import type { ConversionProgressEvent, TaskEvent } from "./types.ts";

const words = { preparing: "Preparing", writingPlayers: "Writing players", writingArtifacts: "Writing artifacts" };
const event = (progress: ConversionProgressEvent): TaskEvent => ({ kind: "progress", progress });

test("warnings are collected before rendering, deduplicated, and bounded", () => {
  const skipped = event({ event: "roundSkipped", round: 2, reason: "missing evidence" });
  const initial: string[] = [];
  let warnings = collectTaskWarning(initial, skipped);
  assert.deepEqual(initial, []);
  assert.deepEqual(warnings, ["Round 2: missing evidence"]);
  assert.equal(collectTaskWarning(warnings, skipped), warnings);
  assert.equal(collectTaskWarning(warnings, event({ event: "roundSkipped", round: 3, reason: "not selected" })), warnings);
  for (let index = 0; index < 10; index += 1) {
    warnings = collectTaskWarning(warnings, { kind: "log", level: "warning", message: `warning ${index}` });
  }
  assert.equal(warnings.length, 6);
  assert.equal(warnings.at(-1), "warning 4");
});

test("log and player-skip events leave visual progress unchanged", () => {
  const current = emptyProgress();
  const skipped = event({ event: "playerSkipped", round: 1, steamId: "1", reason: "missing pawn" });
  assert.equal(nextTaskProgress(current, { kind: "log", level: "warning", message: "warning" }, words), current);
  assert.equal(nextTaskProgress(current, skipped, words), current);
  assert.deepEqual(collectTaskWarning([], skipped), ["Round 1: missing pawn"]);
});

test("round progress counts processed selections but excludes policy skips", () => {
  let current = nextTaskProgress(emptyProgress(), event({ event: "analysisFinished", rounds: 5, selectedRounds: 2, estimatedFiles: 4 }), words);
  current = nextTaskProgress(current, event({ event: "roundFinished", round: 1, files: 2 }), words);
  current = nextTaskProgress(current, event({ event: "roundSkipped", round: 3, reason: "suspicious (too long)" }), words);
  current = nextTaskProgress(current, event({ event: "roundSkipped", round: 4, reason: "not selected" }), words);
  assert.equal(current.completedRounds, 1);
  current = nextTaskProgress(current, event({ event: "roundSkipped", round: 2, reason: "no replayable players" }), words);
  assert.equal(current.completedRounds, 2);
  assert.equal(current.selectedRounds, 2);
  assert.equal(current.announcement, "Round 2");
});

test("artifact progress resets counters and later phases clear stale file details", () => {
  let current = nextTaskProgress({ ...emptyProgress(), written: 12 }, event({ event: "artifactsWritingStarted", root: "archive", artifacts: 2 }), words);
  assert.equal(current.written, 0);
  assert.equal(current.estimated, 2);
  assert.equal(current.unit, "artifacts");
  assert.equal(current.announcement, words.writingArtifacts);
  current = nextTaskProgress(current, event({ event: "artifactWritten", path: "archive/manifest.json", artifactKind: "manifest" }), words);
  assert.equal(current.written, 1);
  assert.equal(current.currentItem, "manifest.json");
  assert.equal(nextTaskProgress(current, { kind: "phase", phase: "exporting" }, words).phase, "artifacts");
  for (const phase of ["voice", "validating"] as const) {
    const next = nextTaskProgress(current, { kind: "phase", phase }, words);
    assert.equal(next.phase, phase);
    assert.equal(next.unit, null);
    assert.equal(next.currentItem, undefined);
  }
});
