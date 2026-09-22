/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { fileName } from "./displayFormat.ts";
import type { TextDictionary } from "./i18n";
import type { TaskEvent, TaskPhase } from "./types";

export type ProgressPhase = Exclude<TaskPhase, "exporting"> | "preparing" | "writing" | "artifacts";

export interface ProgressState {
  phase: ProgressPhase;
  written: number;
  estimated: number;
  unit: "playerFiles" | "artifacts" | null;
  currentRound?: number;
  completedRounds: number;
  selectedRounds: number;
  currentItem?: string;
  announcement: string;
}

export function emptyProgress(): ProgressState {
  return {
    phase: "preparing",
    written: 0,
    estimated: 0,
    unit: null,
    completedRounds: 0,
    selectedRounds: 0,
    announcement: "",
  };
}

export function collectTaskWarning(warnings: string[], task: TaskEvent): string[] {
  let message: string | null = null;
  if (task.kind === "log" && task.level === "warning") message = task.message;
  if (task.kind === "progress") {
    const event = task.progress;
    if (event.event === "playerSkipped" || event.event === "roundSkipped" && event.reason !== "not selected") {
      message = `Round ${event.round}: ${event.reason}`;
    }
  }
  return message === null || warnings.includes(message) || warnings.length >= 6
    ? warnings
    : [...warnings, message];
}

export function nextTaskProgress(
  current: ProgressState,
  task: TaskEvent,
  words: Pick<TextDictionary, "preparing" | "writingPlayers" | "writingArtifacts">,
): ProgressState {
  if (task.kind === "log") return current;
  if (task.kind === "phase") {
    const clearItem = task.phase === "voice" || task.phase === "validating";
    return {
      ...current,
      phase: task.phase === "exporting" ? current.phase : task.phase,
      unit: clearItem ? null : current.unit,
      currentItem: clearItem ? undefined : current.currentItem,
      announcement: task.phase,
    };
  }

  const event = task.progress;
  switch (event.event) {
    case "analysisStarted":
      return { ...current, phase: "preparing", announcement: words.preparing };
    case "analysisFinished":
      return {
        ...current,
        phase: "writing",
        written: 0,
        estimated: event.estimatedFiles,
        unit: "playerFiles",
        selectedRounds: event.selectedRounds,
        announcement: words.writingPlayers,
      };
    case "roundStarted":
      return { ...current, phase: "writing", currentRound: event.round, currentItem: undefined };
    case "playerWritten":
      return { ...current, written: current.written + 1, currentItem: `${event.playerName} · ${event.side}` };
    case "roundFinished":
      return { ...current, completedRounds: current.completedRounds + 1, announcement: `Round ${event.round}` };
    case "roundSkipped":
      if (event.reason === "not selected") return current;
      return {
        ...current,
        completedRounds: current.completedRounds + (event.reason.startsWith("suspicious (") ? 0 : 1),
        announcement: `Round ${event.round}`,
      };
    case "playerSkipped":
      return current;
    case "artifactsWritingStarted":
      return {
        ...current,
        phase: "artifacts",
        written: 0,
        estimated: event.artifacts,
        unit: "artifacts",
        currentItem: event.root,
        announcement: words.writingArtifacts,
      };
    case "artifactWritten":
      return { ...current, written: current.written + 1, currentItem: fileName(event.path) };
    case "finished":
      return { ...current, currentItem: fileName(event.manifestPath) };
  }
}
