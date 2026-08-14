/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

export const IGNORED_UPDATE_VERSIONS_STORAGE_KEY = "demotracer.ignored-update-versions.v1";
export const PENDING_PLAYBACK_UPDATE_STORAGE_KEY = "demotracer.pending-playback-update.v1";

export interface IgnoredUpdateVersions {
  gui?: string;
  playback?: string;
}

export interface PendingPlaybackUpdate {
  guiVersion: string;
  playbackVersion: string;
}

function normalizedVersion(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const version = value.trim();
  return version && version.length <= 64 ? version : undefined;
}

export function normalizeIgnoredUpdateVersions(value: unknown): IgnoredUpdateVersions {
  try {
    const record = typeof value === "string" ? JSON.parse(value) as unknown : value;
    if (!record || typeof record !== "object" || Array.isArray(record)) return {};
    const candidate = record as Record<string, unknown>;
    const gui = normalizedVersion(candidate.gui);
    const playback = normalizedVersion(candidate.playback);
    return {
      ...(gui ? { gui } : {}),
      ...(playback ? { playback } : {}),
    };
  } catch {
    return {};
  }
}

export function updateVersionIsIgnored(
  ignored: IgnoredUpdateVersions,
  component: keyof IgnoredUpdateVersions,
  version?: string | null,
): boolean {
  const normalized = normalizedVersion(version);
  return Boolean(normalized && ignored[component] === normalized);
}

export function normalizePendingPlaybackUpdate(value: unknown): PendingPlaybackUpdate | null {
  try {
    const record = typeof value === "string" ? JSON.parse(value) as unknown : value;
    if (!record || typeof record !== "object" || Array.isArray(record)) return null;
    const candidate = record as Record<string, unknown>;
    const guiVersion = normalizedVersion(candidate.guiVersion);
    const playbackVersion = normalizedVersion(candidate.playbackVersion);
    return guiVersion && playbackVersion ? { guiVersion, playbackVersion } : null;
  } catch {
    return null;
  }
}
