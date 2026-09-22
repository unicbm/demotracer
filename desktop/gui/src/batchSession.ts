/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { BatchItemPhase, BatchLedger, TaskEvent } from "./types";

export const BATCH_SELECTION_LIMIT = 8;
export type BatchConcurrency = "auto" | 2 | 4 | 6 | 8;
export type BatchJobPhase = Exclude<BatchItemPhase, "voice" | "complete"> | "completed";
export type BatchRunState = "idle" | "running" | "stopping" | "interrupted" | "complete";

export interface BatchItemProgress {
  progress?: number | null;
  startedAtMs?: number;
  finishedAtMs?: number;
  written: number;
  estimated: number;
}

export function batchJobPhase(phase: BatchItemPhase): BatchJobPhase {
  if (phase === "complete") return "completed";
  if (phase === "voice") return "converting";
  return phase;
}

export function batchRunState(status: BatchLedger["status"] | undefined, invocationActive: boolean): BatchRunState {
  if (invocationActive) return status === "stopping" ? "stopping" : "running";
  if (status === "completed" || status === "completedWithErrors") return "complete";
  return status ? "interrupted" : "idle";
}

export function nextBatchItemProgress(current: BatchItemProgress | undefined, task: TaskEvent): BatchItemProgress {
  const next: BatchItemProgress = current ?? { written: 0, estimated: 0, startedAtMs: Date.now() };
  if (task.kind === "phase") {
    return task.phase === "complete" && next.progress !== 1 ? { ...next, progress: 1 } : next;
  }
  if (task.kind === "log") return next;

  const event = task.progress;
  switch (event.event) {
    case "analysisStarted":
      return { ...next, progress: 0.02 };
    case "analysisFinished":
      return { ...next, progress: 0.05, written: 0, estimated: Math.max(1, event.estimatedFiles) };
    case "playerWritten": {
      const written = next.written + 1;
      return {
        ...next,
        written,
        progress: Math.min(0.88, 0.05 + 0.83 * (written / Math.max(1, next.estimated))),
      };
    }
    case "artifactsWritingStarted":
      return { ...next, progress: 0.9, written: 0, estimated: Math.max(1, event.artifacts) };
    case "artifactWritten": {
      const written = next.written + 1;
      return {
        ...next,
        written,
        progress: Math.min(0.99, 0.9 + 0.09 * (written / Math.max(1, next.estimated))),
      };
    }
    case "finished":
      return { ...next, progress: 1, finishedAtMs: Date.now() };
    default:
      return next;
  }
}

export function findRestorableBatch(ledgers: readonly BatchLedger[]): BatchLedger | undefined {
  return ledgers.find((ledger) => (
    ledger.status === "pending"
    || ledger.status === "paused"
    || ledger.status === "running"
    || ledger.status === "stopping"
  ));
}

export function activeBatchItemCount(ledger: BatchLedger | null): number {
  return ledger?.items.filter((item) => item.status === "pending" || item.status === "running").length ?? 0;
}
