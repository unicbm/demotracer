/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useState } from "react";
import type { BatchConcurrency } from "./batchSession";
import type { ActivityLogRange } from "./components/LogsWorkspace";
import {
  DEFAULT_PLAYBACK_ADVANCED_OPTIONS,
  type PlaybackPresetOptions,
} from "./playbackCommand";
import {
  CUSTOM_CSS_PROFILES_STORAGE_KEY,
  CUSTOM_CSS_STORAGE_KEY,
  normalizeCustomCss,
  normalizeCustomCssProfiles,
  normalizeUiFontSize,
  normalizeUiScale,
  recommendedUiScale,
  UI_FONT_SIZE_DEFAULT,
  UI_FONT_SIZE_STORAGE_KEY,
  type CustomCssProfile,
} from "./appearance";
import {
  CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY,
  STARTER_CUSTOM_CSS_PROFILES,
} from "./customCssPresets";
import { COSMETIC_PHRASE, TEXT } from "./i18n";
import { storedLibraryPreferences, uniqueLibraryRoots } from "./library";
import { readStoredLibrarySession } from "./librarySession";
import type {
  AppLogEntry,
  CommandErrorDto,
  ConverterSettings,
  DemoLibraryEntry,
  DemoSourcePreflight,
  Language,
  LocalEnvironmentSettings,
  ManifestArchive,
  PlaybackUpdateStatus,
} from "./types";
import { detailedAnalysisErrorMessage } from "./errorPresentation";

export const DEFAULT_SETTINGS: ConverterSettings = {
  side: "both",
  fullRound: false,
  maxRoundSeconds: 240,
  exportVoice: true,
  exportCosmetics: false,
  exportStickers: false,
  exportCharms: false,
  includeSuspicious: false,
};

export const INITIAL_LIBRARY_PREFERENCES = storedLibraryPreferences();

export const DEFAULT_LOCAL_ENVIRONMENT: LocalEnvironmentSettings = {
  cs2Path: "",
  demoRoots: [],
  soundNotifications: true,
};

export const BATCH_PREFERENCES_STORAGE_KEY = "demotracer.batch-preferences.v1";
export const COSMETIC_CONSENT_STORAGE_KEY = "demotracer.cosmetic-consent.v1";
export const LEGACY_UI_SCALE_STORAGE_KEY = "demotracer.ui-scale.v1";
export const INVENTORY_SIMULATOR_PANEL_WIDTH_KEY = "demotracer.inventory-simulator-panel-width.v1";
export const INVENTORY_SIMULATOR_PANEL_DEFAULT_WIDTH = 580;
export const INVENTORY_SIMULATOR_PANEL_MIN_WIDTH = 440;
export const INVENTORY_SIMULATOR_PANEL_MAX_WIDTH = 900;
export const ACTIVITY_LOG_LIMIT = 5_000;

export function activityLogSinceMs(range: ActivityLogRange): number | null {
  if (range === "all") return null;
  const start = new Date();
  start.setHours(0, 0, 0, 0);
  if (range === "sevenDays") start.setDate(start.getDate() - 6);
  return start.getTime();
}

export const INITIAL_LIBRARY_SESSION = readStoredLibrarySession(localStorage);

export interface StoredBatchPreferences {
  concurrency: BatchConcurrency;
}

export interface DemoPreflightProgress {
  current: number;
  total: number;
  fileName: string;
}

export interface DuplicateDemoConflictState {
  primary: DemoSourcePreflight;
  batch?: {
    selections: DemoSourcePreflight[];
    replaceSourceIds: string[];
    mergedSegments: number;
    relinkedDuplicates: number;
  };
}

export interface SaveArchiveNoteResult {
  manifestPath: string;
  note: string | null;
}

export type ReparseTarget =
  | { kind: "archive"; archive: ManifestArchive }
  | { kind: "library"; entry: DemoLibraryEntry };

