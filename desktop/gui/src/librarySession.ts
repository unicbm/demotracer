/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { PlayerSelection } from "./components/PlayerRoster";
import type { CommandMode } from "./components/TaskViews";
import type { ManifestArchive, WorkspaceSection } from "./types";

export const LIBRARY_SESSION_STORAGE_KEY = "demotracer.library-session.v1";

export interface StoredLibrarySession {
  manifestPath: string;
  selectedRound: number | null;
  selectedPlayer: PlayerSelection | null;
  commandMode: CommandMode;
}

export interface LibraryWorkspaceState {
  activeSection: WorkspaceSection;
  archive: ManifestArchive | null;
  archivePath: string;
  selectedRound: number | null;
  selectedPlayer: PlayerSelection | null;
  commandMode: CommandMode;
}

export type LibraryWorkspaceAction =
  | { type: "navigate"; section: WorkspaceSection }
  | { type: "clear" }
  | { type: "opening"; path: string }
  | { type: "open"; archive: ManifestArchive; restored?: StoredLibrarySession | null }
  | { type: "replaceArchive"; archive: ManifestArchive }
  | { type: "selectRound"; round: number | null; forceRoundMode?: boolean }
  | { type: "selectPlayer"; player: PlayerSelection | null }
  | { type: "setCommandMode"; mode: CommandMode };

export const EMPTY_LIBRARY_WORKSPACE: LibraryWorkspaceState = {
  activeSection: "library",
  archive: null,
  archivePath: "",
  selectedRound: null,
  selectedPlayer: null,
  commandMode: "sequence",
};

export function buildArchiveSessionMeta(
  title: string,
  sourceFileName: string,
  map: string,
  roundCount: number,
  roundsLabel: string,
): string {
  const normalizedTitle = normalizeDemoLabel(title);
  const normalizedSource = normalizeDemoLabel(sourceFileName);
  const distinctSource = normalizedSource && normalizedSource !== normalizedTitle
    ? sourceFileName.trim()
    : "";
  return [distinctSource, map, `${roundCount} ${roundsLabel}`]
    .filter(Boolean)
    .join(" · ");
}

function normalizeDemoLabel(value: string): string {
  return value.trim().replace(/\.dem$/i, "").toLowerCase();
}

export function libraryWorkspaceReducer(
  state: LibraryWorkspaceState,
  action: LibraryWorkspaceAction,
): LibraryWorkspaceState {
  switch (action.type) {
    case "navigate":
      return { ...state, activeSection: action.section };
    case "clear":
      return { ...EMPTY_LIBRARY_WORKSPACE };
    case "opening":
      return {
        ...state,
        activeSection: "analysis",
        archivePath: action.path,
        selectedPlayer: null,
      };
    case "open": {
      const availableRounds = action.archive.rounds.filter((round) => round.available);
      const firstAvailableRound = availableRounds[0];
      const restored = action.restored?.manifestPath.toLocaleLowerCase()
        === action.archive.manifestPath.toLocaleLowerCase()
        ? action.restored
        : null;
      const selectedRound = restored?.selectedRound !== null
        && restored?.selectedRound !== undefined
        && availableRounds.some((round) => round.round === restored.selectedRound)
        ? restored.selectedRound
        : firstAvailableRound?.round ?? null;
      const commandMode = restored?.commandMode
        ?? (availableRounds.length > 1 && (firstAvailableRound?.sequenceLength ?? 0) > 0 ? "sequence" : "round");
      return {
        activeSection: "analysis",
        archive: action.archive,
        archivePath: action.archive.manifestPath,
        selectedRound,
        selectedPlayer: restored?.selectedPlayer ?? null,
        commandMode,
      };
    }
    case "replaceArchive":
      return state.archive?.manifestPath === action.archive.manifestPath
        ? { ...state, archive: action.archive, archivePath: action.archive.manifestPath }
        : state;
    case "selectRound":
      return {
        ...state,
        selectedRound: action.round,
        commandMode: action.forceRoundMode ? "round" : state.commandMode,
      };
    case "selectPlayer":
      return { ...state, selectedPlayer: action.player };
    case "setCommandMode":
      return { ...state, commandMode: action.mode };
  }
}

export function readStoredLibrarySession(storage: Pick<Storage, "getItem">): StoredLibrarySession | null {
  try {
    const saved = JSON.parse(storage.getItem(LIBRARY_SESSION_STORAGE_KEY) ?? "null") as Partial<StoredLibrarySession> | null;
    if (!saved || typeof saved.manifestPath !== "string" || !saved.manifestPath.toLowerCase().endsWith(".json")) return null;
    return {
      manifestPath: saved.manifestPath,
      selectedRound: typeof saved.selectedRound === "number" ? saved.selectedRound : null,
      selectedPlayer: saved.selectedPlayer
        && typeof saved.selectedPlayer.teamId === "string"
        && typeof saved.selectedPlayer.playerIndex === "number"
        ? saved.selectedPlayer
        : null,
      commandMode: saved.commandMode === "round" ? "round" : "sequence",
    };
  } catch {
    return null;
  }
}

export function writeStoredLibrarySession(
  storage: Pick<Storage, "setItem">,
  session: StoredLibrarySession,
) {
  storage.setItem(LIBRARY_SESSION_STORAGE_KEY, JSON.stringify(session));
}
