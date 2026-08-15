/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { useEffect, useState } from "react";
import type { DemoLibraryEntry } from "../types";

interface AvatarPlayerEvidence {
  steamId: string;
}

const archiveAvatarCache = new Map<string, string>();
const pendingArchiveAvatars = new Map<string, Promise<string | null>>();

export function teamAvatarEvidence(
  entry: Pick<DemoLibraryEntry, "avatarOverrides"> | null | undefined,
  players: readonly AvatarPlayerEvidence[],
) {
  const steamIds = new Set(players.map((player) => player.steamId.trim()).filter(Boolean));
  const candidates = (entry?.avatarOverrides ?? []).filter((avatar) => steamIds.has(avatar.steamId));
  const evidencedSteamIds = new Set(candidates.map((avatar) => avatar.steamId));
  const unique = new Map(candidates.map((avatar) => [avatar.sha256, avatar]));
  const minimumEvidence = Math.min(2, steamIds.size);
  return evidencedSteamIds.size >= minimumEvidence && unique.size === 1
    ? [...unique.values()][0]
    : null;
}

export function useArchiveTeamAvatar(
  entry: Pick<DemoLibraryEntry, "manifestPath" | "avatarOverrides"> | null | undefined,
  players: readonly AvatarPlayerEvidence[],
  enabled: boolean,
): string | null {
  const evidence = teamAvatarEvidence(entry, players);
  const manifestPath = entry?.manifestPath ?? "";
  const cacheKey = evidence ? `${manifestPath}\n${evidence.sha256}` : "";
  const [url, setUrl] = useState<string | null>(() => archiveAvatarCache.get(cacheKey) ?? null);

  useEffect(() => {
    if (!enabled || !manifestPath || !evidence || !("__TAURI_INTERNALS__" in window)) {
      setUrl(null);
      return undefined;
    }
    const cached = archiveAvatarCache.get(cacheKey);
    if (cached) {
      setUrl(cached);
      return undefined;
    }
    setUrl(null);
    let active = true;
    let request = pendingArchiveAvatars.get(cacheKey);
    if (!request) {
      request = invoke<string | null>("load_library_avatar", {
        request: {
          manifestPath,
          relativePath: evidence.path,
          sha256: evidence.sha256,
        },
      }).catch(() => null).finally(() => pendingArchiveAvatars.delete(cacheKey));
      pendingArchiveAvatars.set(cacheKey, request);
    }
    void request.then((loaded) => {
      if (loaded) archiveAvatarCache.set(cacheKey, loaded);
      if (active) setUrl(loaded);
    });
    return () => { active = false; };
  }, [cacheKey, enabled, manifestPath, evidence?.path, evidence?.sha256]);

  return url;
}