export interface InventorySimulatorPanelBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export function measureInventorySimulatorPanel(element: HTMLElement): InventorySimulatorPanelBounds | null {
  const rect = element.getBoundingClientRect();
  if (rect.width < 1 || rect.height < 1) return null;
  const pixelRatio = window.devicePixelRatio || 1;
  return {
    x: Math.max(0, Math.round(rect.left * pixelRatio)),
    y: Math.max(0, Math.round(rect.top * pixelRatio)),
    width: Math.max(1, Math.round(rect.width * pixelRatio)),
    height: Math.max(1, Math.round(rect.height * pixelRatio)),
  };
}

export function normalizeInventorySimulatorPanelWidth(width: number): number {
  const viewportMaximum = Math.max(INVENTORY_SIMULATOR_PANEL_MIN_WIDTH, window.innerWidth - 430);
  return Math.min(
    INVENTORY_SIMULATOR_PANEL_MAX_WIDTH,
    viewportMaximum,
    Math.max(INVENTORY_SIMULATOR_PANEL_MIN_WIDTH, width),
  );
}

export function storedInventorySimulatorPanelWidth(): number {
  const stored = Number(localStorage.getItem(INVENTORY_SIMULATOR_PANEL_WIDTH_KEY));
  return normalizeInventorySimulatorPanelWidth(Number.isFinite(stored) && stored > 0
    ? stored
    : INVENTORY_SIMULATOR_PANEL_DEFAULT_WIDTH);
}

export function storedBatchPreferences(): StoredBatchPreferences {
  try {
    const saved = JSON.parse(localStorage.getItem(BATCH_PREFERENCES_STORAGE_KEY) ?? "null") as Partial<StoredBatchPreferences> | null;
    const concurrency = saved?.concurrency;
    return {
      concurrency: concurrency === "auto" || concurrency === 2 || concurrency === 4 || concurrency === 6 || concurrency === 8
        ? concurrency
        : "auto",
    };
  } catch {
    return { concurrency: "auto" };
  }
}

export const DEFAULT_PLAYBACK_PRESET: PlaybackPresetOptions = {
  weapons: true,
  cosmetics: false,
  steamIdentity: true,
  avatar: false,
  voice: true,
  playoff: false,
  ...DEFAULT_PLAYBACK_ADVANCED_OPTIONS,
};

