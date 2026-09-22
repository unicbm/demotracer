/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import { activeBatchItemCount, batchJobPhase, batchRunState, findRestorableBatch, nextBatchItemProgress } from "./batchSession.ts";
import type { BatchLedger } from "./types.ts";

function ledger(status: BatchLedger["status"], itemStatuses: BatchLedger["items"][number]["status"][]): BatchLedger {
  return {
    status,
    items: itemStatuses.map((itemStatus, index) => ({
      itemId: String(index),
      status: itemStatus,
    } as BatchLedger["items"][number])),
  } as BatchLedger;
}

test("completed batch history never reappears as the current import", () => {
  const completedWithErrors = ledger("completedWithErrors", ["completed", "failed", "failed"]);
  const completed = ledger("completed", ["completed", "completed", "completed"]);

  assert.equal(findRestorableBatch([completedWithErrors, completed]), undefined);
});

test("only interrupted work is restored and counted", () => {
  const interrupted = ledger("paused", ["completed", "pending", "running", "failed"]);

  assert.equal(findRestorableBatch([ledger("completed", ["completed"]), interrupted]), interrupted);
  assert.equal(activeBatchItemCount(interrupted), 2);
});

test("restored work stays interrupted until this process starts the invocation", () => {
  for (const status of ["pending", "paused", "running", "stopping"] as const) {
    assert.equal(batchRunState(status, false), "interrupted");
  }
  assert.equal(batchRunState("paused", true), "running");
  assert.equal(batchRunState("stopping", true), "stopping");
  assert.equal(batchRunState("completedWithErrors", false), "complete");
  assert.equal(batchRunState(undefined, false), "idle");
  assert.equal(batchRunState(undefined, true), "running");
});

test("non-progress events retain the existing progress state", () => {
  const current = { written: 2, estimated: 5, progress: 0.4, startedAtMs: 100 };
  assert.equal(nextBatchItemProgress(current, { kind: "log", level: "warning", message: "round skipped" }), current);
  assert.equal(nextBatchItemProgress(current, { kind: "progress", progress: { event: "roundSkipped", round: 2, reason: "missing evidence" } }), current);
  assert.equal(nextBatchItemProgress(current, { kind: "phase", phase: "voice" }), current);
  assert.equal(batchJobPhase("voice"), "converting");
});

test("artifact estimates cannot report completion before the task completes", () => {
  let progress = nextBatchItemProgress(undefined, {
    kind: "progress", progress: { event: "artifactsWritingStarted", root: "archive", artifacts: 0 },
  });
  for (let index = 0; index < 3; index += 1) {
    progress = nextBatchItemProgress(progress, {
      kind: "progress", progress: { event: "artifactWritten", path: "manifest.json", artifactKind: "manifest" },
    });
  }
  assert.ok(Number.isFinite(progress.progress));
  assert.ok(progress.progress! < 1);
  const completed = nextBatchItemProgress(progress, { kind: "phase", phase: "complete" });
  assert.equal(completed.progress, 1);
  assert.equal(completed.startedAtMs, progress.startedAtMs);
  assert.equal(batchJobPhase("complete"), "completed");
});
