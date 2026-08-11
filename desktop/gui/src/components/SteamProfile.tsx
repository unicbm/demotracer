/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import type { SteamProfile } from "../types";
import "./steam-profile.css";

export type SteamProfileMap = ReadonlyMap<string, SteamProfile>;

const memoryProfileCache = new Map<string, SteamProfile>();
const pendingProfileRequests = new Map<string, Promise<SteamProfile | null>>();
const PROFILE_REQUEST_BATCH_SIZE = 32;
const demoPlayerColors: Readonly<Record<string, string>> = {
  blue: "#62a8f5",
  green: "#69bd5b",
  yellow: "#e9c849",
  orange: "#e58a3b",
  purple: "#b878df",
};

export function demoPlayerColorValue(value: string | null | undefined): string | undefined {
  return value ? demoPlayerColors[value.trim().toLowerCase()] : undefined;
}

function cachedProfileMap(steamIds: string[]): Map<string, SteamProfile> {
  return new Map(steamIds.flatMap((steamId) => {
    const profile = memoryProfileCache.get(steamId);
    return profile ? [[steamId, profile] as const] : [];
  }));
}

function requestProfiles(steamIds: string[]): Promise<SteamProfile[]> {
  const missing = steamIds.filter((steamId) =>
    !memoryProfileCache.has(steamId) && !pendingProfileRequests.has(steamId));
  for (let offset = 0; offset < missing.length; offset += PROFILE_REQUEST_BATCH_SIZE) {
    const batchSteamIds = missing.slice(offset, offset + PROFILE_REQUEST_BATCH_SIZE);
    const batch = invoke<SteamProfile[]>("load_steam_profiles", { steamIds: batchSteamIds })
      .then((profiles) => {
        profiles.forEach((profile) => memoryProfileCache.set(profile.steamId, profile));
        return new Map(profiles.map((profile) => [profile.steamId, profile]));
      })
      .catch(() => new Map<string, SteamProfile>());
    batchSteamIds.forEach((steamId) => {
      const request = batch
        .then((profiles) => profiles.get(steamId) ?? null)
        .finally(() => pendingProfileRequests.delete(steamId));
      pendingProfileRequests.set(steamId, request);
    });
  }

  return Promise.all(steamIds.map((steamId) => {
    const cached = memoryProfileCache.get(steamId);
    return cached ? Promise.resolve(cached) : pendingProfileRequests.get(steamId) ?? Promise.resolve(null);
  })).then((profiles) => profiles.filter((profile): profile is SteamProfile => profile !== null));
}

export function useSteamProfiles(steamIds: string[]): SteamProfileMap {
  const requestKey = useMemo(() => [...new Set(steamIds
    .map((steamId) => steamId.trim())
    .filter((steamId) => /^[1-9]\d{16}$/.test(steamId)))].sort().join(","), [steamIds]);
  const [profiles, setProfiles] = useState<SteamProfileMap>(() => cachedProfileMap(requestKey ? requestKey.split(",") : []));

  useEffect(() => {
    const requested = requestKey ? requestKey.split(",") : [];
    if (requested.length === 0) {
      setProfiles(new Map());
      return undefined;
    }

    const cached = cachedProfileMap(requested);
    setProfiles(cached);
    const missing = requested.filter((steamId) => !cached.has(steamId));
    if (missing.length === 0) return undefined;

    let active = true;
    void requestProfiles(missing)
      .then(() => {
        if (active) setProfiles(cachedProfileMap(requested));
      })
      .catch(() => {
        if (active) setProfiles(cachedProfileMap(requested));
      });
    return () => {
      active = false;
    };
  }, [requestKey]);

  return profiles;
}

export function currentSteamAlias(profile: SteamProfile | undefined, demoName: string): string | null {
  const alias = profile?.personaName.trim();
  const normalize = (value: string) => value.toLocaleLowerCase().replace(/[\p{P}\p{S}\s]+/gu, "");
  if (!alias || normalize(alias) === normalize(demoName)) return null;
  return alias;
}