export function storedLanguage(): Language {
  const saved = localStorage.getItem("demotracer.language");
  if (saved === "zh" || saved === "en") return saved;
  return navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function storedUiFontSize(): number {
  const stored = localStorage.getItem(UI_FONT_SIZE_STORAGE_KEY);
  if (stored !== null) return normalizeUiFontSize(stored);
  const legacyScale = localStorage.getItem(LEGACY_UI_SCALE_STORAGE_KEY);
  if (legacyScale !== null) {
    const scale = normalizeUiScale(legacyScale);
    return normalizeUiFontSize(UI_FONT_SIZE_DEFAULT + Math.round((scale - 1) * 10));
  }
  const recommendedScale = recommendedUiScale(window.screen.width, window.screen.height, window.devicePixelRatio);
  return normalizeUiFontSize(UI_FONT_SIZE_DEFAULT + Math.round((recommendedScale - 1) * 10));
}

export function storedCosmeticConsent(): boolean {
  return localStorage.getItem(COSMETIC_CONSENT_STORAGE_KEY) === "accepted";
}

export function storedSettings(): ConverterSettings {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.settings") ?? "null") as Partial<ConverterSettings> | null;
    if (!saved || typeof saved !== "object" || Array.isArray(saved)) return { ...DEFAULT_SETTINGS };
    return {
      ...DEFAULT_SETTINGS,
      side: saved.side === "both" || saved.side === "t" || saved.side === "ct" ? saved.side : DEFAULT_SETTINGS.side,
      fullRound: typeof saved.fullRound === "boolean" ? saved.fullRound : DEFAULT_SETTINGS.fullRound,
      maxRoundSeconds: typeof saved.maxRoundSeconds === "number"
        && Number.isFinite(saved.maxRoundSeconds)
        && saved.maxRoundSeconds >= 30
        && saved.maxRoundSeconds <= 1800
        ? saved.maxRoundSeconds
        : DEFAULT_SETTINGS.maxRoundSeconds,
      exportVoice: typeof saved.exportVoice === "boolean" ? saved.exportVoice : DEFAULT_SETTINGS.exportVoice,
      exportCosmetics: storedCosmeticConsent() && saved.exportCosmetics === true,
      exportStickers: typeof saved.exportStickers === "boolean" ? saved.exportStickers : DEFAULT_SETTINGS.exportStickers,
      exportCharms: typeof saved.exportCharms === "boolean" ? saved.exportCharms : DEFAULT_SETTINGS.exportCharms,
      includeSuspicious: false,
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

export function storedPlaybackPreset(): PlaybackPresetOptions {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.playback-preset.v1") ?? "null") as Partial<PlaybackPresetOptions> | null;
    if (!saved || typeof saved !== "object") return { ...DEFAULT_PLAYBACK_PRESET };
    const readBoolean = (key: "weapons" | "cosmetics" | "steamIdentity" | "avatar" | "voice" | "playoff" | "threat360Los") =>
      typeof saved[key] === "boolean" ? saved[key] : DEFAULT_PLAYBACK_PRESET[key];
    const readToggle = (key: "projectileAlignment" | "crosshairAlignment" | "leftHandAlignment" | "allowPartial" | "threat360") =>
      saved[key] === "on" || saved[key] === "off"
        ? saved[key]
        : DEFAULT_PLAYBACK_PRESET[key];
    const cosmetics = readBoolean("cosmetics");
    const avatar = readBoolean("avatar");
    return {
      weapons: readBoolean("weapons") || cosmetics,
      cosmetics,
      steamIdentity: readBoolean("steamIdentity") || avatar,
      avatar,
      voice: readBoolean("voice"),
      playoff: readBoolean("playoff"),
      projectileAlignment: readToggle("projectileAlignment"),
      crosshairAlignment: readToggle("crosshairAlignment"),
      leftHandAlignment: readToggle("leftHandAlignment"),
      matchPresentation: saved.matchPresentation === "off" || saved.matchPresentation === "scoreboard"
        ? saved.matchPresentation
        : DEFAULT_PLAYBACK_PRESET.matchPresentation,
      allowPartial: readToggle("allowPartial"),
      handoffMode: ["off", "death", "contact", "death_or_contact", "death_contact_c4"].includes(saved.handoffMode ?? "")
        ? saved.handoffMode as PlaybackPresetOptions["handoffMode"]
        : DEFAULT_PLAYBACK_PRESET.handoffMode,
      handoffScope: saved.handoffScope === "all" ? "all" : "slot",
      threat360: readToggle("threat360"),
      threat360Range: typeof saved.threat360Range === "number"
        && Number.isFinite(saved.threat360Range)
        && saved.threat360Range >= 150
        && saved.threat360Range <= 800
        ? saved.threat360Range
        : DEFAULT_PLAYBACK_PRESET.threat360Range,
      threat360Los: readBoolean("threat360Los"),
      friendlyFire: saved.friendlyFire === "on" ? "on" : "off",
    };
  } catch {
    return { ...DEFAULT_PLAYBACK_PRESET };
  }
}

export function storedLocalEnvironment(): LocalEnvironmentSettings {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.local-environment.v1") ?? "null") as Partial<LocalEnvironmentSettings> | null;
    if (!saved || typeof saved !== "object") return { ...DEFAULT_LOCAL_ENVIRONMENT };
    return {
      cs2Path: typeof saved.cs2Path === "string" ? saved.cs2Path : "",
      demoRoots: Array.isArray(saved.demoRoots)
        ? uniqueLibraryRoots(saved.demoRoots.filter((root): root is string => typeof root === "string"))
        : [],
      soundNotifications: typeof saved.soundNotifications === "boolean" ? saved.soundNotifications : true,
    };
  } catch {
    return { ...DEFAULT_LOCAL_ENVIRONMENT };
  }
}

export function normalizedDiagnosticPath(path: string): string {
  return path.trim().replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
}

export function isDemoFilePath(path: string): boolean {
  const lowered = path.toLowerCase();
  return lowered.endsWith(".dem") || lowered.endsWith(".dem.zst");
}

export function commonParentDirectory(paths: string[]): string {
  const parents = paths.map((path) => {
    const normalized = path.trim().replace(/\//g, "\\").replace(/\\+$/, "");
    const separator = normalized.lastIndexOf("\\");
    return separator > 2 ? normalized.slice(0, separator) : normalized.slice(0, Math.max(0, separator + 1));
  });
  if (parents.length === 0) return "";
  const segments = parents.map((path) => path.split("\\").filter(Boolean));
  const common: string[] = [];
  const length = Math.min(...segments.map((parts) => parts.length));
  for (let index = 0; index < length; index += 1) {
    const value = segments[0][index];
    if (!segments.every((parts) => parts[index].toLocaleLowerCase() === value.toLocaleLowerCase())) break;
    common.push(value);
  }
  if (common.length === 0) return parents[0];
  const drive = common[0].endsWith(":");
  return `${common.join("\\")}${drive && common.length === 1 ? "\\" : ""}`;
}

export function parseCommandError(error: unknown): CommandErrorDto {
  if (error && typeof error === "object" && "code" in error && "message" in error) {
    const value = error as { code: unknown; message: unknown; path?: unknown };
    return {
      code: String(value.code),
      message: String(value.message),
      path: typeof value.path === "string" ? value.path : undefined,
    };
  }
  if (typeof error === "string") {
    try {
      return parseCommandError(JSON.parse(error));
    } catch {
      return { code: "unknown", message: error };
    }
  }
  if (error && typeof error === "object" && "message" in error) {
    return { code: "unknown", message: String(error.message) };
  }
  return { code: "unknown", message: String(error) };
}

export function userFacingErrorMessage(error: { code: string; message: string; path?: string | null }, language: Language): string {
  const code = error.code.toLocaleLowerCase();
  const words = TEXT[language];
  if (code.includes("cancel") || code.includes("stopping")) {
    return words.errorTaskStopped;
  }
  if (code.includes("playback_update_unavailable")) {
    return words.errorPlaybackUpdateUnavailable;
  }
  if (code.includes("playback_update_check")) {
    return words.errorPlaybackUpdateCheck;
  }
  if (code.includes("playback_update_download")) {
    return words.errorPlaybackUpdateDownload;
  }
  if (code.includes("playback_update_manifest")) {
    return words.errorPlaybackUpdateManifest;
  }
  if (code.includes("playback_update_hash") || code.includes("playback_update_signature") || code.includes("playback_signing_key")) {
    return words.errorPlaybackUpdateIntegrity;
  }
  if (code.includes("cs2_running")) {
    return words.errorCs2Running;
  }
  if (code === "playback_receipt_contract_mismatch") {
    return words.errorPlaybackDesktopMismatch;
  }
  if (code.includes("not_found") || code.includes("unavailable") || code.includes("missing")) {
    return words.errorFileNotFound;
  }
  if (code.includes("permission") || code.includes("denied") || code.includes("unsafe") || code.includes("write")) {
    return words.errorFolderNotWritable;
  }
  if (code.includes("invalid") || code.includes("unsupported") || code.includes("validation")) {
    return words.errorValidation;
  }
  if (code.includes("dialog")) {
    return words.errorDialog;
  }
  if (code.includes("copy")) {
    return words.errorCopy;
  }
  if (code.includes("inventory_simulator")) {
    return words.errorInventorySimulator;
  }
  if (code.includes("playback")) {
    return words.errorPlayback;
  }
  if (code.includes("analysis") || code.includes("demo") || code.includes("parse")) {
    return detailedAnalysisErrorMessage(words.errorAnalysis, error.message, language);
  }
  return words.errorGeneric.replace("{code}", error.code);
}

export function playbackUpdateFailureStatus(reason: unknown, language: Language): PlaybackUpdateStatus {
  const error = parseCommandError(reason);
  if (error.code.toLocaleLowerCase().includes("playback_update_unavailable")) {
    return { phase: "unavailable" };
  }
  return { phase: "error", error: userFacingErrorMessage(error, language) };
}

export function userFacingErrorTitle(error: { code: string }, language: Language): string {
  const code = error.code.toLocaleLowerCase();
  const words = TEXT[language];
  if (code.includes("analysis") || code.includes("demo") || code.includes("parse")) {
    return words.errorAnalysisTitle;
  }
  if (code.includes("playback")) return words.errorPlaybackTitle;
  if (code.includes("inventory_simulator")) return words.errorInventorySimulatorTitle;
  if (code.includes("permission") || code.includes("denied") || code.includes("write")) {
    return words.errorFolderNotWritableTitle;
  }
  if (code.includes("not_found") || code.includes("missing")) return words.errorFileNotFoundTitle;
  return words.errorTitle;
}

export function consentIsValid(phrase: string): boolean {
  return phrase.trim() === COSMETIC_PHRASE;
}

export function useElapsed(active: boolean): number {
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    if (!active) {
      setSeconds(0);
      return;
    }
    const started = Date.now();
    const timer = window.setInterval(() => setSeconds(Math.floor((Date.now() - started) / 1000)), 1000);
    return () => window.clearInterval(timer);
  }, [active]);
  return seconds;
}

