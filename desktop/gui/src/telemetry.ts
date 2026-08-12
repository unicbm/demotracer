/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { DemoSource } from "./types";

export const AGGREGATE_TELEMETRY_STORAGE_KEY = "demotracer.aggregate-telemetry.v1";
export const PRESENCE_TELEMETRY_CONSENT_STORAGE_KEY = "demotracer.presence-telemetry-consent.v1";
const LEGACY_TELEMETRY_CONSENT_STORAGE_KEY = "demotracer.telemetry-consent.v1";

export type TelemetryPresenceConsent = "unknown" | "enabled" | "disabled";
export type TelemetryDemoSource =
  | "5e"
  | "perfect-world"
  | "faceit"
  | "valve-premier"
  | "matchmaking"
  | "pracc"
  | "popflash"
  | "esportal"
  | "gamers-club"
  | "fastcup"
  | "renown"
  | "cevo"
  | "challengermode"
  | "esea"
  | "starladder"
  | "flashpoint"
  | "blast"
  | "pgl"
  | "esl"
  | "matchzy"
  | "ebot"
  | "get5"
  | "other"
  | "unknown";

export type TelemetryRoundsBucket = "0" | "1-4" | "5-12" | "13-24" | "25+" | "unknown";
export type TelemetryDurationBucket = "<10s" | "10-29s" | "30-59s" | "1-2m" | "3-9m" | "10m+" | "unknown";

export interface TelemetrySubmission {
  kind: "session" | "analysis" | "conversion";
  outcome: "ping" | "success" | "failure";
  demoSource?: TelemetryDemoSource;
  errorCode?: string;
  roundsBucket?: TelemetryRoundsBucket;
  durationBucket?: TelemetryDurationBucket;
}

const SOURCE_CATEGORIES: Readonly<Record<string, TelemetryDemoSource>> = {
  "5e": "5e",
  "perfect world": "perfect-world",
  "faceit": "faceit",
  "valve premier": "valve-premier",
  "matchmaking": "matchmaking",
  "pracc": "pracc",
  "popflash": "popflash",
  "esportal": "esportal",
  "gamers club": "gamers-club",
  "fastcup": "fastcup",
  "renown": "renown",
  "cevo": "cevo",
  "challengermode": "challengermode",
  "esea": "esea",
  "starladder": "starladder",
  "flashpoint": "flashpoint",
  "blast": "blast",
  "pgl": "pgl",
  "esl": "esl",
  "matchzy": "matchzy",
  "ebot": "ebot",
  "get5": "get5",
};

export function storedAggregateTelemetryEnabled(storage: Pick<Storage, "getItem"> = localStorage): boolean {
  const value = storage.getItem(AGGREGATE_TELEMETRY_STORAGE_KEY);
  if (value === "enabled") return true;
  if (value === "disabled") return false;
  return storage.getItem(LEGACY_TELEMETRY_CONSENT_STORAGE_KEY) !== "disabled";
}

export function storedPresenceTelemetryConsent(
  storage: Pick<Storage, "getItem"> = localStorage,
): TelemetryPresenceConsent {
  const value = storage.getItem(PRESENCE_TELEMETRY_CONSENT_STORAGE_KEY)
    ?? storage.getItem(LEGACY_TELEMETRY_CONSENT_STORAGE_KEY);
  return value === "enabled" || value === "disabled" ? value : "unknown";
}

export function telemetryDemoSource(source?: DemoSource | null): TelemetryDemoSource {
  if (!source?.name.trim()) return "unknown";
  return SOURCE_CATEGORIES[source.name.trim().toLocaleLowerCase()] ?? "other";
}

export function telemetryRoundsBucket(rounds?: number | null): TelemetryRoundsBucket {
  if (rounds == null || !Number.isFinite(rounds) || rounds < 0) return "unknown";
  if (rounds === 0) return "0";
  if (rounds <= 4) return "1-4";
  if (rounds <= 12) return "5-12";
  if (rounds <= 24) return "13-24";
  return "25+";
}

export function telemetryDurationBucket(elapsedMs?: number | null): TelemetryDurationBucket {
  if (elapsedMs == null || !Number.isFinite(elapsedMs) || elapsedMs < 0) return "unknown";
  if (elapsedMs < 10_000) return "<10s";
  if (elapsedMs < 30_000) return "10-29s";
  if (elapsedMs < 60_000) return "30-59s";
  if (elapsedMs < 3 * 60_000) return "1-2m";
  if (elapsedMs < 10 * 60_000) return "3-9m";
  return "10m+";
}
