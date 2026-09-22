/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { DemoLibraryEntry, DemoLibraryScan } from "./types";

const LIBRARY_ROOT_KEY = "demotracer.library-root.v1";
const LIBRARY_ROOTS_KEY = "demotracer.library-roots.v2";
const EXPORT_ROOT_KEY = "demotracer.export-root.v2";
const DEMO_SOURCE_INDEX_KEY = "demotracer.demo-source-index.v1";
const MAX_DEMO_SOURCE_INDEX_ENTRIES = 512;
export const SOURCE_LINK_NOTE_DISMISSED_STORAGE_KEY = "demotracer.library-source-link-note-dismissed.v1";

export function normalizeSourceLinkNoteDismissed(value: unknown): boolean {
  return value === true || value === "true";
}

export interface LibraryPreferences {
  exportRoot: string;
  roots: string[];
}

export type DemoSourceIndex = Record<string, string>;

function normalizedDemoHash(value: string): string | null {
  const hash = value.trim().toLocaleLowerCase();
  return /^[0-9a-f]{64}$/.test(hash) ? hash : null;
}

function isDemoPath(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const lowered = value.trim().toLocaleLowerCase();
  return lowered.endsWith(".dem") || lowered.endsWith(".dem.zst");
}

export function storedDemoSourceIndex(): DemoSourceIndex {
  try {
    const stored = JSON.parse(localStorage.getItem(DEMO_SOURCE_INDEX_KEY) ?? "null") as unknown;
    if (!stored || typeof stored !== "object" || Array.isArray(stored)) return {};
    const entries = Object.entries(stored)
      .filter(([hash, path]) => normalizedDemoHash(hash) !== null && isDemoPath(path))
      .slice(-MAX_DEMO_SOURCE_INDEX_ENTRIES)
      .map(([hash, path]) => [normalizedDemoHash(hash)!, (path as string).trim()]);
    return Object.fromEntries(entries);
  } catch {
    return {};
  }
}

export function rememberDemoSource(index: DemoSourceIndex, demoSha256: string, path: string): DemoSourceIndex {
  const hash = normalizedDemoHash(demoSha256);
  if (!hash || !isDemoPath(path)) return index;
  const entries = Object.entries(index)
    .filter(([candidate]) => candidate !== hash)
    .slice(-(MAX_DEMO_SOURCE_INDEX_ENTRIES - 1));
  return Object.fromEntries([...entries, [hash, path.trim()]]);
}

export function persistDemoSourceIndex(index: DemoSourceIndex) {
  try {
    localStorage.setItem(DEMO_SOURCE_INDEX_KEY, JSON.stringify(index));
  } catch {
    // The archive-local source pointer remains authoritative if storage is unavailable.
  }
}

export function normalizeLibraryRoot(root: string): string {
  const value = root.trim();
  if (value === "/") return value;
  const driveRoot = value.match(/^([a-z]):[\\/]+$/i);
  if (driveRoot) return `${driveRoot[1].toUpperCase()}:\\`;
  return value.replace(/[\\/]+$/, "");
}

function rootKey(root: string): string {
  return normalizeLibraryRoot(root).replace(/\\/g, "/").toLocaleLowerCase();
}

function manifestPathKey(path: string): string {
  return path.trim().replace(/\\/g, "/").toLocaleLowerCase();
}

export function isReusableDemoArchive(entry: DemoLibraryEntry): boolean {
  return entry.metadataStatus === "current"
    && entry.compatibility !== "unsupported"
    && entry.sourceAvailable !== false
    && entry.rounds > 0
    && entry.files > 0;
}

export function demoLibraryTimestamp(entry: DemoLibraryEntry): number {
  if (entry.playedAt?.trim()) {
    const playedAt = Date.parse(entry.playedAt);
    if (Number.isFinite(playedAt)) return playedAt;
  }
  return entry.sourceModifiedAtMs ?? entry.modifiedAtMs ?? 0;
}

export function librarySeriesForManifest(
  entries: readonly DemoLibraryEntry[],
  manifestPath: string,
): DemoLibraryEntry[] {
  const active = entries.find(
    (entry) => manifestPathKey(entry.manifestPath) === manifestPathKey(manifestPath),
  );
  const seriesId = active?.series?.id;
  if (!seriesId) return [];
  const series = entries
    .filter((entry) => entry.series?.id === seriesId)
    .sort((left, right) => (
      (left.series?.order ?? Number.MAX_SAFE_INTEGER)
      - (right.series?.order ?? Number.MAX_SAFE_INTEGER)
      || left.manifestPath.localeCompare(right.manifestPath)
    ));
  return series.length >= 2 ? series : [];
}

export function uniqueLibraryRoots(roots: Iterable<string>): string[] {
  const unique = new Map<string, string>();
  for (const candidate of roots) {
    const root = normalizeLibraryRoot(candidate);
    if (!root) continue;
    const key = rootKey(root);
    if (!unique.has(key)) unique.set(key, root);
  }
  return [...unique.values()];
}

export function withExportRoot(roots: Iterable<string>, exportRoot: string): string[] {
  return uniqueLibraryRoots([exportRoot, ...roots]);
}

export function storedLibraryPreferences(): LibraryPreferences {
  const legacyRoot = localStorage.getItem(LIBRARY_ROOT_KEY)?.trim() ?? "";
  const legacyOutput = localStorage.getItem("demotracer.output")?.trim() ?? "";
  const exportRoot = normalizeLibraryRoot(localStorage.getItem(EXPORT_ROOT_KEY)?.trim() || legacyRoot || legacyOutput);
  let roots: string[] = [];
  try {
    const stored = JSON.parse(localStorage.getItem(LIBRARY_ROOTS_KEY) ?? "[]");
    if (Array.isArray(stored)) roots = stored.filter((value): value is string => typeof value === "string");
  } catch {
    roots = [];
  }
  return {
    exportRoot,
    roots: withExportRoot(roots.length > 0 ? roots : [legacyRoot, legacyOutput], exportRoot),
  };
}

export function persistLibraryPreferences(preferences: LibraryPreferences) {
  const exportRoot = normalizeLibraryRoot(preferences.exportRoot);
  const roots = withExportRoot(preferences.roots, exportRoot);
  try {
    if (exportRoot) {
      localStorage.setItem(EXPORT_ROOT_KEY, exportRoot);
      localStorage.setItem("demotracer.output", exportRoot);
      localStorage.setItem(LIBRARY_ROOT_KEY, exportRoot);
    } else {
      localStorage.removeItem(EXPORT_ROOT_KEY);
    }
    localStorage.setItem(LIBRARY_ROOTS_KEY, JSON.stringify(roots));
  } catch {
    // Preferences are convenient state; conversion must remain usable if storage is unavailable.
  }
}

export function mergeLibraryScan(scan: DemoLibraryScan): DemoLibraryScan {
  // Portable demo-info.json is the only source of analyzed archive metadata.
  // Browser-local score caches are deliberately never merged: they cannot be
  // revisioned with the archive and previously preserved incorrect snapshots.
  return scan;
}

export function mergeLibraryScans(scans: DemoLibraryScan[], root: string): DemoLibraryScan {
  const entries = new Map<string, DemoLibraryScan["entries"][number]>();
  for (const scan of scans) {
    for (const entry of scan.entries) {
      const key = `path:${rootKey(entry.manifestPath)}`;
      if (!entries.has(key)) entries.set(key, entry);
    }
  }
  return mergeLibraryScan({
    root,
    entries: [...entries.values()],
    skipped: scans.flatMap((scan) => scan.skipped),
  });
}