export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const update = () => setMatches(media.matches);
    update();
    media.addEventListener("change", update);
    return () => media.removeEventListener("change", update);
  }, [query]);
  return matches;
}

export function mergeActivityLogs(current: AppLogEntry[], incoming: AppLogEntry[]): AppLogEntry[] {
  const merged = new Map(current.map((entry) => [entry.id, entry]));
  for (const entry of incoming) merged.set(entry.id, entry);
  return [...merged.values()]
    .sort((left, right) => left.timestampMs - right.timestampMs || left.id.localeCompare(right.id))
    .slice(-ACTIVITY_LOG_LIMIT);
}

export function loadCustomCssProfiles(): CustomCssProfile[] {
  const stored = normalizeCustomCssProfiles(localStorage.getItem(CUSTOM_CSS_PROFILES_STORAGE_KEY));
  const legacyCss = normalizeCustomCss(localStorage.getItem(CUSTOM_CSS_STORAGE_KEY));
  const profiles = stored.length > 0
    ? stored
    : legacyCss.trim()
      ? [{ id: "migrated-custom-css", name: "Custom CSS", css: legacyCss }]
      : [];
  return reconcileCustomCssProfiles(
    profiles,
    localStorage.getItem(CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY) === "1",
  );
}

export function reconcileCustomCssProfiles(
  profiles: readonly CustomCssProfile[],
  starterProfilesSeeded: boolean,
): CustomCssProfile[] {
  const normalizedProfiles = normalizeCustomCssProfiles(profiles);
  const starterById = new Map(STARTER_CUSTOM_CSS_PROFILES.map((profile) => [profile.id, profile]));
  const isLegacyStarter = (profile: CustomCssProfile, starter: CustomCssProfile) => (
    profile.css.includes(`/* DemoTracer · ${starter.name} */`)
    && !profile.css.includes("@media (prefers-color-scheme: dark)")
  );
  const refreshedProfiles = normalizedProfiles.map((profile) => {
    const starter = starterById.get(profile.id);
    if (!starter) return profile;
    return !starterProfilesSeeded || isLegacyStarter(profile, starter) ? starter : profile;
  });
  if (starterProfilesSeeded && refreshedProfiles.every((profile, index) => profile === normalizedProfiles[index])) {
    return normalizedProfiles;
  }
  const existingIds = new Set(refreshedProfiles.map((profile) => profile.id));
  const existingNames = new Set(refreshedProfiles.map((profile) => profile.name.toLocaleLowerCase()));
  return [
    ...refreshedProfiles,
    ...STARTER_CUSTOM_CSS_PROFILES.filter((profile) => (
      !existingIds.has(profile.id) && !existingNames.has(profile.name.toLocaleLowerCase())
    )),
  ];
}