export function teamRepresentative<T extends { name: string; steamId: string }>(teamName: string, players: T[]): T | undefined {
  const normalizedTeam = teamName.trim().toLocaleLowerCase().replace(/^team[\s_-]*/, "").replace(/[\s_-]+/g, "");
  return players.find((player) => player.name.toLocaleLowerCase().replace(/[\s_-]+/g, "") === normalizedTeam)
    ?? [...players].sort((left, right) => left.name.localeCompare(right.name))[0];
}

function useRetryingImage(url: string | null | undefined) {
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const retryTimer = useRef<number | null>(null);

  useEffect(() => {
    if (retryTimer.current !== null) window.clearTimeout(retryTimer.current);
    retryTimer.current = null;
    setAttempt(0);
    setFailed(false);
    return () => {
      if (retryTimer.current !== null) window.clearTimeout(retryTimer.current);
    };
  }, [url]);

  const retry = () => {
    if (retryTimer.current !== null) return;
    if (attempt >= 2) {
      setFailed(true);
      return;
    }
    retryTimer.current = window.setTimeout(() => {
      retryTimer.current = null;
      setAttempt((current) => current + 1);
    }, 750 * (attempt + 1));
  };

  if (!url || failed) return { src: null, retry };
  const separator = url.includes("?") ? "&" : "?";
  return {
    src: attempt === 0 ? url : `${url}${separator}demotracer-retry=${attempt}`,
    retry,
  };
}

export function SteamAvatar({
  profile,
  fallbackName,
  playerColor,
  size = "normal",
}: {
  profile?: SteamProfile;
  fallbackName: string;
  playerColor?: string | null;
  size?: "compact" | "normal" | "hero" | "large" | "profile";
}) {
  const initial = Array.from(fallbackName.trim())[0]?.toLocaleUpperCase() || "?";
  const accent = demoPlayerColorValue(playerColor);
  const avatarStyle = accent
    ? ({ "--steam-avatar-accent": accent } as CSSProperties)
    : undefined;
  const avatarImage = useRetryingImage(profile?.avatarUrl);
  const frameImage = useRetryingImage(profile?.avatarFrameUrl);
  const loading = size === "compact" || size === "hero" || size === "profile" ? "eager" : "lazy";

  return (
    <span className={`steam-avatar is-${size}${accent ? " has-player-color" : ""}${frameImage.src ? " has-profile-frame" : ""}`} style={avatarStyle} title={profile?.personaName} aria-hidden="true">
      <span>{initial}</span>
      {avatarImage.src ? (
        <img
          className="steam-avatar-image"
          src={avatarImage.src}
          alt=""
          loading={loading}
          draggable={false}
          referrerPolicy="no-referrer"
          onError={avatarImage.retry}
        />
      ) : null}
      {frameImage.src ? (
        <img
          className="steam-avatar-frame"
          src={frameImage.src}
          alt=""
          loading={loading}
          draggable={false}
          referrerPolicy="no-referrer"
          onError={frameImage.retry}
        />
      ) : null}
    </span>
  );
}

export function SteamPlayerIdentity({
  profile,
  demoName,
  steamId,
  playerColor,
  size = "normal",
  className = "",
  showAlias = true,
}: {
  profile?: SteamProfile;
  demoName: string;
  steamId: string;
  playerColor?: string | null;
  size?: "compact" | "normal" | "hero" | "large" | "profile";
  className?: string;
  showAlias?: boolean;
}) {
  const alias = showAlias ? currentSteamAlias(profile, demoName) : null;
  return (
    <span className={`steam-player-identity ${className}`.trim()} title={`SteamID ${steamId}`}>
      <SteamAvatar profile={profile} fallbackName={demoName} playerColor={playerColor} size={size} />
      <span className="steam-player-labels">
        <span className="steam-player-name-row">
          <strong title={demoName}>{demoName}</strong>
          {alias ? <small title={alias}>{alias}</small> : null}
        </span>
      </span>
    </span>
  );
}
