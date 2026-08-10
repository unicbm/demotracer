/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Channel, invoke } from "@tauri-apps/api/core";
import { getVersion } from "@tauri-apps/api/app";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { exit as exitApp, relaunch } from "@tauri-apps/plugin-process";
import { check, type Update } from "@tauri-apps/plugin-updater";
import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import packageMetadata from "../package.json";
import { AppChrome, AppSidebar } from "./components/AppChrome";
import { activeBatchItemCount, findRestorableBatch } from "./batchSession";
import { ArchiveWorkspace } from "./components/ArchiveWorkspace";
import type { InventorySimulatorItem } from "./inventorySimulator";
import {
  BATCH_SELECTION_LIMIT,
  BatchWorkspace,
  type BatchConcurrency,
  type BatchJobItem,
  type BatchJobPhase,
  type BatchRunState,
  type BatchScanCandidate,
} from "./components/BatchWorkspace";
import { DialogPrimitive } from "./components/Dialog";
import { ExportInspector } from "./components/ExportInspector";
import { FaqWorkspace } from "./components/FaqWorkspace";
import { LibraryWorkspace, type LibrarySort } from "./components/LibraryWorkspace";
import { LogsWorkspace } from "./components/LogsWorkspace";
import { InventorySimulatorPanel } from "./components/InventorySimulatorPanel";
import { releaseNotesForLanguage } from "./releaseNotes";
import { DEFAULT_PLAYBACK_ADVANCED_OPTIONS, type PlaybackPresetOptions } from "./components/PlaybackCommandBuilder";
import { playerSelectionKey } from "./components/PlayerRoster";
import { RoundWorkspace } from "./components/RoundWorkspace";
import { SettingsWorkspace } from "./components/SettingsWorkspace";
import { SingleTaskPanel } from "./components/SingleTaskPanel";
import {
  AnalysisFailedView,
  type CopyTarget,
  OpeningArchiveView,
  ResultView,
  ValidationFailedView,
} from "./components/TaskViews";
import { AlertIcon, ArrowIcon, CheckIcon, CloseIcon, CopyIcon, FolderIcon, RefreshIcon, ReplayIcon } from "./icons";
import { COSMETIC_PHRASE, TEXT } from "./i18n";
import {
  CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY,
  STARTER_CUSTOM_CSS_PROFILES,
} from "./customCssPresets";
import {
  ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY,
  applyCustomCss,
  applyThemeCustomization,
  CUSTOM_CSS_PROFILES_STORAGE_KEY,
  CUSTOM_CSS_STORAGE_KEY,
  LEGACY_APPEARANCE_STORAGE_KEYS,
  normalizeActiveCustomCssProfileId,
  normalizeCustomCss,
  normalizeCustomCssProfiles,
  normalizeSidebarCollapsed,
  normalizeTheme,
  normalizeThemeCustomization,
  normalizeUiFontSize,
  normalizeUiScale,
  recommendedUiScale,
  resolveTheme,
  SIDEBAR_COLLAPSED_STORAGE_KEY,
  stepUiFontSize,
  themeBackground,
  THEME_CUSTOMIZATION_STORAGE_KEY,
  THEME_STORAGE_KEY,
  UI_FONT_SIZE_DEFAULT,
  UI_FONT_SIZE_STORAGE_KEY,
  type CustomCssProfile,
  type ThemeCustomization,
} from "./appearance";
import {
  isReusableDemoArchive,
  librarySeriesForManifest,
  mergeLibraryScans,
  normalizeLibraryRoot,
  persistDemoSourceIndex,
  persistLibraryPreferences,
  rememberDemoSource,
  storedDemoSourceIndex,
  storedLibraryPreferences,
  uniqueLibraryRoots,
  withExportRoot,
} from "./library";
import {
  EMPTY_LIBRARY_WORKSPACE,
  LIBRARY_SESSION_STORAGE_KEY,
  libraryWorkspaceReducer,
  readStoredLibrarySession,
  writeStoredLibrarySession,
  type StoredLibrarySession,
} from "./librarySession";
import type {
  AnalysisResult,
  ActivityLogLevel,
  ActivityLogMaintenance,
  AppLogEntry,
  BatchEvent,
  BatchItem,
  BatchItemPhase,
  BatchLedger,
  BatchList,
  Cs2InstallCandidate,
  CommandErrorDto,
  ConversionProgressEvent,
  ConversionSummary,
  ConverterSettings,
  DemoLibraryEntry,
  DemoLibraryScan,
  DemoFolderScan,
  DemoSourcePreflight,
  EnvironmentDiagnosticReport,
  GuiUpdateStatus,
  GsiStatus,
  ImportArchivesResult,
  Language,
  LocalEnvironmentSettings,
  ManifestArchive,
  OutputPreflight,
  PlaybackInstallResult,
  PlaybackReleaseStatus,
  PlaybackUpdateRelease,
  PlaybackUpdateStatus,
  Phase,
  ProgressPhase,
  ProgressState,
  RefreshArchiveMetadataResult,
  RefreshLibraryMetadataResult,
  ResolveArchiveSourceResult,
  RoundInfo,
  SaveServerConfigResult,
  ServerConfigDocument,
  ServerConfigValidation,
  TaskEvent,
  TaskPhase,
  Theme,
} from "./types";

const DEFAULT_SETTINGS: ConverterSettings = {
  side: "both",
  fullRound: false,
  freezePrerollSeconds: 10,
  subtickMode: "auto",
  maxRoundSeconds: 240,
  exportVoice: true,
  exportCosmetics: false,
  exportStickers: false,
  exportCharms: false,
  includeSuspicious: false,
};

const INITIAL_LIBRARY_PREFERENCES = storedLibraryPreferences();

const DEFAULT_LOCAL_ENVIRONMENT: LocalEnvironmentSettings = {
  cs2Path: "",
  demoRoots: [],
  soundNotifications: true,
};

const BATCH_PREFERENCES_STORAGE_KEY = "demotracer.batch-preferences.v1";
const COSMETIC_CONSENT_STORAGE_KEY = "demotracer.cosmetic-consent.v1";
const LEGACY_UI_SCALE_STORAGE_KEY = "demotracer.ui-scale.v1";
const INVENTORY_SIMULATOR_PANEL_WIDTH_KEY = "demotracer.inventory-simulator-panel-width.v1";
const INVENTORY_SIMULATOR_PANEL_DEFAULT_WIDTH = 580;
const INVENTORY_SIMULATOR_PANEL_MIN_WIDTH = 440;
const INVENTORY_SIMULATOR_PANEL_MAX_WIDTH = 900;
const ACTIVITY_LOG_LIMIT = 5_000;

const INITIAL_LIBRARY_SESSION = readStoredLibrarySession(localStorage);

interface StoredBatchPreferences {
  folderPath: string;
  concurrency: BatchConcurrency;
}

interface BatchItemProgress {
  progress?: number | null;
  stage?: string | null;
  startedAtMs?: number;
  finishedAtMs?: number;
  written: number;
  estimated: number;
}

interface DemoPreflightProgress {
  current: number;
  total: number;
  fileName: string;
}

interface DuplicateDemoConflictState {
  primary: DemoSourcePreflight;
  batch?: {
    selections: DemoSourcePreflight[];
    replaceSourceIds: string[];
    mergedSegments: number;
    relinkedDuplicates: number;
  };
}

interface SaveArchiveNoteResult {
  manifestPath: string;
  note: string | null;
}

type ReparseTarget =
  | { kind: "archive"; archive: ManifestArchive }
  | { kind: "library"; entry: DemoLibraryEntry };

interface InventorySimulatorPanelBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

function measureInventorySimulatorPanel(element: HTMLElement): InventorySimulatorPanelBounds | null {
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

function normalizeInventorySimulatorPanelWidth(width: number): number {
  const viewportMaximum = Math.max(INVENTORY_SIMULATOR_PANEL_MIN_WIDTH, window.innerWidth - 430);
  return Math.min(
    INVENTORY_SIMULATOR_PANEL_MAX_WIDTH,
    viewportMaximum,
    Math.max(INVENTORY_SIMULATOR_PANEL_MIN_WIDTH, width),
  );
}

function storedInventorySimulatorPanelWidth(): number {
  const stored = Number(localStorage.getItem(INVENTORY_SIMULATOR_PANEL_WIDTH_KEY));
  return normalizeInventorySimulatorPanelWidth(Number.isFinite(stored) && stored > 0
    ? stored
    : INVENTORY_SIMULATOR_PANEL_DEFAULT_WIDTH);
}

function storedBatchPreferences(): StoredBatchPreferences {
  try {
    const saved = JSON.parse(localStorage.getItem(BATCH_PREFERENCES_STORAGE_KEY) ?? "null") as Partial<StoredBatchPreferences> | null;
    const concurrency = saved?.concurrency;
    return {
      folderPath: typeof saved?.folderPath === "string" ? saved.folderPath : "",
      concurrency: concurrency === "auto" || concurrency === 2 || concurrency === 4 || concurrency === 6 || concurrency === 8
        ? concurrency
        : "auto",
    };
  } catch {
    return { folderPath: "", concurrency: "auto" };
  }
}

function batchJobPhase(phase: BatchItemPhase): BatchJobPhase {
  if (phase === "complete") return "completed";
  if (phase === "voice") return "converting";
  return phase;
}

function batchRunState(status: BatchLedger["status"] | undefined, invocationActive: boolean): BatchRunState {
  if (invocationActive) {
    if (status === "stopping") return "stopping";
    return "running";
  }
  if (status === "completed" || status === "completedWithErrors") return "complete";
  if (status === "paused") return "interrupted";
  if (status === "running" || status === "stopping" || status === "pending") return "interrupted";
  return "idle";
}

function nextBatchItemProgress(current: BatchItemProgress | undefined, task: TaskEvent): BatchItemProgress {
  const next: BatchItemProgress = current ?? { written: 0, estimated: 0, startedAtMs: Date.now() };
  if (task.kind === "phase") {
    return { ...next, progress: task.phase === "complete" ? 1 : next.progress };
  }
  if (task.kind === "log") {
    return task.level === "info" ? next : { ...next, stage: task.message };
  }

  const event = task.progress;
  switch (event.event) {
    case "analysisStarted":
      return { ...next, progress: 0.02 };
    case "analysisFinished":
      return { ...next, progress: 0.05, written: 0, estimated: Math.max(1, event.estimatedFiles) };
    case "roundStarted":
      return { ...next, stage: `Round ${event.round}` };
    case "roundSkipped":
      return { ...next, stage: `Round ${event.round}: ${event.reason}` };
    case "playerSkipped":
      return { ...next, stage: `${event.steamId}: ${event.reason}` };
    case "playerWritten": {
      const written = next.written + 1;
      return {
        ...next,
        written,
        progress: Math.min(0.88, 0.05 + 0.83 * (written / Math.max(1, next.estimated))),
        stage: event.playerName,
      };
    }
    case "artifactsWritingStarted":
      return { ...next, progress: 0.9, written: 0, estimated: Math.max(1, event.artifacts), stage: undefined };
    case "artifactWritten": {
      const written = next.written + 1;
      return {
        ...next,
        written,
        progress: Math.min(0.99, 0.9 + 0.09 * (written / Math.max(1, next.estimated))),
        stage: fileName(event.path),
      };
    }
    case "finished":
      return { ...next, progress: 1, stage: fileName(event.manifestPath), finishedAtMs: Date.now() };
    default:
      return next;
  }
}

const DEFAULT_PLAYBACK_PRESET: PlaybackPresetOptions = {
  weapons: true,
  cosmetics: false,
  steamIdentity: true,
  avatar: false,
  voice: true,
  playoff: false,
  ...DEFAULT_PLAYBACK_ADVANCED_OPTIONS,
};

function emptyProgress(): ProgressState {
  return {
    phase: "preparing",
    message: "",
    written: 0,
    estimated: 0,
    unit: null,
    completedRounds: 0,
    selectedRounds: 0,
    log: [],
    warnings: [],
    announcement: "",
  };
}

function storedLanguage(): Language {
  const saved = localStorage.getItem("demotracer.language");
  if (saved === "zh" || saved === "en") return saved;
  return navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en";
}

function storedUiFontSize(): number {
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

function storedCosmeticConsent(): boolean {
  return localStorage.getItem(COSMETIC_CONSENT_STORAGE_KEY) === "accepted";
}

function storedSettings(): ConverterSettings {
  try {
    const saved = JSON.parse(localStorage.getItem("demotracer.settings") ?? "null") as Partial<ConverterSettings> | null;
    if (!saved || typeof saved !== "object" || Array.isArray(saved)) return { ...DEFAULT_SETTINGS };
    return {
      ...DEFAULT_SETTINGS,
      side: saved.side === "both" || saved.side === "t" || saved.side === "ct" ? saved.side : DEFAULT_SETTINGS.side,
      fullRound: typeof saved.fullRound === "boolean" ? saved.fullRound : DEFAULT_SETTINGS.fullRound,
      freezePrerollSeconds: typeof saved.freezePrerollSeconds === "number"
        && Number.isFinite(saved.freezePrerollSeconds)
        && saved.freezePrerollSeconds >= 0
        && saved.freezePrerollSeconds <= 120
        ? saved.freezePrerollSeconds
        : DEFAULT_SETTINGS.freezePrerollSeconds,
      subtickMode: saved.subtickMode === "auto" || saved.subtickMode === "off"
        ? saved.subtickMode
        : DEFAULT_SETTINGS.subtickMode,
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

function storedPlaybackPreset(): PlaybackPresetOptions {
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
    };
  } catch {
    return { ...DEFAULT_PLAYBACK_PRESET };
  }
}

function storedLocalEnvironment(): LocalEnvironmentSettings {
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

const ENVIRONMENT_REPORT_STORAGE_KEY = "demotracer.environment-report.v1";

interface StoredEnvironmentReport {
  cs2Path: string;
  report: EnvironmentDiagnosticReport;
}

function normalizedDiagnosticPath(path: string): string {
  return path.trim().replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
}

function isEnvironmentDiagnosticReport(value: unknown): value is EnvironmentDiagnosticReport {
  if (!value || typeof value !== "object") return false;
  const report = value as Partial<EnvironmentDiagnosticReport>;
  return Number.isFinite(report.checkedAtMs)
    && typeof report.requestedPath === "string"
    && typeof report.cs2Root === "string"
    && typeof report.gameCsgoPath === "string"
    && ["pass", "warning", "error", "unverified"].includes(String(report.overall))
    && Array.isArray(report.checks)
    && Array.isArray(report.plugins)
    && Array.isArray(report.conflicts)
    && Boolean(report.receipt && typeof report.receipt === "object");
}

function cachedEnvironmentReport(report: EnvironmentDiagnosticReport): EnvironmentDiagnosticReport {
  const runtimeConflictRules = new Set(["known_cosmetic_writer", "cs2_bot_improver_bot_randomizer"]);
  const checks = report.checks
    .filter((check) => check.group !== "runtime")
    .map((check) => check.id === "counterStrikeSharp.runtime" && check.status === "pass"
      ? {
          ...check,
          status: "unverified" as const,
          summary: "CounterStrikeSharp was installed at the last inspection; its loaded host version requires a fresh inspection.",
          actual: "cached; runtime version unknown",
        }
      : check);
  const conflicts = report.conflicts.map((conflict) => runtimeConflictRules.has(conflict.ruleId)
    ? {
        ...conflict,
        confidence: "medium" as const,
        summary: `${conflict.title} was present at the last inspection. Inspect again to verify whether it is currently loaded or overlaps DemoTracer runtime behavior.`,
      }
    : conflict);

  return {
    ...report,
    cached: true,
    overall: "unverified",
    runtimeVerification: "unknown",
    checks,
    plugins: report.plugins.map((plugin) => ({ ...plugin, runtimeState: "unknown" })),
    conflicts,
  };
}

function storedEnvironmentReport(expectedCs2Path: string): EnvironmentDiagnosticReport | null {
  const expectedPath = normalizedDiagnosticPath(expectedCs2Path);
  if (!expectedPath) return null;
  try {
    const saved = JSON.parse(localStorage.getItem(ENVIRONMENT_REPORT_STORAGE_KEY) ?? "null") as Partial<StoredEnvironmentReport> | null;
    if (!saved || typeof saved !== "object" || typeof saved.cs2Path !== "string") return null;
    if (normalizedDiagnosticPath(saved.cs2Path) !== expectedPath || !isEnvironmentDiagnosticReport(saved.report)) return null;
    return cachedEnvironmentReport(saved.report);
  } catch {
    return null;
  }
}

function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path;
}

function isDemoFilePath(path: string): boolean {
  const lowered = path.toLowerCase();
  return lowered.endsWith(".dem") || lowered.endsWith(".dem.zst");
}

function commonParentDirectory(paths: string[]): string {
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

function formatBytes(value: number | string): string {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const power = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** power).toFixed(power === 0 ? 0 : 1)} ${units[power]}`;
}

function parseCommandError(error: unknown): CommandErrorDto {
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

function userFacingErrorMessage(error: { code: string; message: string; path?: string | null }, language: Language): string {
  const code = error.code.toLocaleLowerCase();
  const zh = language === "zh";
  if (code.includes("cancel") || code.includes("stopping")) {
    return zh ? "任务已停止。已完成的输出会保留。" : "Task stopped. Completed output is kept.";
  }
  if (code.includes("playback_update_unavailable")) {
    return zh ? "稳定通道暂未发布回放组件更新。" : "The stable channel has not published a playback component update yet.";
  }
  if (code.includes("playback_update_check")) {
    return zh ? "暂时无法连接回放组件更新服务。" : "The playback update service is temporarily unavailable.";
  }
  if (code.includes("playback_update_download")) {
    return zh ? "回放组件下载失败，请稍后重试。" : "The playback component download failed. Try again later.";
  }
  if (code.includes("playback_update_manifest")) {
    return zh ? "回放组件更新清单未通过安全检查。" : "The playback update manifest did not pass security validation.";
  }
  if (code.includes("playback_update_hash") || code.includes("playback_update_signature") || code.includes("playback_signing_key")) {
    return zh ? "下载的回放组件未通过完整性或签名验证，未执行安装。" : "The downloaded playback component failed integrity or signature verification and was not installed.";
  }
  if (code.includes("cs2_running")) {
    return zh ? "请先关闭 CS2，再安装或回滚回放组件。" : "Close CS2 before installing or rolling back playback components.";
  }
  if (code.includes("not_found") || code.includes("unavailable") || code.includes("missing")) {
    return zh ? "找不到所选文件，请重新选择。" : "The selected file was not found. Choose it again.";
  }
  if (code.includes("permission") || code.includes("denied") || code.includes("unsafe") || code.includes("write")) {
    return zh ? "无法写入所选目录，请选择其他目录。" : "The selected folder is not writable. Choose another folder.";
  }
  if (code.includes("invalid") || code.includes("unsupported") || code.includes("validation")) {
    return zh ? "文件或设置未通过检查。" : "The file or settings did not pass validation.";
  }
  if (code.includes("dialog")) {
    return zh ? "系统选择窗口暂时无法打开，请稍后再试。" : "The system picker could not be opened. Try again in a moment.";
  }
  if (code.includes("copy")) {
    return zh ? "没有复制成功，请再试一次。" : "The content was not copied. Try again.";
  }
  if (code.includes("inventory_simulator")) {
    return zh ? "无法打开饰品预览。" : "The inventory preview could not be opened.";
  }
  if (code.includes("playback")) {
    return zh ? "回放组件操作失败，请检查 CS2 路径。" : "Playback component operation failed. Check the CS2 path.";
  }
  if (code.includes("analysis") || code.includes("demo") || code.includes("parse")) {
    return zh ? "无法分析此 Demo。请重试或选择其他文件。" : "This demo could not be analyzed. Retry or choose another file.";
  }
  return zh
    ? `操作失败（${error.code}）。请检查当前选择后重试。`
    : `The operation failed (${error.code}). Check the current selection and try again.`;
}

function playbackUpdateFailureStatus(reason: unknown, language: Language): PlaybackUpdateStatus {
  const error = parseCommandError(reason);
  if (error.code.toLocaleLowerCase().includes("playback_update_unavailable")) {
    return { phase: "unavailable" };
  }
  return { phase: "error", error: userFacingErrorMessage(error, language) };
}

function userFacingErrorTitle(error: { code: string }, language: Language): string {
  const code = error.code.toLocaleLowerCase();
  const zh = language === "zh";
  if (code.includes("analysis") || code.includes("demo") || code.includes("parse")) {
    return zh ? "Demo 分析失败" : "Demo analysis failed";
  }
  if (code.includes("playback")) return zh ? "回放组件操作失败" : "Playback operation failed";
  if (code.includes("inventory_simulator")) return zh ? "饰品预览无法打开" : "Inventory preview unavailable";
  if (code.includes("permission") || code.includes("denied") || code.includes("write")) {
    return zh ? "目录不可写" : "Folder is not writable";
  }
  if (code.includes("not_found") || code.includes("missing")) return zh ? "文件不存在" : "File not found";
  return zh ? "操作未完成" : "The operation did not finish";
}

function phaseFromBackend(phase: TaskPhase, current: ProgressPhase): ProgressPhase {
  if (phase === "decompressing") return "decompressing";
  if (phase === "parsing") return "parsing";
  if (phase === "analyzing") return "analyzing";
  if (phase === "voice") return "voice";
  if (phase === "validating") return "validating";
  if (phase === "complete") return "complete";
  return current;
}

function consentIsValid(phrase: string): boolean {
  return phrase.trim() === COSMETIC_PHRASE;
}

function useElapsed(active: boolean): number {
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

function useMediaQuery(query: string): boolean {
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

function mergeActivityLogs(current: AppLogEntry[], incoming: AppLogEntry[]): AppLogEntry[] {
  const merged = new Map(current.map((entry) => [entry.id, entry]));
  for (const entry of incoming) merged.set(entry.id, entry);
  return [...merged.values()]
    .sort((left, right) => left.timestampMs - right.timestampMs || left.id.localeCompare(right.id))
    .slice(-ACTIVITY_LOG_LIMIT);
}

function loadCustomCssProfiles(): CustomCssProfile[] {
  const stored = normalizeCustomCssProfiles(localStorage.getItem(CUSTOM_CSS_PROFILES_STORAGE_KEY));
  const legacyCss = normalizeCustomCss(localStorage.getItem(CUSTOM_CSS_STORAGE_KEY));
  const profiles = stored.length > 0
    ? stored
    : legacyCss.trim()
      ? [{ id: "migrated-custom-css", name: "Custom CSS", css: legacyCss }]
      : [];
  const starterById = new Map(STARTER_CUSTOM_CSS_PROFILES.map((profile) => [profile.id, profile]));
  const starterProfilesSeeded = localStorage.getItem(CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY) === "1";
  const isLegacyStarter = (profile: CustomCssProfile, starter: CustomCssProfile) => (
    profile.css.includes(`/* DemoTracer · ${starter.name} */`)
    && !profile.css.includes("@media (prefers-color-scheme: dark)")
  );
  const refreshedProfiles = profiles.map((profile) => {
    const starter = starterById.get(profile.id);
    if (!starter) return profile;
    return !starterProfilesSeeded || isLegacyStarter(profile, starter) ? starter : profile;
  });
  if (starterProfilesSeeded && refreshedProfiles.every((profile, index) => profile === profiles[index])) {
    return profiles;
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

function App() {
  const [language, setLanguage] = useState<Language>(storedLanguage);
  const [theme, setTheme] = useState<Theme>(() => normalizeTheme(localStorage.getItem(THEME_STORAGE_KEY)));
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => (
    normalizeSidebarCollapsed(localStorage.getItem(SIDEBAR_COLLAPSED_STORAGE_KEY))
  ));
  const [uiFontSize, setUiFontSize] = useState(storedUiFontSize);
  const [themeCustomization, setThemeCustomization] = useState<ThemeCustomization>(() => (
    normalizeThemeCustomization(localStorage.getItem(THEME_CUSTOMIZATION_STORAGE_KEY))
  ));
  const [customCssProfiles, setCustomCssProfiles] = useState<CustomCssProfile[]>(loadCustomCssProfiles);
  const [activeCustomCssProfileId, setActiveCustomCssProfileId] = useState<string | null>(() => {
    const storedActive = normalizeActiveCustomCssProfileId(
      localStorage.getItem(ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY),
      customCssProfiles,
    );
    if (storedActive) return storedActive;
    const legacyCss = normalizeCustomCss(localStorage.getItem(CUSTOM_CSS_STORAGE_KEY));
    return customCssProfiles.find((profile) => legacyCss && profile.css === legacyCss)?.id ?? null;
  });
  const [phase, setPhase] = useState<Phase>("idle");
  const [singleTask, setSingleTask] = useState<"analysis" | "conversion" | null>(null);
  const [singleTaskPanelOpen, setSingleTaskPanelOpen] = useState(false);
  const [activeTaskSourcePath, setActiveTaskSourcePath] = useState("");
  const [libraryWorkspace, dispatchLibraryWorkspace] = useReducer(
    libraryWorkspaceReducer,
    EMPTY_LIBRARY_WORKSPACE,
  );
  const {
    activeSection,
    archive,
    archivePath,
    selectedRound: selectedArchiveRound,
    selectedPlayer,
    commandMode,
  } = libraryWorkspace;
  const [sourcePath, setSourcePath] = useState("");
  const [outputDir, setOutputDir] = useState(INITIAL_LIBRARY_PREFERENCES.exportRoot);
  const [libraryPreferences, setLibraryPreferences] = useState(INITIAL_LIBRARY_PREFERENCES);
  const [demoSourceIndex, setDemoSourceIndex] = useState(storedDemoSourceIndex);
  const [libraryScan, setLibraryScan] = useState<DemoLibraryScan | null>(null);
  const [libraryLoading, setLibraryLoading] = useState(false);
  const [repairingManifest, setRepairingManifest] = useState("");
  const [repairingLibrary, setRepairingLibrary] = useState(false);
  const [importingArchives, setImportingArchives] = useState(false);
  const [deletingManifest, setDeletingManifest] = useState("");
  const [archiveDeleteTarget, setArchiveDeleteTarget] = useState<DemoLibraryEntry | null>(null);
  const [reparseTarget, setReparseTarget] = useState<ReparseTarget | null>(null);
  const [analysisCancelPending, setAnalysisCancelPending] = useState(false);
  const [libraryNotice, setLibraryNotice] = useState("");
  const [libraryQuery, setLibraryQuery] = useState("");
  const [libraryMap, setLibraryMap] = useState("");
  const [libraryPlatform, setLibraryPlatform] = useState("");
  const [librarySort, setLibrarySort] = useState<LibrarySort>("recent");
  const [savingArchiveNote, setSavingArchiveNote] = useState(false);
  const [batchFolderPath, setBatchFolderPath] = useState(() => storedBatchPreferences().folderPath);
  const [batchScanError, setBatchScanError] = useState("");
  const [batchScan, setBatchScan] = useState<DemoFolderScan | null>(null);
  const [batchSelectedIds, setBatchSelectedIds] = useState<string[]>([]);
  const [batchReplaceSourceIds, setBatchReplaceSourceIds] = useState<string[]>([]);
  const [batchConcurrency, setBatchConcurrency] = useState<BatchConcurrency>(() => storedBatchPreferences().concurrency);
  const [batchLedger, setBatchLedger] = useState<BatchLedger | null>(null);
  const [batchProgressByItem, setBatchProgressByItem] = useState<Record<string, BatchItemProgress>>({});
  const [batchInvocationActive, setBatchInvocationActive] = useState(false);
  const [batchStopPending, setBatchStopPending] = useState(false);
  const [batchStartingCandidates, setBatchStartingCandidates] = useState<BatchScanCandidate[]>([]);
  const [batchClock, setBatchClock] = useState(() => Date.now());
  const [outputRoot, setOutputRoot] = useState("");
  const [analysis, setAnalysis] = useState<AnalysisResult | null>(null);
  const [selectedRounds, setSelectedRounds] = useState<Set<number>>(new Set());
  const [settings, setSettings] = useState<ConverterSettings>(storedSettings);
  const [cosmeticConsentAccepted, setCosmeticConsentAccepted] = useState(storedCosmeticConsent);
  const [playbackPreset, setPlaybackPreset] = useState<PlaybackPresetOptions>(storedPlaybackPreset);
  const [localEnvironment, setLocalEnvironment] = useState<LocalEnvironmentSettings>(storedLocalEnvironment);
  const [installCandidates, setInstallCandidates] = useState<Cs2InstallCandidate[]>([]);
  const [installDetectionCompleted, setInstallDetectionCompleted] = useState(false);
  const [environmentReport, setEnvironmentReport] = useState<EnvironmentDiagnosticReport | null>(
    () => storedEnvironmentReport(storedLocalEnvironment().cs2Path),
  );
  const [detectingInstallations, setDetectingInstallations] = useState(false);
  const [inspectingEnvironment, setInspectingEnvironment] = useState(false);
  const [appVersion, setAppVersion] = useState(packageMetadata.version);
  const [guiUpdate, setGuiUpdate] = useState<GuiUpdateStatus>({
    phase: "idle",
    currentVersion: packageMetadata.version,
  });
  const [guiUpdateDialogOpen, setGuiUpdateDialogOpen] = useState(false);
  const [playbackRelease, setPlaybackRelease] = useState<PlaybackReleaseStatus | null>(null);
  const [playbackUpdate, setPlaybackUpdate] = useState<PlaybackUpdateStatus>({ phase: "idle" });
  const [playbackReleaseError, setPlaybackReleaseError] = useState("");
  const [releaseAction, setReleaseAction] = useState<"installingOnline" | "installingFile" | "rollingBack" | null>(null);
  const [releaseNotice, setReleaseNotice] = useState("");
  const [serverConfigDocument, setServerConfigDocument] = useState<ServerConfigDocument | null>(null);
  const [serverConfigDraft, setServerConfigDraft] = useState("");
  const [serverConfigValidation, setServerConfigValidation] = useState<ServerConfigValidation | null>(null);
  const [loadingServerConfig, setLoadingServerConfig] = useState(false);
  const [savingServerConfig, setSavingServerConfig] = useState(false);
  const [activityLogs, setActivityLogs] = useState<AppLogEntry[]>([]);
  const [activityLogsLoading, setActivityLogsLoading] = useState(false);
  const [gsiRuntimeStatus, setGsiRuntimeStatus] = useState<GsiStatus | null>(null);
  const [progress, setProgress] = useState<ProgressState>(emptyProgress);
  const [result, setResult] = useState<ConversionSummary | null>(null);
  const [conversionWarnings, setConversionWarnings] = useState<string[]>([]);
  const [analysisError, setAnalysisError] = useState("");
  const [validationError, setValidationError] = useState("");
  const [globalError, setGlobalError] = useState<CommandErrorDto | null>(null);
  const [overwriteConflict, setOverwriteConflict] = useState<OutputPreflight | null>(null);
  const [conversionStartPending, setConversionStartPending] = useState(false);
  const [demoPreflightActive, setDemoPreflightActive] = useState(false);
  const [demoPreflightProgress, setDemoPreflightProgress] = useState<DemoPreflightProgress | null>(null);
  const [duplicateDemoConflict, setDuplicateDemoConflict] = useState<DuplicateDemoConflictState | null>(null);
  const [cosmeticOpen, setCosmeticOpen] = useState(false);
  const [closeOpen, setCloseOpen] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [cosmeticPhrase, setCosmeticPhrase] = useState("");
  const [copiedTarget, setCopiedTarget] = useState<CopyTarget | null>(null);
  const [liveMessage, setLiveMessage] = useState("");
  const [inventorySimulatorPanelAvailable, setInventorySimulatorPanelAvailable] = useState(false);
  const [inventorySimulatorPanelOpen, setInventorySimulatorPanelOpen] = useState(false);
  const [inventorySimulatorPanelResizing, setInventorySimulatorPanelResizing] = useState(false);
  const [inventorySimulatorPanelWidth, setInventorySimulatorPanelWidth] = useState(storedInventorySimulatorPanelWidth);

  const taskTokenRef = useRef(0);
  const manifestReadTokenRef = useRef(0);
  const libraryRestoreRef = useRef<StoredLibrarySession | null>(INITIAL_LIBRARY_SESSION);
  const libraryRestoreStartedRef = useRef(false);
  const manifestCacheRef = useRef(new Map<string, ManifestArchive>());
  const manifestCacheGenerationRef = useRef(0);
  const libraryScanTokenRef = useRef(0);
  const taskWarningsRef = useRef<string[]>([]);
  const isBusyRef = useRef(false);
  const conversionStartLockRef = useRef(false);
  const analyzedMaxRoundSecondsRef = useRef(DEFAULT_SETTINGS.maxRoundSeconds);
  const environmentInspectionTokenRef = useRef(0);
  const pendingGuiUpdateRef = useRef<Update | null>(null);
  const batchIdRef = useRef("");
  const batchGenerationRef = useRef(0);
  const batchStopPendingRef = useRef(false);
  const batchCancelGenerationRef = useRef(-1);
  const taskSoundContextRef = useRef<AudioContext | null>(null);
  const startupServerConfigPathRef = useRef(localEnvironment.cs2Path.trim());
  const soundNotificationsRef = useRef(localEnvironment.soundNotifications);
  const retryButtonRef = useRef<HTMLButtonElement | null>(null);
  const resultHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const cosmeticInputRef = useRef<HTMLInputElement | null>(null);
  const chooseOtherOutputRef = useRef<HTMLButtonElement | null>(null);
  const openExistingArchiveRef = useRef<HTMLButtonElement | null>(null);
  const keepWorkingRef = useRef<HTMLButtonElement | null>(null);
  const guiUpdateLaterRef = useRef<HTMLButtonElement | null>(null);
  const cancelArchiveDeleteRef = useRef<HTMLButtonElement | null>(null);
  const inventorySimulatorHostRef = useRef<HTMLDivElement | null>(null);
  const browserLogPreviewSeededRef = useRef(false);

  const invalidateManifestCache = useCallback((path?: string) => {
    manifestCacheGenerationRef.current += 1;
    if (path) manifestCacheRef.current.delete(normalizedDiagnosticPath(path));
    else manifestCacheRef.current.clear();
  }, []);

  const words = TEXT[language];
  const recordActivityLog = useCallback((
    level: ActivityLogLevel,
    source: string,
    message: string,
  ) => {
    if (!message.trim()) return;
    if (!("__TAURI_INTERNALS__" in window)) {
      const timestampMs = Date.now();
      setActivityLogs((current) => mergeActivityLogs(current, [{
        id: `${timestampMs}-${Math.random().toString(16).slice(2)}`,
        timestampMs,
        level,
        source,
        message,
      }]));
      return;
    }
    void invoke<AppLogEntry>("append_activity_log", {
      request: { level, source, message },
    }).then((entry) => {
      setActivityLogs((current) => mergeActivityLogs(current, [entry]));
    }).catch(() => undefined);
  }, []);
  const refreshActivityLogs = useCallback(async () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    setActivityLogsLoading(true);
    try {
      const [entries, status] = await Promise.all([
        invoke<AppLogEntry[]>("list_activity_logs", { limit: ACTIVITY_LOG_LIMIT }),
        invoke<GsiStatus>("gsi_status"),
      ]);
      setActivityLogs(entries);
      setGsiRuntimeStatus(status);
    } finally {
      setActivityLogsLoading(false);
    }
  }, []);
  const openActivityLogDirectory = useCallback(() => {
    if ("__TAURI_INTERNALS__" in window) {
      void invoke<void>("open_activity_log_directory").catch((reason) => {
        setGlobalError(parseCommandError(reason));
      });
    }
  }, []);
  const clearActivityLogs = useCallback(() => {
    if (!window.confirm(TEXT[language].logsClearConfirm)) return;
    if (!("__TAURI_INTERNALS__" in window)) {
      setActivityLogs([]);
      return;
    }
    setActivityLogsLoading(true);
    void invoke<number>("clear_activity_logs").then(() => {
      setActivityLogs([]);
    }).catch((reason) => {
      setGlobalError(parseCommandError(reason));
    }).finally(() => setActivityLogsLoading(false));
  }, [language]);
  const libraryRoot = libraryPreferences.exportRoot;
  const libraryRoots = libraryPreferences.roots;
  const numberFormat = useMemo(() => new Intl.NumberFormat(language === "zh" ? "zh-CN" : "en-US"), [language]);
  const isRepairing = repairingLibrary || Boolean(repairingManifest);
  const isMaintainingLibrary = isRepairing || importingArchives || Boolean(deletingManifest);
  const isBusy = singleTask !== null || phase === "openingArchive" || isMaintainingLibrary || batchInvocationActive || demoPreflightActive || conversionStartPending;
  isBusyRef.current = isBusy;
  const systemDark = useMediaQuery("(prefers-color-scheme: dark)");
  const resolvedTheme = resolveTheme(theme, systemDark);
  const inspectorVisible = selectedPlayer === null
    && analysis !== null
    && (phase === "selecting" || singleTask === "conversion");
  const elapsedSeconds = useElapsed(singleTask === "analysis");
  const sourceFileName = analysis?.fileName || fileName(sourcePath);
  const analysisSessionTitle = activeSection === "analysis" && phase === "archive" && archive
    ? archive.displayName || fileName(archive.demoPath) || archive.demoId
    : activeSection === "analysis" ? sourceFileName : "";
  const analysisSessionMeta = activeSection === "analysis" && phase === "archive" && archive
    ? [fileName(archive.sourcePath || archive.demoPath), archive.map, `${archive.rounds.length} ${words.rounds}`].filter(Boolean).join(" · ")
    : activeSection === "analysis" && analysis
      ? [analysis.map || "—", `${analysis.rounds.length} ${words.rounds}`].join(" · ")
      : "";
  // The title bar is contextual chrome, not a second page heading. Pages with
  // their own visible heading (library, import, and FAQ) deliberately leave it
  // empty; analysis keeps match context and the heading-less utility pages keep
  // a compact label.
  const sessionTitle = activeSection === "analysis"
    ? analysisSessionTitle || words.navAnalysis
    : activeSection === "logs"
      ? words.navLogs
      : activeSection === "settings"
        ? words.navSettings
        : "";
  const sessionMeta = activeSection === "analysis" ? analysisSessionMeta : "";
  const analysisAvailable = phase !== "idle" || archive !== null || analysis !== null || result !== null;
  soundNotificationsRef.current = localEnvironment.soundNotifications;
  const importedBatchSources = useMemo(() => new Set(
    (libraryScan?.entries ?? [])
      .map((entry) => entry.sourcePath)
      .filter((path): path is string => Boolean(path))
      .map(normalizedDiagnosticPath),
  ), [libraryScan]);
  const batchReplaceSources = useMemo(() => new Set(batchReplaceSourceIds), [batchReplaceSourceIds]);
  const activeArchiveSeries = useMemo(
    () => librarySeriesForManifest(libraryScan?.entries ?? [], archivePath),
    [archivePath, libraryScan],
  );
  const batchCandidates = useMemo<BatchScanCandidate[]>(() => {
    return (batchScan?.candidates ?? []).map((candidate) => {
      const sourceId = normalizedDiagnosticPath(candidate.path);
      const replacing = batchReplaceSources.has(sourceId);
      const imported = importedBatchSources.has(sourceId) && !replacing;
      return {
        id: normalizedDiagnosticPath(candidate.path),
        path: candidate.path,
        fileName: candidate.fileName,
        sizeBytes: candidate.sizeBytes,
        compressed: candidate.compressed,
        modifiedAtMs: candidate.modifiedAtMs,
        status: imported ? "imported" : "ready",
        reason: replacing
          ? (language === "zh" ? "将重新解析并替换这个已有档案。" : "This existing archive will be re-parsed and replaced.")
          : imported
            ? (language === "zh" ? "这个源 Demo 已经有本地档案。" : "This source demo already has a local archive.")
            : null,
      };
    });
  }, [batchReplaceSources, batchScan, importedBatchSources, language]);
  const batchJobs = useMemo<BatchJobItem[]>(() => {
    if (!batchLedger) {
      return batchStartingCandidates.map((candidate) => ({
        id: candidate.id,
        candidateId: candidate.id,
        path: candidate.path,
        fileName: candidate.fileName,
        phase: "queued",
        progress: null,
      }));
    }
    return batchLedger.items.map((item: BatchItem) => {
      const transient = batchProgressByItem[item.itemId];
      const phase = item.status === "completed"
        ? "completed"
        : item.status === "failed"
          ? "failed"
          : batchJobPhase(item.phase);
      const finishedAt = transient?.finishedAtMs ?? (phase === "completed" || phase === "failed" ? batchLedger.updatedAtMs : undefined);
      const elapsed = transient?.startedAtMs
        ? Math.max(0, Math.floor(((finishedAt ?? batchClock) - transient.startedAtMs) / 1000))
        : null;
      return {
        id: item.itemId,
        candidateId: normalizedDiagnosticPath(item.sourcePath),
        path: item.sourcePath,
        fileName: item.fileName,
        phase,
        progress: phase === "completed" ? 1 : transient?.progress ?? null,
        elapsedSeconds: elapsed,
        error: item.error ? userFacingErrorMessage(item.error, language) : null,
        outputPath: item.manifestPath ?? null,
      };
    });
  }, [batchClock, batchLedger, batchProgressByItem, batchStartingCandidates, language]);
  const batchSummary = useMemo(() => {
    const items = batchLedger?.items;
    if (!items) return { total: batchStartingCandidates.length, completed: 0, failed: 0, skipped: 0 };
    return {
      total: items.length,
      completed: items.filter((item) => item.status === "completed").length,
      failed: items.filter((item) => item.status === "failed").length,
      skipped: 0,
    };
  }, [batchLedger, batchStartingCandidates.length]);
  const currentBatchRunState = batchStopPending && batchInvocationActive
    ? "stopping"
    : batchRunState(batchLedger?.status, batchInvocationActive);
  const canResumeBatch = !batchInvocationActive && Boolean(batchLedger?.items.some((item) =>
    item.status === "pending" || item.status === "running"));
  const hasRetryableBatchJobs = !batchInvocationActive && Boolean(batchLedger?.items.some((item) =>
    item.status === "failed"));
  const batchActiveCount = batchLedger
    ? activeBatchItemCount(batchLedger)
    : batchStartingCandidates.length;
  const batchCosmeticOptionsLocked = batchInvocationActive || canResumeBatch;
  const batchCosmeticSettings = batchLedger && batchCosmeticOptionsLocked
    ? batchLedger.settings
    : settings;
  const primeTaskSound = useCallback((force = false) => {
    if (!force && !soundNotificationsRef.current) return;
    try {
      const context = taskSoundContextRef.current?.state === "closed"
        ? new AudioContext()
        : taskSoundContextRef.current ?? new AudioContext();
      taskSoundContextRef.current = context;
      if (context.state === "suspended") void context.resume().catch(() => undefined);
    } catch {
      // Sound is feedback only. An unavailable audio device must never block a task.
    }
  }, []);

  const playTaskSound = useCallback((kind: "success" | "failure" | "stopped") => {
    if (!soundNotificationsRef.current) return;
    try {
      const context = taskSoundContextRef.current?.state === "closed"
        ? new AudioContext()
        : taskSoundContextRef.current ?? new AudioContext();
      taskSoundContextRef.current = context;
      const schedule = () => {
        try {
          const now = context.currentTime;
          const notes = kind === "success"
            ? [{ frequency: 660, offset: 0 }, { frequency: 880, offset: 0.14 }]
            : kind === "stopped"
              ? [{ frequency: 520, offset: 0 }, { frequency: 390, offset: 0.16 }]
              : [{ frequency: 330, offset: 0 }, { frequency: 247, offset: 0.18 }];
          for (const note of notes) {
            const oscillator = context.createOscillator();
            const gain = context.createGain();
            const start = now + note.offset;
            oscillator.type = "sine";
            oscillator.frequency.setValueAtTime(note.frequency, start);
            gain.gain.setValueAtTime(0.0001, start);
            gain.gain.exponentialRampToValueAtTime(0.075, start + 0.018);
            gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.14);
            oscillator.connect(gain);
            gain.connect(context.destination);
            oscillator.start(start);
            oscillator.stop(start + 0.15);
          }
        } catch {
          // Notification audio is deliberately best effort.
        }
      };
      if (context.state === "suspended") void context.resume().then(schedule).catch(() => undefined);
      else schedule();
    } catch {
      // Notification audio is deliberately best effort.
    }
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.dataset.colorMode = resolvedTheme;
    document.documentElement.lang = language === "zh" ? "zh-CN" : "en";
    const nativeBackground = themeBackground(resolvedTheme);
    document.documentElement.style.backgroundColor = nativeBackground;
    document.body.style.backgroundColor = nativeBackground;
    document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')?.setAttribute("content", nativeBackground);
    localStorage.setItem(THEME_STORAGE_KEY, theme);
    localStorage.setItem("demotracer.language", language);
    if ("__TAURI_INTERNALS__" in window) {
      void Promise.all([
        getCurrentWindow().setTheme(theme === "system" ? null : theme),
        getCurrentWindow().setBackgroundColor(nativeBackground),
        getCurrentWebview().setBackgroundColor(nativeBackground),
      ]).catch(() => undefined);
    }
  }, [language, resolvedTheme, theme]);

  useEffect(() => {
    for (const key of LEGACY_APPEARANCE_STORAGE_KEYS) localStorage.removeItem(key);
  }, []);

  useEffect(() => {
    localStorage.setItem(SIDEBAR_COLLAPSED_STORAGE_KEY, String(sidebarCollapsed));
  }, [sidebarCollapsed]);

  useEffect(() => {
    const normalized = normalizeUiFontSize(uiFontSize);
    localStorage.setItem(UI_FONT_SIZE_STORAGE_KEY, String(normalized));
    localStorage.removeItem(LEGACY_UI_SCALE_STORAGE_KEY);
    document.documentElement.style.zoom = "";
    document.documentElement.style.setProperty("--ui-font-size", `${normalized}px`);
    if ("__TAURI_INTERNALS__" in window) void getCurrentWebview().setZoom(1).catch(() => undefined);
  }, [uiFontSize]);

  useEffect(() => {
    const normalized = normalizeThemeCustomization(themeCustomization);
    applyThemeCustomization(normalized);
    if (Object.keys(normalized).length > 0) {
      localStorage.setItem(THEME_CUSTOMIZATION_STORAGE_KEY, JSON.stringify(normalized));
    } else {
      localStorage.removeItem(THEME_CUSTOMIZATION_STORAGE_KEY);
    }
  }, [themeCustomization]);

  useEffect(() => {
    const normalizedProfiles = normalizeCustomCssProfiles(customCssProfiles);
    if (STARTER_CUSTOM_CSS_PROFILES.every((starter) => (
      normalizedProfiles.some((profile) => profile.id === starter.id && profile.css === starter.css)
    ))) {
      localStorage.setItem(CUSTOM_CSS_STARTER_PROFILES_STORAGE_KEY, "1");
    }
    if (normalizedProfiles.length > 0) {
      localStorage.setItem(CUSTOM_CSS_PROFILES_STORAGE_KEY, JSON.stringify(normalizedProfiles));
    } else {
      localStorage.removeItem(CUSTOM_CSS_PROFILES_STORAGE_KEY);
    }
    const normalizedActiveId = normalizeActiveCustomCssProfileId(activeCustomCssProfileId, normalizedProfiles);
    if (normalizedActiveId) localStorage.setItem(ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY, normalizedActiveId);
    else localStorage.removeItem(ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY);
    const activeCss = normalizedProfiles.find((profile) => profile.id === normalizedActiveId)?.css ?? "";
    applyCustomCss(activeCss);
    if (activeCss) localStorage.setItem(CUSTOM_CSS_STORAGE_KEY, activeCss);
    else localStorage.removeItem(CUSTOM_CSS_STORAGE_KEY);
  }, [activeCustomCssProfileId, customCssProfiles]);

  useEffect(() => {
    if (!inventorySimulatorPanelAvailable) return;
    if (!inventorySimulatorPanelOpen || inventorySimulatorPanelResizing) {
      void invoke("set_inventory_simulator_panel", {
        request: { visible: false },
      }).catch(() => undefined);
      return;
    }

    const host = inventorySimulatorHostRef.current;
    if (!host) return;
    let frame = 0;
    let disposed = false;
    const updateBounds = () => {
      frame = 0;
      if (disposed) return;
      const bounds = measureInventorySimulatorPanel(host);
      if (!bounds) return;
      void invoke("set_inventory_simulator_panel", {
        request: { visible: true, bounds },
      }).catch(() => undefined);
    };
    const scheduleBoundsUpdate = () => {
      if (frame === 0) frame = window.requestAnimationFrame(updateBounds);
    };
    const observer = new ResizeObserver(scheduleBoundsUpdate);
    observer.observe(host);
    window.addEventListener("resize", scheduleBoundsUpdate);
    scheduleBoundsUpdate();
    return () => {
      disposed = true;
      observer.disconnect();
      window.removeEventListener("resize", scheduleBoundsUpdate);
      if (frame !== 0) window.cancelAnimationFrame(frame);
    };
  }, [
    inventorySimulatorPanelAvailable,
    inventorySimulatorPanelOpen,
    inventorySimulatorPanelResizing,
    uiFontSize,
  ]);

  useEffect(() => {
    localStorage.setItem(INVENTORY_SIMULATOR_PANEL_WIDTH_KEY, String(Math.round(inventorySimulatorPanelWidth)));
  }, [inventorySimulatorPanelWidth]);

  useEffect(() => {
    const clampPanelWidth = () => {
      setInventorySimulatorPanelWidth((current) => normalizeInventorySimulatorPanelWidth(current));
    };
    window.addEventListener("resize", clampPanelWidth);
    return () => window.removeEventListener("resize", clampPanelWidth);
  }, []);

  useEffect(() => {
    const handleZoomShortcut = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      if (event.key === "0") {
        event.preventDefault();
        setUiFontSize(UI_FONT_SIZE_DEFAULT);
        return;
      }
      if (event.key === "+" || event.key === "=" || event.code === "NumpadAdd") {
        event.preventDefault();
        setUiFontSize((current) => stepUiFontSize(current, 1));
        return;
      }
      if (event.key === "-" || event.code === "NumpadSubtract") {
        event.preventDefault();
        setUiFontSize((current) => stepUiFontSize(current, -1));
      }
    };
    window.addEventListener("keydown", handleZoomShortcut);
    return () => window.removeEventListener("keydown", handleZoomShortcut);
  }, []);

  useEffect(() => () => {
    const context = taskSoundContextRef.current;
    taskSoundContextRef.current = null;
    if (context) {
      try {
        void context.close().catch(() => undefined);
      } catch {
        // Best-effort cleanup only.
      }
    }
  }, []);

  useEffect(() => {
    const persisted = {
      ...settings,
      exportCosmetics: cosmeticConsentAccepted && settings.exportCosmetics,
      includeSuspicious: false,
    };
    localStorage.setItem("demotracer.settings", JSON.stringify(persisted));
  }, [cosmeticConsentAccepted, settings]);

  useEffect(() => {
    if (cosmeticConsentAccepted) {
      localStorage.setItem(COSMETIC_CONSENT_STORAGE_KEY, "accepted");
    }
  }, [cosmeticConsentAccepted]);

  useEffect(() => {
    localStorage.setItem("demotracer.playback-preset.v1", JSON.stringify(playbackPreset));
  }, [playbackPreset]);

  useEffect(() => {
    localStorage.setItem("demotracer.local-environment.v1", JSON.stringify(localEnvironment));
  }, [localEnvironment]);

  useEffect(() => {
    if ("__TAURI_INTERNALS__" in window || !import.meta.env.DEV || browserLogPreviewSeededRef.current) return;
    browserLogPreviewSeededRef.current = true;
    const now = Date.now();
    setActivityLogs([
      { id: "preview-1", timestampMs: now - 42_000, level: "info", source: "app", message: "CS2 DemoTracer 1.1.0 started" },
      { id: "preview-2", timestampMs: now - 31_000, level: "debug", source: "analysis", message: "phase=parsing" },
      { id: "preview-3", timestampMs: now - 24_000, level: "info", source: "analysis", message: "Parsed match.dem.zst: 24 rounds · 10 players" },
      { id: "preview-4", timestampMs: now - 15_000, level: "warn", source: "conversion", message: "Round 12: partial player evidence was preserved" },
      { id: "preview-5", timestampMs: now - 4_000, level: "info", source: "gsi", message: "map=de_anubis · round=7 · roundPhase=freezetime · activity=playing" },
    ]);
    setGsiRuntimeStatus({
      listening: true,
      configured: true,
      connected: true,
      port: 32123,
      lastUpdateMs: now - 4_000,
      provider: "Counter-Strike 2",
      map: "de_anubis",
      mapPhase: "live",
      round: 7,
      roundPhase: "freezetime",
      playerActivity: "playing",
      playerHealth: 100,
    });
  }, []);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    void refreshActivityLogs();
    void invoke<ActivityLogMaintenance>("maintain_activity_logs").catch(() => undefined);
    const timer = window.setInterval(() => {
      void invoke<ActivityLogMaintenance>("maintain_activity_logs").catch(() => undefined);
    }, 15 * 60 * 1_000);
    return () => window.clearInterval(timer);
  }, [refreshActivityLogs]);

  useEffect(() => {
    if (activeSection !== "logs" || !("__TAURI_INTERNALS__" in window)) return;
    void refreshActivityLogs();
    const timer = window.setInterval(() => void refreshActivityLogs(), 2_500);
    return () => window.clearInterval(timer);
  }, [activeSection, refreshActivityLogs]);

  useEffect(() => {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || !("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    void invoke<GsiStatus>("configure_gsi", { cs2Path }).then((status) => {
      if (!disposed) setGsiRuntimeStatus(status);
    }).catch(async (reason) => {
      const error = parseCommandError(reason);
      const status = await invoke<GsiStatus>("gsi_status").catch(() => null);
      if (disposed) return;
      setGsiRuntimeStatus(status ? { ...status, error: error.message } : null);
      recordActivityLog("warn", "gsi", `GSI configuration skipped: ${error.code}`);
    });
    return () => { disposed = true; };
  }, [localEnvironment.cs2Path, recordActivityLog]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    void getVersion().then((currentVersion) => {
      if (disposed) return;
      setAppVersion(currentVersion);
      setGuiUpdate((current) => ({ ...current, currentVersion }));
      void checkGuiApplicationUpdate(false, currentVersion);
    }).catch(() => undefined);
    return () => {
      disposed = true;
      const pending = pendingGuiUpdateRef.current;
      pendingGuiUpdateRef.current = null;
      if (pending) void pending.close().catch(() => undefined);
    };
  }, []);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window) || localEnvironment.cs2Path.trim()) return;
    let disposed = false;
    setDetectingInstallations(true);
    void invoke<Cs2InstallCandidate[]>("detect_cs2_installations").then(async (candidates) => {
      if (disposed) return;
      setInstallCandidates(candidates);
      setInstallDetectionCompleted(true);
      const candidate = candidates[0];
      if (!candidate) return;
      setLocalEnvironment((current) => ({ ...current, cs2Path: candidate.path }));
      try {
        const report = await invoke<EnvironmentDiagnosticReport>("inspect_cs2_install", { path: candidate.path });
        if (!disposed) setEnvironmentReport(report);
      } catch {
        // Steam discovery remains useful even if the first diagnostic pass fails.
      }
    }).catch(() => {
      if (!disposed) setInstallDetectionCompleted(true);
    }).finally(() => {
      if (!disposed) setDetectingInstallations(false);
    });
    return () => { disposed = true; };
  }, []);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const cs2Path = localEnvironment.cs2Path.trim();
    let disposed = false;
    setPlaybackReleaseError("");
    setPlaybackUpdate(cs2Path ? { phase: "checking" } : { phase: "idle" });
    void invoke<PlaybackReleaseStatus>("playback_release_status", { cs2Path: cs2Path || null }).then((status) => {
      if (disposed) return;
      setPlaybackRelease(status);
    }).catch((reason) => {
      if (!disposed) setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    });
    if (cs2Path) {
      void invoke<PlaybackUpdateRelease>("playback_update_status", { cs2Path }).then((status) => {
        if (disposed) return;
        setPlaybackUpdate({
          phase: status.updateAvailable ? "available" : "current",
          latestVersion: status.latestVersion,
          notes: status.notes,
        });
      }).catch((reason) => {
        if (disposed) return;
        setPlaybackUpdate(playbackUpdateFailureStatus(reason, language));
      });
    }
    return () => { disposed = true; };
  }, [language, localEnvironment.cs2Path]);

  useEffect(() => {
    if (!archivePath || !archive) return;
    writeStoredLibrarySession(localStorage, {
      manifestPath: archivePath,
      selectedRound: selectedArchiveRound,
      selectedPlayer,
      commandMode,
    });
  }, [archive, archivePath, commandMode, selectedArchiveRound, selectedPlayer]);

  useEffect(() => {
    const cs2Path = startupServerConfigPathRef.current;
    if (!cs2Path || !("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    setLoadingServerConfig(true);
    void invoke<ServerConfigDocument>("load_server_config", { cs2Path }).then((document) => {
      if (disposed) return;
      setServerConfigDocument(document);
      setServerConfigDraft(document.normalizedJson || document.json);
      setServerConfigValidation(document.validation);
    }).catch(() => {
      // Startup probing is silent; the Settings page still exposes an explicit retry.
    }).finally(() => {
      if (!disposed) setLoadingServerConfig(false);
    });
    return () => { disposed = true; };
  }, []);

  useEffect(() => {
    localStorage.setItem(BATCH_PREFERENCES_STORAGE_KEY, JSON.stringify({
      folderPath: batchFolderPath,
      concurrency: batchConcurrency,
    } satisfies StoredBatchPreferences));
  }, [batchConcurrency, batchFolderPath]);

  useEffect(() => {
    const ready = new Set(batchCandidates.filter((candidate) => candidate.status === "ready").map((candidate) => candidate.id));
    setBatchSelectedIds((current) => {
      const next = current.filter((id) => ready.has(id)).slice(0, BATCH_SELECTION_LIMIT);
      return next.length === current.length && next.every((id, index) => id === current[index]) ? current : next;
    });
  }, [batchCandidates]);

  useEffect(() => {
    if (!batchInvocationActive) return;
    setBatchClock(Date.now());
    const timer = window.setInterval(() => setBatchClock(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [batchInvocationActive]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    const generation = batchGenerationRef.current;
    void invoke<BatchList>("list_batch_imports").then(({ batches }) => {
      if (disposed || generation !== batchGenerationRef.current || batches.length === 0) return;
      const resumable = findRestorableBatch(batches);
      if (!resumable) return;
      const latest = resumable;
      batchIdRef.current = latest.batchId;
      setBatchLedger(latest);
      setBatchFolderPath((current) => current || latest.sourceRoot);
      setBatchConcurrency(latest.requestedConcurrency && [2, 4, 6, 8].includes(latest.requestedConcurrency)
        ? latest.requestedConcurrency as 2 | 4 | 6 | 8
        : "auto");
    }).catch((reason) => {
      if (!disposed && generation === batchGenerationRef.current) {
        const error = parseCommandError(reason);
        setBatchScanError(userFacingErrorMessage(error, language));
      }
    });
    return () => { disposed = true; };
  }, []);

  useEffect(() => {
    if (environmentReport?.cached) return;
    if (!environmentReport) {
      localStorage.removeItem(ENVIRONMENT_REPORT_STORAGE_KEY);
      return;
    }
    const saved: StoredEnvironmentReport = {
      cs2Path: environmentReport.cs2Root,
      report: environmentReport,
    };
    localStorage.setItem(ENVIRONMENT_REPORT_STORAGE_KEY, JSON.stringify(saved));
  }, [environmentReport]);

  useEffect(() => {
    persistLibraryPreferences(libraryPreferences);
  }, [libraryPreferences]);

  useEffect(() => {
    persistDemoSourceIndex(demoSourceIndex);
  }, [demoSourceIndex]);

  useEffect(() => {
    if (libraryRoot || !("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    void invoke<string>("default_library_dir").then((path) => {
      if (disposed || !path) return;
      const root = normalizeLibraryRoot(path);
      setLibraryScan(null);
      setLibraryPreferences({ exportRoot: root, roots: [root] });
      setOutputDir(root);
    }).catch((reason) => {
      if (!disposed) setGlobalError(parseCommandError(reason));
    });
    return () => { disposed = true; };
  }, [libraryRoot]);

  useEffect(() => {
    if (phase === "analysisFailed") retryButtonRef.current?.focus({ preventScroll: true });
    if (phase === "complete") resultHeadingRef.current?.focus({ preventScroll: true });
    if (phase === "archive") {
      window.requestAnimationFrame(() => {
        const firstRound = document.querySelector<HTMLButtonElement>('.archive-round-option[aria-pressed="true"]:not(:disabled)');
        const archiveHeading = document.querySelector<HTMLElement>("#archive-workspace-title");
        (firstRound ?? archiveHeading)?.focus({ preventScroll: true });
      });
    }
    if (phase === "selecting") {
      window.requestAnimationFrame(() => {
        const firstRound = document.querySelector<HTMLInputElement>('.round-data-table input[data-round-select="true"]:not(:disabled)');
        const suspiciousToggle = document.querySelector<HTMLInputElement>(".allow-suspicious-control input");
        (firstRound ?? suspiciousToggle)?.focus({ preventScroll: true });
      });
    }
  }, [phase]);

  const absorbEvent = useCallback((raw: TaskEvent, token: number, source: "analysis" | "conversion") => {
    if (token !== taskTokenRef.current) return;

    if (raw.kind === "phase") {
      recordActivityLog("debug", source, `phase=${raw.phase}`);
      setProgress((current) => ({
        ...current,
        phase: phaseFromBackend(raw.phase, current.phase),
        unit: raw.phase === "voice" || raw.phase === "validating" ? null : current.unit,
        currentItem: raw.phase === "voice" || raw.phase === "validating" ? undefined : current.currentItem,
        announcement: raw.phase,
      }));
      return;
    }

    if (raw.kind === "log") {
      recordActivityLog(
        raw.level === "warning" ? "warn" : raw.level,
        source,
        raw.message,
      );
      if (raw.level === "warning" && !taskWarningsRef.current.includes(raw.message) && taskWarningsRef.current.length < 6) {
        taskWarningsRef.current = [...taskWarningsRef.current, raw.message];
      }
      setProgress((current) => ({
        ...current,
        log: [...current.log.slice(-199), { level: raw.level, message: raw.message }],
        warnings: taskWarningsRef.current,
      }));
      return;
    }

    const event: ConversionProgressEvent = raw.progress;
    setProgress((current) => {
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
          return {
            ...current,
            completedRounds: current.completedRounds + 1,
            announcement: `Round ${event.round}`,
          };
        case "roundSkipped": {
          if (event.reason === "not selected") return current;
          const message = `Round ${event.round}: ${event.reason}`;
          const policySkip = event.reason.startsWith("suspicious (");
          if (!taskWarningsRef.current.includes(message) && taskWarningsRef.current.length < 6) taskWarningsRef.current = [...taskWarningsRef.current, message];
          return {
            ...current,
            completedRounds: current.completedRounds + (policySkip ? 0 : 1),
            log: [...current.log.slice(-199), { level: "warning", message }],
            warnings: taskWarningsRef.current,
            announcement: `Round ${event.round}`,
          };
        }
        case "playerSkipped": {
          const message = `Round ${event.round}: ${event.reason}`;
          if (!taskWarningsRef.current.includes(message) && taskWarningsRef.current.length < 6) taskWarningsRef.current = [...taskWarningsRef.current, message];
          return {
            ...current,
            log: [...current.log.slice(-199), { level: "warning", message }],
            warnings: taskWarningsRef.current,
          };
        }
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
    });
  }, [recordActivityLog, words]);

  const runAnalysis = useCallback(async (
    path: string,
    expectedDemoSha256?: string,
    preserveArchive = false,
  ) => {
    if (!isDemoFilePath(path)) {
      setGlobalError({ code: "invalid_demo_path", message: words.invalidDemo, path });
      return;
    }

    primeTaskSound();
    const token = ++taskTokenRef.current;
    localStorage.removeItem(LIBRARY_SESSION_STORAGE_KEY);
    libraryRestoreRef.current = null;
    const maxRoundSeconds = settings.maxRoundSeconds;
    analyzedMaxRoundSecondsRef.current = maxRoundSeconds;
    setGlobalError(null);
    setAnalysisCancelPending(false);
    setAnalysisError("");
    setValidationError("");
    setSourcePath(path);
    setCosmeticPhrase("");
    setSettings((current) => ({ ...current, includeSuspicious: false }));
    setProgress({ ...emptyProgress(), phase: "parsing" });
    setSingleTask("analysis");
    setSingleTaskPanelOpen(true);
    setActiveTaskSourcePath(path);
    taskWarningsRef.current = [];
    recordActivityLog("info", "analysis", `Started parsing ${fileName(path) || path}`);

    const events = new Channel<TaskEvent>();
    events.onmessage = (event) => absorbEvent(event, token, "analysis");
    try {
      const next = await invoke<AnalysisResult>("analyze_demo", {
        request: {
          path,
          expectedDemoSha256: expectedDemoSha256 || null,
          maxRoundSeconds,
        },
        events,
      });
      if (token !== taskTokenRef.current) return;
      setSourcePath(next.sourcePath);
      setDemoSourceIndex((current) => rememberDemoSource(current, next.demoSha256, next.sourcePath));
      setAnalysis(next);
      setResult(null);
      setOutputRoot("");
      dispatchLibraryWorkspace({ type: "clear" });
      dispatchLibraryWorkspace({ type: "navigate", section: "analysis" });
      localStorage.removeItem(LIBRARY_SESSION_STORAGE_KEY);
      setSelectedRounds(new Set(next.rounds.filter((round) => round.selectedByDefault).map((round) => round.round)));
      setOutputDir((current) => current || libraryRoot);
      setPhase("selecting");
      setSingleTask(null);
      setSingleTaskPanelOpen(false);
      setAnalysisCancelPending(false);
      recordActivityLog(
        "info",
        "analysis",
        `Parsed ${next.fileName}: ${next.rounds.length} rounds · ${next.players.length} players`,
      );
      playTaskSound("success");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      const error = parseCommandError(reason);
      setAnalysisCancelPending(false);
      if (error.code === "analysis_cancelled") {
        recordActivityLog("warn", "analysis", `Parsing cancelled: ${fileName(path) || path}`);
        setAnalysisError("");
        setSingleTask(null);
        setSingleTaskPanelOpen(false);
        if (preserveArchive) {
          setPhase("archive");
        } else {
          setPhase("idle");
          setSourcePath("");
          dispatchLibraryWorkspace({ type: "navigate", section: "library" });
        }
        playTaskSound("stopped");
        return;
      }
      if (preserveArchive) {
        recordActivityLog("error", "analysis", `Parsing failed (${error.code}): ${fileName(path) || path}`);
        setSingleTask(null);
        setSingleTaskPanelOpen(false);
        setPhase("archive");
        setGlobalError(error);
        playTaskSound("failure");
        return;
      }
      dispatchLibraryWorkspace({ type: "clear" });
      dispatchLibraryWorkspace({ type: "navigate", section: "analysis" });
      localStorage.removeItem(LIBRARY_SESSION_STORAGE_KEY);
      recordActivityLog("error", "analysis", `Parsing failed (${error.code}): ${fileName(path) || path}`);
      setAnalysisError(userFacingErrorMessage(error, language));
      setPhase("analysisFailed");
      setSingleTask(null);
      setSingleTaskPanelOpen(false);
      playTaskSound("failure");
    }
  }, [absorbEvent, language, libraryRoot, playTaskSound, primeTaskSound, recordActivityLog, settings.maxRoundSeconds, words.invalidDemo]);

  async function cancelAnalysis() {
    if (singleTask !== "analysis" || analysisCancelPending) return;
    setAnalysisCancelPending(true);
    try {
      await invoke("cancel_analysis");
    } catch (reason) {
      setAnalysisCancelPending(false);
      setGlobalError(parseCommandError(reason));
    }
  }

  const preflightDemoSelection = useCallback(async (path: string) => {
    if (!isDemoFilePath(path)) {
      setGlobalError({ code: "invalid_demo_path", message: words.invalidDemo, path });
      return;
    }
    if (!("__TAURI_INTERNALS__" in window) || libraryRoots.length === 0) {
      await runAnalysis(path);
      return;
    }

    setGlobalError(null);
    setDemoPreflightActive(true);
    setDemoPreflightProgress({ current: 1, total: 1, fileName: fileName(path) });
    try {
      const preflight = await invoke<DemoSourcePreflight>("preflight_demo_source", {
        request: { path, libraryRoots },
      });
      setDemoPreflightActive(false);
      setDemoPreflightProgress(null);
      const reusableMatches = preflight.matches.filter(isReusableDemoArchive);
      if (reusableMatches.length > 0) {
        setDuplicateDemoConflict({
          primary: { ...preflight, matches: reusableMatches },
        });
        return;
      }
      await runAnalysis(preflight.sourcePath);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setDemoPreflightActive(false);
      setDemoPreflightProgress(null);
    }
  }, [libraryRoots, runAnalysis, words.invalidDemo]);

  function stageBatchSelections(
    selections: DemoSourcePreflight[],
    replaceSourceIds: string[],
    mergedSegments: number,
    relinkedDuplicates: number,
  ) {
    const root = commonParentDirectory(selections.map((item) => item.sourcePath));
    const candidates = selections.map((item) => ({
      path: item.sourcePath,
      relativePath: fileName(item.sourcePath),
      fileName: fileName(item.sourcePath),
      sizeBytes: String(item.sourceSizeBytes),
      compressed: item.compressed,
      modifiedAtMs: item.sourceModifiedAtMs ?? null,
    }));
    setBatchFolderPath(root);
    setBatchScan({
      root,
      recursive: false,
      limit: BATCH_SELECTION_LIMIT,
      candidates,
      truncated: false,
      skippedReparsePoints: 0,
      warnings: [],
    });
    setBatchReplaceSourceIds(replaceSourceIds);
    setBatchSelectedIds(candidates.map((candidate) => normalizedDiagnosticPath(candidate.path)));
    setBatchLedger(null);
    setBatchProgressByItem({});
    setBatchStartingCandidates([]);
    const notices = [
      replaceSourceIds.length > 0
        ? (language === "zh"
          ? `将重新解析并替换 ${replaceSourceIds.length} 个已有档案。`
          : `${replaceSourceIds.length} existing archives will be re-parsed and replaced.`)
        : "",
      mergedSegments > 0
        ? (language === "zh"
          ? `已将 ${mergedSegments} 个分段文件合并到对应比赛。`
          : `Merged ${mergedSegments} selected segment files into their matches.`)
        : "",
      relinkedDuplicates > 0
        ? (language === "zh"
          ? `已把 ${relinkedDuplicates} 个既有档案关联到本次选择的系列赛文件。`
          : `Linked ${relinkedDuplicates} existing archives to the selected series files.`)
        : "",
    ].filter(Boolean);
    setBatchScanError(notices.join(" "));
    dispatchLibraryWorkspace({ type: "navigate", section: "batch" });
  }

  async function prepareDemoSelections(paths: string[]) {
    const uniquePaths = [...new Map(
      paths
        .filter(isDemoFilePath)
        .map((path) => [normalizedDiagnosticPath(path), path] as const),
    ).values()];
    if (uniquePaths.length === 0 || uniquePaths.length > BATCH_SELECTION_LIMIT) {
      setGlobalError({
        code: "demo_selection_invalid",
        message: language === "zh"
          ? "一次请选择 1–8 个 .dem 或 .dem.zst 文件。"
          : "Choose 1–8 .dem or .dem.zst files at a time.",
      });
      return;
    }
    if (uniquePaths.length === 1) {
      await preflightDemoSelection(uniquePaths[0]);
      return;
    }

    setGlobalError(null);
    setDemoPreflightActive(true);
    try {
      const selections: DemoSourcePreflight[] = [];
      const duplicateMatches: DemoSourcePreflight[] = [];
      const repairSourceIds: string[] = [];
      const seenSources = new Set<string>();
      let mergedSegments = 0;
      let relinkedDuplicates = 0;
      for (const [index, path] of uniquePaths.entries()) {
        setDemoPreflightProgress({
          current: index + 1,
          total: uniquePaths.length,
          fileName: fileName(path),
        });
        const preflight = await invoke<DemoSourcePreflight>("preflight_demo_source", {
          request: { path, libraryRoots },
        });
        const key = normalizedDiagnosticPath(preflight.sourcePath);
        if (seenSources.has(key)) {
          mergedSegments += 1;
          continue;
        }
        seenSources.add(key);
        const reusableMatches = preflight.matches.filter(isReusableDemoArchive);
        if (preflight.matches.length > 0) {
          try {
            await invoke<ResolveArchiveSourceResult>("resolve_archive_source", {
              request: {
                manifestPath: preflight.matches[0].manifestPath,
                demoPath: preflight.sourcePath,
              },
            });
            relinkedDuplicates += 1;
          } catch {
            // Content deduplication remains valid even when an old or read-only
            // archive cannot refresh its local source pointer.
          }
        }
        if (reusableMatches.length > 0) {
          duplicateMatches.push({ ...preflight, matches: reusableMatches });
        } else if (preflight.matches.length > 0) {
          repairSourceIds.push(key);
        }
        selections.push(preflight);
      }

      if (selections.length === 0) {
        return;
      }

      if (duplicateMatches.length > 0) {
        setDuplicateDemoConflict({
          primary: duplicateMatches[0],
          batch: {
            selections,
            replaceSourceIds: [
              ...repairSourceIds,
              ...duplicateMatches.map((item) => normalizedDiagnosticPath(item.sourcePath)),
            ],
            mergedSegments,
            relinkedDuplicates,
          },
        });
        return;
      }

      stageBatchSelections(selections, repairSourceIds, mergedSegments, relinkedDuplicates);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setDemoPreflightActive(false);
      setDemoPreflightProgress(null);
    }
  }

  const runManifest = useCallback(async (path: string) => {
    if (!path.toLowerCase().endsWith(".json")) {
      setGlobalError({ code: "invalid_manifest_path", message: words.invalidManifest, path });
      return;
    }

    const returnPhase = phase;
    const returnSection = activeSection;
    const restoringSavedSession = libraryRestoreRef.current?.manifestPath.toLocaleLowerCase() === path.toLocaleLowerCase();
    const token = ++manifestReadTokenRef.current;
    const cacheKey = normalizedDiagnosticPath(path);
    const cached = manifestCacheRef.current.get(cacheKey);
    const showArchive = (next: ManifestArchive) => {
      const restored = libraryRestoreRef.current?.manifestPath.toLocaleLowerCase() === next.manifestPath.toLocaleLowerCase()
        ? libraryRestoreRef.current
        : null;
      dispatchLibraryWorkspace({ type: "open", archive: next, restored });
      setSourcePath(next.sourcePath ?? "");
      libraryRestoreRef.current = null;
      setAnalysis(null);
      setResult(null);
      setOutputRoot(next.root);
      setSelectedRounds(new Set());
      setPhase("archive");
    };
    setGlobalError(null);
    dispatchLibraryWorkspace({ type: "opening", path });
    if (cached) {
      showArchive(cached);
      return;
    }
    setPhase("openingArchive");
    try {
      const next = await invoke<ManifestArchive>("read_manifest", { path });
      if (token !== manifestReadTokenRef.current) return;
      manifestCacheRef.current.set(cacheKey, next);
      manifestCacheRef.current.set(normalizedDiagnosticPath(next.manifestPath), next);
      showArchive(next);
    } catch (reason) {
      if (token !== manifestReadTokenRef.current) return;
      if (restoringSavedSession) {
        libraryRestoreRef.current = null;
        localStorage.removeItem(LIBRARY_SESSION_STORAGE_KEY);
      } else {
        setGlobalError(parseCommandError(reason));
      }
      setPhase(returnPhase === "openingArchive" ? "idle" : returnPhase);
      dispatchLibraryWorkspace({ type: "navigate", section: returnSection });
    }
  }, [activeSection, phase, words.invalidManifest]);

  const inspectLibraryEntry = useCallback(async (entry: DemoLibraryEntry): Promise<ManifestArchive> => {
    const cacheKey = normalizedDiagnosticPath(entry.manifestPath);
    const cached = manifestCacheRef.current.get(cacheKey);
    if (cached) return cached;
    const inspected = await invoke<ManifestArchive>("read_manifest", { path: entry.manifestPath });
    manifestCacheRef.current.set(cacheKey, inspected);
    manifestCacheRef.current.set(normalizedDiagnosticPath(inspected.manifestPath), inspected);
    return inspected;
  }, []);

  async function saveArchiveNote(note: string): Promise<boolean> {
    if (!archive || savingArchiveNote) return false;
    setSavingArchiveNote(true);
    setGlobalError(null);
    try {
      const saved = await invoke<SaveArchiveNoteResult>("save_archive_note", {
        request: { manifestPath: archive.manifestPath, note },
      });
      const updated = { ...archive, note: saved.note };
      dispatchLibraryWorkspace({ type: "replaceArchive", archive: updated });
      manifestCacheRef.current.set(normalizedDiagnosticPath(updated.manifestPath), updated);
      setLibraryScan((current) => current ? {
        ...current,
        entries: current.entries.map((entry) => (
          normalizedDiagnosticPath(entry.manifestPath) === normalizedDiagnosticPath(saved.manifestPath)
            ? { ...entry, note: saved.note }
            : entry
        )),
      } : current);
      return true;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return false;
    } finally {
      setSavingArchiveNote(false);
    }
  }

  useEffect(() => {
    const saved = libraryRestoreRef.current;
    if (!saved || libraryRestoreStartedRef.current || !("__TAURI_INTERNALS__" in window)) return;
    libraryRestoreStartedRef.current = true;
    void runManifest(saved.manifestPath);
  }, [runManifest]);

  const prewarmManifestCache = useCallback(async (
    entries: DemoLibraryEntry[],
    generation: number,
  ) => {
    await new Promise<void>((resolve) => window.setTimeout(resolve, 0));
    for (const entry of entries) {
      if (generation !== manifestCacheGenerationRef.current) return;
      const cacheKey = normalizedDiagnosticPath(entry.manifestPath);
      if (manifestCacheRef.current.has(cacheKey)) continue;
      try {
        const archive = await invoke<ManifestArchive>("read_manifest", { path: entry.manifestPath });
        if (generation !== manifestCacheGenerationRef.current) return;
        manifestCacheRef.current.set(cacheKey, archive);
        manifestCacheRef.current.set(normalizedDiagnosticPath(archive.manifestPath), archive);
      } catch {
        // Library summaries remain usable when a full manifest cannot be warmed.
        // Opening that archive will surface the normal actionable error.
      }
      await new Promise<void>((resolve) => window.setTimeout(resolve, 0));
    }
  }, []);

  const scanLibrary = useCallback(async (roots: string[]) => {
    const paths = uniqueLibraryRoots(roots);
    if (paths.length === 0 || !("__TAURI_INTERNALS__" in window)) return;
    const token = ++libraryScanTokenRef.current;
    const cacheGeneration = ++manifestCacheGenerationRef.current;
    manifestCacheRef.current.clear();
    setGlobalError(null);
    setLibraryLoading(true);
    try {
      const scans = await Promise.all(paths.map(async (root): Promise<DemoLibraryScan> => {
        try {
          return await invoke<DemoLibraryScan>("scan_demo_library", { root });
        } catch (reason) {
          const error = parseCommandError(reason);
          return {
            root,
            entries: [],
            skipped: [{ path: error.path ?? root, message: userFacingErrorMessage(error, language) }],
          };
        }
      }));
      if (token !== libraryScanTokenRef.current) return;
      const merged = mergeLibraryScans(scans, paths[0]);
      setLibraryScan(merged);
      void prewarmManifestCache(merged.entries, cacheGeneration);
    } catch (reason) {
      if (token !== libraryScanTokenRef.current) return;
      const error = parseCommandError(reason);
      setGlobalError(error);
      setLibraryScan((current) => current ?? {
        root: paths[0],
        entries: [],
        skipped: [{ path: error.path ?? paths[0], message: userFacingErrorMessage(error, language) }],
      });
    } finally {
      if (token === libraryScanTokenRef.current) setLibraryLoading(false);
    }
  }, [prewarmManifestCache]);

  function applyBatchLedger(next: BatchLedger, generation: number, allowBatchSwitch = false) {
    if (generation !== batchGenerationRef.current) return;
    const activeBatchId = batchIdRef.current;
    if (activeBatchId && activeBatchId !== next.batchId && !allowBatchSwitch) return;
    batchIdRef.current = next.batchId;
    setBatchLedger((current) => {
      if (!current || current.batchId !== next.batchId || next.revision > current.revision) return next;
      return current;
    });
    setBatchFolderPath((current) => current || next.sourceRoot);
  }

  async function refreshBatchLedger(batchId: string, generation: number) {
    try {
      const next = await invoke<BatchLedger>("read_batch_import", { request: { batchId } });
      applyBatchLedger(next, generation);
    } catch {
      // The command that owns the batch will surface terminal errors. Event-time refreshes are
      // best effort so a transient read cannot hide the live per-item event stream.
    }
  }

  function updateBatchLedgerItem(batchId: string, itemId: string, patch: Partial<BatchItem>) {
    setBatchLedger((current) => {
      if (!current || current.batchId !== batchId) return current;
      return {
        ...current,
        items: current.items.map((item) => item.itemId === itemId ? { ...item, ...patch } : item),
      };
    });
  }

  function handleBatchEvent(event: BatchEvent, generation: number) {
    if (generation !== batchGenerationRef.current) return;
    if (batchIdRef.current && batchIdRef.current !== event.batchId) return;
    batchIdRef.current = event.batchId;
    switch (event.kind) {
      case "started":
        recordActivityLog("info", "batch", `Batch ${event.batchId} started`);
        void refreshBatchLedger(event.batchId, generation);
        if (batchStopPendingRef.current) void requestBatchCancel(event.batchId, generation);
        break;
      case "itemPhase":
        updateBatchLedgerItem(event.batchId, event.itemId, {
          status: event.phase === "complete" ? "completed" : event.phase === "failed" ? "failed" : "running",
          phase: event.phase,
        });
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: {
            ...(current[event.itemId] ?? { written: 0, estimated: 0 }),
            startedAtMs: current[event.itemId]?.startedAtMs ?? Date.now(),
          },
        }));
        break;
      case "itemTask":
        if (event.task.kind === "log") {
          recordActivityLog(
            event.task.level === "warning" ? "warn" : event.task.level,
            "batch",
            `${event.itemId}: ${event.task.message}`,
          );
        }
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: nextBatchItemProgress(current[event.itemId], event.task),
        }));
        break;
      case "estimateUpdated":
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "itemCompleted":
        recordActivityLog("info", "batch", `Completed ${fileName(event.manifestPath) || event.itemId}`);
        invalidateManifestCache(event.manifestPath);
        updateBatchLedgerItem(event.batchId, event.itemId, {
          status: "completed",
          phase: "complete",
          archiveRoot: event.archiveRoot,
          manifestPath: event.manifestPath,
          error: null,
        });
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: {
            ...(current[event.itemId] ?? { written: 0, estimated: 0 }),
            progress: 1,
            finishedAtMs: Date.now(),
          },
        }));
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "itemFailed":
        recordActivityLog("error", "batch", `${event.itemId}: ${event.error.message || event.error.code}`);
        updateBatchLedgerItem(event.batchId, event.itemId, {
          status: "failed",
          phase: "failed",
          error: event.error,
        });
        setBatchProgressByItem((current) => ({
          ...current,
          [event.itemId]: {
            ...(current[event.itemId] ?? { written: 0, estimated: 0 }),
            finishedAtMs: Date.now(),
          },
        }));
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "paused":
        recordActivityLog("warn", "batch", `Batch ${event.batchId} paused`);
        setBatchLedger((current) => current?.batchId === event.batchId ? { ...current, status: "paused" } : current);
        void refreshBatchLedger(event.batchId, generation);
        break;
      case "finished":
        recordActivityLog(
          event.failed > 0 ? "warn" : "info",
          "batch",
          `Batch ${event.batchId} finished: ${event.completed} completed · ${event.failed} failed`,
        );
        setBatchLedger((current) => current?.batchId === event.batchId
          ? { ...current, status: event.failed > 0 ? "completedWithErrors" : "completed" }
          : current);
        void refreshBatchLedger(event.batchId, generation);
        break;
    }
  }

  async function startBatchImport(candidateIds: string[]) {
    if (singleTask || batchInvocationActive || candidateIds.length === 0) return;
    if (settings.exportCosmetics && !cosmeticConsentAccepted) {
      requestCosmeticExport();
      return;
    }
    let destination = libraryRoot;
    if (!destination) destination = (await chooseLibraryRoot()) ?? "";
    if (!destination) return;
    const selected = new Set(candidateIds.slice(0, BATCH_SELECTION_LIMIT));
    const candidates = batchCandidates.filter((candidate) => selected.has(candidate.id) && candidate.status === "ready");
    if (candidates.length !== selected.size) {
      setBatchSelectedIds(candidates.map((candidate) => candidate.id));
      setGlobalError({
        code: "batch_selection_changed",
        message: language === "zh"
          ? "本地库状态刚刚发生变化，已从选择中移除已入库的 Demo。请确认剩余项目后再启动。"
          : "The local library changed, so already imported demos were removed from the selection. Review the remaining items and start again.",
      });
      return;
    }
    if (candidates.length === 0) return;
    const replaceDemoPaths = candidates
      .filter((candidate) => batchReplaceSources.has(candidate.id))
      .map((candidate) => candidate.path);

    primeTaskSound();
    const generation = ++batchGenerationRef.current;
    batchStopPendingRef.current = false;
    batchCancelGenerationRef.current = -1;
    setBatchStopPending(false);
    batchIdRef.current = "";
    setBatchLedger(null);
    setBatchProgressByItem({});
    setBatchStartingCandidates(candidates);
    setBatchInvocationActive(true);
    setGlobalError(null);
    const events = new Channel<BatchEvent>();
    events.onmessage = (event) => handleBatchEvent(event, generation);
    try {
      const next = await invoke<BatchLedger>("start_batch_import", {
        request: {
          sourceRoot: batchScan?.root ?? batchFolderPath,
          libraryRoot: destination,
          demoPaths: candidates.map((candidate) => candidate.path),
          replaceDemoPaths,
          concurrency: batchConcurrency === "auto" ? null : batchConcurrency,
          settings: {
            includeSuspicious: settings.includeSuspicious,
            fullRound: settings.fullRound,
            side: settings.side,
            subtickMode: settings.subtickMode,
            maxRoundSeconds: settings.maxRoundSeconds,
            freezePrerollSeconds: settings.freezePrerollSeconds,
            exportVoice: settings.exportVoice,
            exportCosmetics: settings.exportCosmetics,
            exportStickers: settings.exportCosmetics && settings.exportStickers,
            exportCharms: settings.exportCosmetics && settings.exportCharms,
          },
          cosmeticConsent: settings.exportCosmetics ? { phrase: COSMETIC_PHRASE } : null,
        },
        events,
      });
      if (generation !== batchGenerationRef.current) return;
      applyBatchLedger(next, generation, true);
      setBatchSelectedIds([]);
      setBatchReplaceSourceIds([]);
      if (next.status === "paused") playTaskSound("stopped");
      else if (next.items.some((item) => item.status === "failed")) playTaskSound("failure");
      else playTaskSound("success");
      await scanLibrary(withExportRoot(libraryRoots, destination));
      if (next.status === "completed") {
        finishBatchWorkspace(next.items.filter((item) => item.status === "completed").length);
      }
    } catch (reason) {
      if (generation !== batchGenerationRef.current) return;
      setGlobalError(parseCommandError(reason));
      playTaskSound("failure");
      if (batchIdRef.current) await refreshBatchLedger(batchIdRef.current, generation);
    } finally {
      if (generation === batchGenerationRef.current) {
        batchStopPendingRef.current = false;
        setBatchStopPending(false);
        setBatchStartingCandidates([]);
        setBatchInvocationActive(false);
      }
    }
  }

  async function resumeBatchImport(itemId?: string) {
    if (batchInvocationActive || !batchLedger) return;
    primeTaskSound();
    const generation = ++batchGenerationRef.current;
    batchStopPendingRef.current = false;
    batchCancelGenerationRef.current = -1;
    setBatchStopPending(false);
    setBatchInvocationActive(true);
    setBatchProgressByItem({});
    setGlobalError(null);
    const batchId = batchLedger.batchId;
    batchIdRef.current = batchId;
    const events = new Channel<BatchEvent>();
    events.onmessage = (event) => handleBatchEvent(event, generation);
    try {
      const next = await invoke<BatchLedger>("resume_batch_import", {
        request: {
          batchId,
          retryFailed: Boolean(itemId),
          itemId: itemId ?? null,
        },
        events,
      });
      if (generation !== batchGenerationRef.current) return;
      applyBatchLedger(next, generation);
      if (next.status === "paused") playTaskSound("stopped");
      else if (next.items.some((item) => item.status === "failed")) playTaskSound("failure");
      else playTaskSound("success");
      await scanLibrary(withExportRoot(libraryRoots, next.libraryRoot));
      if (next.status === "completed") {
        finishBatchWorkspace(next.items.filter((item) => item.status === "completed").length);
      }
    } catch (reason) {
      if (generation !== batchGenerationRef.current) return;
      setGlobalError(parseCommandError(reason));
      playTaskSound("failure");
      await refreshBatchLedger(batchId, generation);
    } finally {
      if (generation === batchGenerationRef.current) {
        batchStopPendingRef.current = false;
        setBatchStopPending(false);
        setBatchInvocationActive(false);
      }
    }
  }

  async function requestBatchCancel(batchId: string, generation: number) {
    if (generation !== batchGenerationRef.current || batchCancelGenerationRef.current === generation) return;
    batchCancelGenerationRef.current = generation;
    setBatchLedger((current) => current?.batchId === batchId ? { ...current, status: "stopping", cancelRequested: true } : current);
    try {
      const next = await invoke<BatchLedger>("cancel_batch_import", { request: { batchId } });
      if (generation !== batchGenerationRef.current) return;
      applyBatchLedger(next, generation);
    } catch (reason) {
      if (generation !== batchGenerationRef.current) return;
      batchStopPendingRef.current = false;
      setBatchStopPending(false);
      setBatchLedger((current) => current?.batchId === batchId && current.status === "stopping"
        ? { ...current, status: "running", cancelRequested: false }
        : current);
      setGlobalError(parseCommandError(reason));
    } finally {
      if (batchCancelGenerationRef.current === generation) batchCancelGenerationRef.current = -1;
    }
  }

  function stopBatchImport() {
    if (!batchInvocationActive) return;
    batchStopPendingRef.current = true;
    setBatchStopPending(true);
    const batchId = batchIdRef.current || batchLedger?.batchId;
    if (batchId) void requestBatchCancel(batchId, batchGenerationRef.current);
  }

  function finishBatchWorkspace(completed = 0) {
    batchIdRef.current = "";
    setBatchLedger(null);
    setBatchProgressByItem({});
    setBatchStartingCandidates([]);
    setBatchScan(null);
    setBatchScanError("");
    setBatchSelectedIds([]);
    setBatchReplaceSourceIds([]);
    dispatchLibraryWorkspace({ type: "navigate", section: "library" });
    if (completed > 0) {
      setLibraryNotice(language === "zh"
        ? `已导入 ${completed} 个 Demo。`
        : `Imported ${completed} demos.`);
    }
  }

  function leaveBatchWorkspace() {
    if (batchInvocationActive || canResumeBatch) {
      dispatchLibraryWorkspace({ type: "navigate", section: "library" });
      return;
    }
    finishBatchWorkspace();
  }

  useEffect(() => {
    if (libraryRoots.length > 0) void scanLibrary(libraryRoots);
  }, [libraryRoots, scanLibrary]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window) || !analysis || !outputDir || phase !== "selecting") return;
    let disposed = false;
    void invoke<OutputPreflight>("preflight_output", {
      request: { analysisId: analysis.analysisId, outputDir },
    }).then((preflight) => {
      if (!disposed) setOutputRoot(preflight.root);
    }).catch((reason) => {
      if (!disposed) setGlobalError(parseCommandError(reason));
    });
    return () => { disposed = true; };
  }, [analysis, outputDir, phase]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let unlisten: (() => void) | undefined;
    void getCurrentWebview().onDragDropEvent((event) => {
      if (event.payload.type === "enter" || event.payload.type === "over") {
        if (!isBusy) setDragActive(true);
        return;
      }
      if (event.payload.type === "leave") {
        setDragActive(false);
        return;
      }
      setDragActive(false);
      if (isBusy) return;
      const paths = event.payload.paths;
      if (paths.length === 1 && paths[0].toLowerCase().endsWith(".json")) {
        void runManifest(paths[0]);
        return;
      }
      if (paths.length === 0 || paths.length > BATCH_SELECTION_LIMIT || !paths.every(isDemoFilePath)) {
        setGlobalError({
          code: "demo_selection_invalid",
          message: language === "zh"
            ? "请拖入 1–8 个 .dem 或 .dem.zst 文件；manifest.json 需要单独拖入。"
            : "Drop 1–8 .dem or .dem.zst files. Drop a manifest.json by itself.",
        });
        return;
      }
      void prepareDemoSelections(paths);
    }).then((stop) => { unlisten = stop; });
    return () => unlisten?.();
  }, [isBusy, language, prepareDemoSelections, runManifest]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void getCurrentWindow().onCloseRequested((event) => {
      event.preventDefault();
      if (isBusyRef.current) {
        setCloseOpen(true);
        return;
      }
      void exitDesktopApp();
    }).then((stop) => {
      if (disposed) stop();
      else unlisten = stop;
    }).catch(() => undefined);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    const handleShortcut = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key.toLowerCase() !== "o") return;
      if (isBusy || overwriteConflict || duplicateDemoConflict || cosmeticOpen || closeOpen) return;
      event.preventDefault();
      if (event.shiftKey) void chooseManifest();
      else void chooseDemos();
    };
    window.addEventListener("keydown", handleShortcut);
    return () => window.removeEventListener("keydown", handleShortcut);
  });

  async function chooseDemo(initialPath = "") {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_demo", { initialPath: initialPath || null });
      if (path) await preflightDemoSelection(path);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function chooseDemos() {
    if (isBusy) return;
    try {
      const paths = await invoke<string[] | null>("choose_demos", { initialPath: null });
      if (paths?.length) await prepareDemoSelections(paths);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function chooseManifest() {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_manifest");
      if (path) await runManifest(path);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function addLibraryRoot() {
    if (isBusy) return;
    try {
      const path = await invoke<string | null>("choose_library_dir");
      if (!path) return;
      setLibraryNotice("");
      setLibraryScan(null);
      setLibraryPreferences((current) => ({
        ...current,
        roots: withExportRoot([...current.roots, path], current.exportRoot),
      }));
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  function removeLibraryRoot(root: string) {
    if (isBusy || root.toLocaleLowerCase() === libraryRoot.toLocaleLowerCase()) return;
    setLibraryNotice("");
    setLibraryScan(null);
    setLibraryPreferences((current) => ({
      ...current,
      roots: current.roots.filter((item) => item.toLocaleLowerCase() !== root.toLocaleLowerCase()),
    }));
  }

  async function chooseLibraryRoot(): Promise<string | null> {
    if (isBusy) return null;
    try {
      const path = await invoke<string | null>("choose_library_dir");
      if (!path) return null;
      const root = normalizeLibraryRoot(path);
      setLibraryNotice("");
      setLibraryScan(null);
      setLibraryPreferences((current) => ({
        exportRoot: root,
        roots: withExportRoot(current.roots, root),
      }));
      setOutputDir(root);
      setOutputRoot("");
      return root;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  async function repairArchiveMetadata(entry: DemoLibraryEntry) {
    if (isBusy) return;
    const needsMetadata = entry.metadataStatus !== "current";
    if (!needsMetadata) {
      try {
        setGlobalError(null);
        setLibraryNotice("");
        setRepairingManifest(entry.manifestPath);
        const resolvedSource = await resolveManifestDemoSource(entry);
        if (!resolvedSource) return;
        const name = entry.displayName || fileName(entry.demoPath) || entry.demoId;
        const notice = words.linkSourceResult.replace("{name}", name);
        setLibraryNotice(notice);
        setLiveMessage(notice);
        await scanLibrary(libraryRoots);
      } catch (reason) {
        setGlobalError(parseCommandError(reason));
      } finally {
        setRepairingManifest("");
      }
      return;
    }
    const recordedSource = entry.sourcePath?.trim() || "";
    const indexedSource = demoSourceIndex[entry.demoSha256.trim().toLocaleLowerCase()] || "";
    const recoverableSourceErrors = new Set([
      "source_demo_unavailable",
      "invalid_demo_path",
      "metadata_demo_read_failed",
      "metadata_demo_hash_mismatch",
    ]);
    try {
      setGlobalError(null);
      setLibraryNotice("");
      setRepairingManifest(entry.manifestPath);
      let result: RefreshArchiveMetadataResult | null = null;
      let sourceError: CommandErrorDto | null = null;
      const automaticCandidates: Array<string | null> = [null];
      if (indexedSource && indexedSource.toLocaleLowerCase() !== recordedSource.toLocaleLowerCase()) {
        automaticCandidates.push(indexedSource);
      }
      for (const demoPath of automaticCandidates) {
        try {
          result = await invoke<RefreshArchiveMetadataResult>("refresh_archive_metadata", {
            request: { manifestPath: entry.manifestPath, demoPath },
          });
          break;
        } catch (reason) {
          sourceError = parseCommandError(reason);
          if (!recoverableSourceErrors.has(sourceError.code)) throw reason;
        }
      }
      if (!result) {
        const demoPath = await invoke<string | null>("choose_demo", {
          initialPath: sourceError?.path || recordedSource || indexedSource || entry.demoPath || null,
        });
        if (!demoPath) return;
        result = await invoke<RefreshArchiveMetadataResult>("refresh_archive_metadata", {
          request: { manifestPath: entry.manifestPath, demoPath },
        });
      }
      setDemoSourceIndex((current) => rememberDemoSource(current, entry.demoSha256, result.sourcePath));
      invalidateManifestCache(entry.manifestPath);
      const notice = words.repairArchiveResult.replace("{name}", result.displayName);
      setLibraryNotice(notice);
      setLiveMessage(notice);
      await scanLibrary(libraryRoots);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setRepairingManifest("");
    }
  }

  async function deleteArchiveEntry(entry: DemoLibraryEntry) {
    if (isBusy || deletingManifest) return;
    const name = entry.displayName || fileName(entry.root) || entry.demoId;
    try {
      setGlobalError(null);
      setLibraryNotice("");
      setDeletingManifest(entry.manifestPath);
      await invoke<void>("delete_archive", {
        request: {
          manifestPath: entry.manifestPath,
          libraryRoots,
        },
      });
      invalidateManifestCache(entry.manifestPath);
      setArchiveDeleteTarget(null);
      const notice = words.archiveDeleted.replace("{name}", name);
      setLibraryNotice(notice);
      setLiveMessage(notice);
      await scanLibrary(libraryRoots);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setDeletingManifest("");
    }
  }

  async function resolveManifestDemoSource(source: {
    manifestPath: string;
    demoSha256: string;
    demoPath: string;
    sourcePath?: string | null;
  }): Promise<string | null> {
    const recordedSource = source.sourcePath?.trim() || "";
    const indexedSource = demoSourceIndex[source.demoSha256.trim().toLocaleLowerCase()] || "";
    const recoverableSourceErrors = new Set([
      "source_demo_unavailable",
      "invalid_demo_path",
      "metadata_demo_read_failed",
      "metadata_demo_hash_mismatch",
    ]);
    let sourceError: CommandErrorDto | null = null;
    const automaticCandidates: Array<string | null> = [null];
    if (indexedSource && indexedSource.toLocaleLowerCase() !== recordedSource.toLocaleLowerCase()) {
      automaticCandidates.push(indexedSource);
    }
    let result: ResolveArchiveSourceResult | null = null;
    for (const demoPath of automaticCandidates) {
      try {
        result = await invoke<ResolveArchiveSourceResult>("resolve_archive_source", {
          request: { manifestPath: source.manifestPath, demoPath },
        });
        break;
      } catch (reason) {
        sourceError = parseCommandError(reason);
        if (!recoverableSourceErrors.has(sourceError.code)) throw reason;
      }
    }
    if (!result) {
      const demoPath = await invoke<string | null>("choose_demo", {
        initialPath: sourceError?.path || recordedSource || indexedSource || source.demoPath || null,
      });
      if (!demoPath) return null;
      result = await invoke<ResolveArchiveSourceResult>("resolve_archive_source", {
        request: { manifestPath: source.manifestPath, demoPath },
      });
    }
    setDemoSourceIndex((current) => rememberDemoSource(
      current,
      source.demoSha256,
      result.sourcePath,
    ));
    invalidateManifestCache(source.manifestPath);
    return result.sourcePath;
  }

  async function reconvertArchive(selectedArchive: ManifestArchive) {
    if (isBusy) return;
    try {
      setGlobalError(null);
      setRepairingManifest(selectedArchive.manifestPath);
      const resolvedSource = await resolveManifestDemoSource(selectedArchive);
      if (!resolvedSource) return;
      dispatchLibraryWorkspace({
        type: "replaceArchive",
        archive: { ...selectedArchive, sourcePath: resolvedSource },
      });
      setSourcePath(resolvedSource);
      setRepairingManifest("");
      await runAnalysis(resolvedSource, selectedArchive.demoSha256, true);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setRepairingManifest("");
    }
  }

  async function repairLibraryMetadata() {
    const roots = uniqueLibraryRoots(libraryRoots);
    if (isBusy || roots.length === 0) return;
    let shouldRescan = false;
    try {
      invalidateManifestCache();
      setGlobalError(null);
      setLibraryNotice("");
      setRepairingLibrary(true);
      shouldRescan = true;
      let workingSourceIndex = { ...demoSourceIndex };
      const absorbVerifiedSources = (result: RefreshLibraryMetadataResult) => {
        workingSourceIndex = Object.entries(result.sourcePaths).reduce(
          (index, [hash, path]) => rememberDemoSource(index, hash, path),
          workingSourceIndex,
        );
      };
      const failedRootResult = (
        _root: string,
        reason: unknown,
        fallback?: RefreshLibraryMetadataResult,
      ): RefreshLibraryMetadataResult => {
        const error = parseCommandError(reason);
        return {
          demosScanned: 0,
          demosMatched: 0,
          archivesUpdated: 0,
          archivesCurrent: fallback?.archivesCurrent ?? 0,
          archivesUnmatched: fallback?.archivesUnmatched ?? 0,
          sourceUnmatched: fallback?.sourceUnmatched ?? 0,
          sourcePaths: {},
          failures: [userFacingErrorMessage(error, language)],
        };
      };
      const firstPass = new Map<string, RefreshLibraryMetadataResult>();
      for (const root of roots) {
        try {
          const result = await invoke<RefreshLibraryMetadataResult>("refresh_library_metadata", {
            request: { libraryRoot: root, demoRoot: null, sourcePaths: workingSourceIndex },
          });
          firstPass.set(root, result);
          absorbVerifiedSources(result);
        } catch (reason) {
          firstPass.set(root, failedRootResult(root, reason));
        }
      }
      const automaticRetry = new Map<string, RefreshLibraryMetadataResult>();
      for (const root of roots.filter((candidate) => (firstPass.get(candidate)?.sourceUnmatched ?? 0) > 0)) {
        try {
          const result = await invoke<RefreshLibraryMetadataResult>("refresh_library_metadata", {
            request: { libraryRoot: root, demoRoot: null, sourcePaths: workingSourceIndex },
          });
          automaticRetry.set(root, result);
          absorbVerifiedSources(result);
        } catch (reason) {
          automaticRetry.set(root, failedRootResult(root, reason, firstPass.get(root)));
        }
      }
      let unresolvedRoots = roots.filter((root) => (
        automaticRetry.get(root)?.sourceUnmatched
        ?? firstPass.get(root)?.sourceUnmatched
        ?? 0
      ) > 0);
      const directoryPasses = new Map<string, RefreshLibraryMetadataResult[]>();
      const searchDemoRoot = async (demoRoot: string) => {
        for (const root of unresolvedRoots) {
          const previous = directoryPasses.get(root)?.at(-1)
            ?? automaticRetry.get(root)
            ?? firstPass.get(root);
          try {
            const result = await invoke<RefreshLibraryMetadataResult>("refresh_library_metadata", {
              request: { libraryRoot: root, demoRoot, sourcePaths: workingSourceIndex },
            });
            directoryPasses.set(root, [...(directoryPasses.get(root) ?? []), result]);
            absorbVerifiedSources(result);
          } catch (reason) {
            directoryPasses.set(root, [
              ...(directoryPasses.get(root) ?? []),
              failedRootResult(root, reason, previous),
            ]);
          }
        }
        unresolvedRoots = roots.filter((root) => (
          directoryPasses.get(root)?.at(-1)?.sourceUnmatched
          ?? automaticRetry.get(root)?.sourceUnmatched
          ?? firstPass.get(root)?.sourceUnmatched
          ?? 0
        ) > 0);
      };
      for (const demoRoot of uniqueLibraryRoots(localEnvironment.demoRoots)) {
        if (unresolvedRoots.length === 0) break;
        await searchDemoRoot(demoRoot);
      }
      if (unresolvedRoots.length > 0) {
        const initialPath = libraryScan?.entries.find((entry) => entry.sourcePath)?.sourcePath
          || Object.values(workingSourceIndex).at(-1)
          || localEnvironment.demoRoots.at(-1)
          || null;
        const demoRoot = await invoke<string | null>("choose_demo_source_dir", { initialPath });
        if (demoRoot) {
          await searchDemoRoot(demoRoot);
        }
      }
      const result = roots.reduce<RefreshLibraryMetadataResult>((total, root) => {
        const first = firstPass.get(root)!;
        const retry = automaticRetry.get(root);
        const searchResults = directoryPasses.get(root) ?? [];
        const searched = searchResults.at(-1);
        const latest = searched ?? retry ?? first;
        return {
          demosScanned: total.demosScanned + first.demosScanned
            + (retry?.demosScanned ?? 0)
            + searchResults.reduce((sum, item) => sum + item.demosScanned, 0),
          demosMatched: total.demosMatched + first.demosMatched
            + (retry?.demosMatched ?? 0)
            + searchResults.reduce((sum, item) => sum + item.demosMatched, 0),
          archivesUpdated: total.archivesUpdated + first.archivesUpdated
            + (retry?.archivesUpdated ?? 0)
            + searchResults.reduce((sum, item) => sum + item.archivesUpdated, 0),
          archivesCurrent: total.archivesCurrent + latest.archivesCurrent,
          archivesUnmatched: total.archivesUnmatched + latest.archivesUnmatched,
          sourceUnmatched: total.sourceUnmatched + latest.sourceUnmatched,
          sourcePaths: {
            ...total.sourcePaths,
            ...first.sourcePaths,
            ...(retry?.sourcePaths ?? {}),
            ...Object.assign({}, ...searchResults.map((item) => item.sourcePaths)),
          },
          failures: [
            ...total.failures,
            ...first.failures,
            ...(retry?.failures ?? []),
            ...searchResults.flatMap((item) => item.failures),
          ],
        };
      }, {
        demosScanned: 0,
        demosMatched: 0,
        archivesUpdated: 0,
        archivesCurrent: 0,
        archivesUnmatched: 0,
        sourceUnmatched: 0,
        sourcePaths: {},
        failures: [],
      });
      setDemoSourceIndex(workingSourceIndex);
      const notice = words.repairLibraryResult
        .replace("{updated}", String(result.archivesUpdated))
        .replace("{unmatched}", String(result.sourceUnmatched))
        .replace("{failed}", String(result.failures.length));
      setLibraryNotice(notice);
      setLiveMessage(notice);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      if (shouldRescan) await scanLibrary(libraryRoots);
      setRepairingLibrary(false);
    }
  }

  async function importArchives() {
    if (isBusy || !libraryRoot) return;
    try {
      const sourceRoot = await invoke<string | null>("choose_library_dir");
      if (!sourceRoot) return;
      setGlobalError(null);
      setLibraryNotice("");
      setImportingArchives(true);
      const result = await invoke<ImportArchivesResult>("import_archives", {
        request: { libraryRoot, sourceRoot },
      });
      const notice = words.importArchivesResult
        .replace("{imported}", String(result.archivesImported))
        .replace("{duplicates}", String(result.duplicatesSkipped))
        .replace("{rejected}", String(result.archivesRejected));
      setLibraryNotice(notice);
      setLiveMessage(notice);
      await scanLibrary(libraryRoots);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setImportingArchives(false);
    }
  }

  async function chooseOutput(): Promise<string | null> {
    try {
      const path = await invoke<string | null>("choose_output_dir");
      if (path) {
        const root = normalizeLibraryRoot(path);
        setLibraryNotice("");
        setLibraryScan(null);
        setLibraryPreferences((current) => ({
          exportRoot: root,
          roots: withExportRoot(current.roots, root),
        }));
        setOutputDir(root);
        setOutputRoot("");
        return root;
      }
      return path;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  async function checkGuiApplicationUpdate(manual = true, knownCurrentVersion?: string) {
    if (!("__TAURI_INTERNALS__" in window)) return;
    if (guiUpdate.phase === "checking" || guiUpdate.phase === "downloading" || guiUpdate.phase === "installing") return;
    setReleaseNotice("");
    const currentVersion = knownCurrentVersion
      ?? await getVersion().catch(() => guiUpdate.currentVersion || appVersion || packageMetadata.version);
    setGuiUpdate({ phase: "checking", currentVersion });
    try {
      const previous = pendingGuiUpdateRef.current;
      pendingGuiUpdateRef.current = null;
      if (previous) await previous.close().catch(() => undefined);

      const update = await check({ timeout: 15_000 });
      if (!update) {
        setGuiUpdate({
          phase: "current",
          currentVersion,
          availableVersion: currentVersion,
        });
        setGuiUpdateDialogOpen(false);
        if (manual) setReleaseNotice(words.releaseUpToDate);
        return;
      }

      pendingGuiUpdateRef.current = update;
      setGuiUpdate({
        phase: "available",
        currentVersion,
        availableVersion: update.version,
        notes: update.body ?? undefined,
      });
      setGuiUpdateDialogOpen(true);
    } catch {
      setGuiUpdate({
        phase: "error",
        currentVersion,
      });
    }
  }

  async function installGuiApplicationUpdate() {
    const update = pendingGuiUpdateRef.current;
    if (!update || guiUpdate.phase !== "available") return;
    let downloadedBytes = 0;
    let totalBytes: number | undefined;
    setReleaseNotice("");
    setGuiUpdate((current) => ({
      ...current,
      phase: "downloading",
      downloadedBytes: 0,
    }));
    try {
      await update.downloadAndInstall((event) => {
        if (event.event === "Started") {
          totalBytes = event.data.contentLength ?? undefined;
          setGuiUpdate((current) => ({
            ...current,
            phase: "downloading",
            downloadedBytes: 0,
            totalBytes,
          }));
        } else if (event.event === "Progress") {
          downloadedBytes += event.data.chunkLength;
          setGuiUpdate((current) => ({
            ...current,
            phase: "downloading",
            downloadedBytes,
            totalBytes,
          }));
        } else if (event.event === "Finished") {
          setGuiUpdate((current) => ({
            ...current,
            phase: "installing",
            downloadedBytes,
            totalBytes,
          }));
        }
      }, { timeout: 120_000 });
      pendingGuiUpdateRef.current = null;
      await relaunch();
    } catch {
      setGuiUpdate((current) => ({
        ...current,
        phase: "error",
      }));
    }
  }

  async function finishPlaybackChange(result: PlaybackInstallResult, action: "install" | "rollback") {
    setReleaseNotice(action === "install"
      ? (language === "zh"
          ? `已安装回放组件 v${result.version}（${result.installedFiles} 个文件，清理 ${result.removedLegacyFiles} 个旧 provider 文件）。`
          : `Installed playback v${result.version} (${result.installedFiles} files; removed ${result.removedLegacyFiles} legacy provider files).`)
      : (language === "zh" ? "已恢复上次安装前的回放组件。" : "Restored playback components from the pre-install backup."));
    await runEnvironmentInspection(localEnvironment.cs2Path);
    const status = await invoke<PlaybackReleaseStatus>("playback_release_status", {
      cs2Path: localEnvironment.cs2Path.trim(),
    });
    setPlaybackRelease(status);
    await checkPlaybackUpdate(true);
  }

  async function checkPlaybackUpdate(ignoreBusy = false) {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || (!ignoreBusy && releaseAction)) return;
    setPlaybackUpdate({ phase: "checking" });
    try {
      const status = await invoke<PlaybackUpdateRelease>("playback_update_status", { cs2Path });
      setPlaybackUpdate({
        phase: status.updateAvailable ? "available" : "current",
        latestVersion: status.latestVersion,
        notes: status.notes,
      });
    } catch (reason) {
      setPlaybackUpdate(playbackUpdateFailureStatus(reason, language));
    }
  }

  async function installLatestPlaybackBundle() {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || releaseAction) return;
    setReleaseAction("installingOnline");
    setPlaybackReleaseError("");
    setReleaseNotice("");
    try {
      const result = await invoke<PlaybackInstallResult>("install_latest_playback_bundle", { cs2Path });
      await finishPlaybackChange(result, "install");
    } catch (reason) {
      setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    } finally {
      setReleaseAction(null);
    }
  }

  async function installPlaybackBundle() {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || releaseAction) return;
    setPlaybackReleaseError("");
    setReleaseNotice("");
    try {
      const packagePath = await invoke<string | null>("choose_playback_bundle", { initialPath: null });
      if (!packagePath) return;
      setReleaseAction("installingFile");
      const result = await invoke<PlaybackInstallResult>("install_playback_bundle", { cs2Path, packagePath });
      await finishPlaybackChange(result, "install");
    } catch (reason) {
      setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    } finally {
      setReleaseAction(null);
    }
  }

  async function rollbackPlaybackInstall() {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || releaseAction || !playbackRelease?.canRollback) return;
    setReleaseAction("rollingBack");
    setPlaybackReleaseError("");
    setReleaseNotice("");
    try {
      const result = await invoke<PlaybackInstallResult>("rollback_playback_install", { cs2Path });
      await finishPlaybackChange(result, "rollback");
    } catch (reason) {
      setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    } finally {
      setReleaseAction(null);
    }
  }

  async function runEnvironmentInspection(path = localEnvironment.cs2Path) {
    const candidate = path.trim();
    if (!candidate || inspectingEnvironment) return;
    const token = ++environmentInspectionTokenRef.current;
    setGlobalError(null);
    setInspectingEnvironment(true);
    try {
      const report = await invoke<EnvironmentDiagnosticReport>("inspect_cs2_install", { path: candidate });
      if (token !== environmentInspectionTokenRef.current) return;
      setLocalEnvironment((current) => ({ ...current, cs2Path: report.cs2Root || candidate }));
      setEnvironmentReport(report);
    } catch (reason) {
      if (token !== environmentInspectionTokenRef.current) return;
      setGlobalError(parseCommandError(reason));
    } finally {
      if (token === environmentInspectionTokenRef.current) setInspectingEnvironment(false);
    }
  }

  async function chooseCs2Directory() {
    if (detectingInstallations || inspectingEnvironment) return;
    try {
      const path = await invoke<string | null>("choose_cs2_dir", {
        initialPath: localEnvironment.cs2Path.trim() || null,
      });
      if (!path) return;
      setLocalEnvironment((current) => ({ ...current, cs2Path: path }));
      setEnvironmentReport(null);
      await runEnvironmentInspection(path);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function detectCs2Installations() {
    if (detectingInstallations || inspectingEnvironment) return;
    setGlobalError(null);
    setInstallCandidates([]);
    setInstallDetectionCompleted(false);
    setDetectingInstallations(true);
    try {
      const candidates = await invoke<Cs2InstallCandidate[]>("detect_cs2_installations");
      setInstallCandidates(candidates);
      setInstallDetectionCompleted(true);
      if (candidates.length === 1) {
        const [candidate] = candidates;
        setLocalEnvironment((current) => ({ ...current, cs2Path: candidate.path }));
        setEnvironmentReport(null);
        await runEnvironmentInspection(candidate.path);
      }
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    } finally {
      setDetectingInstallations(false);
    }
  }

  function useCs2Candidate(candidate: Cs2InstallCandidate) {
    setLocalEnvironment((current) => ({ ...current, cs2Path: candidate.path }));
    setEnvironmentReport(null);
    void runEnvironmentInspection(candidate.path);
  }

  async function addDemoRoot() {
    if (isBusy) return;
    try {
      const initialPath = localEnvironment.demoRoots.at(-1) ?? "";
      const path = await invoke<string | null>("choose_demo_source_dir", {
        initialPath: initialPath || null,
      });
      if (!path) return;
      setLocalEnvironment((current) => ({
        ...current,
        demoRoots: uniqueLibraryRoots([...current.demoRoots, path]),
      }));
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  function removeDemoRoot(root: string) {
    setLocalEnvironment((current) => ({
      ...current,
      demoRoots: current.demoRoots.filter((candidate) => candidate.toLocaleLowerCase() !== root.toLocaleLowerCase()),
    }));
  }

  async function loadServerConfig(): Promise<boolean> {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || loadingServerConfig || savingServerConfig) return false;
    setLoadingServerConfig(true);
    setGlobalError(null);
    try {
      const document = await invoke<ServerConfigDocument>("load_server_config", { cs2Path });
      setServerConfigDocument(document);
      setServerConfigDraft(document.normalizedJson || document.json);
      setServerConfigValidation(document.validation);
      return true;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return false;
    } finally {
      setLoadingServerConfig(false);
    }
  }

  async function validateServerConfigDraft(): Promise<ServerConfigValidation | null> {
    if (!serverConfigDraft.trim()) return null;
    setGlobalError(null);
    try {
      const validation = await invoke<ServerConfigValidation>("validate_server_config", {
        request: { json: serverConfigDraft },
      });
      setServerConfigValidation(validation);
      return validation;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  async function saveServerConfig(): Promise<boolean> {
    const cs2Path = localEnvironment.cs2Path.trim();
    if (!cs2Path || !serverConfigDocument || savingServerConfig || !serverConfigDraft.trim()) return false;
    primeTaskSound();
    setSavingServerConfig(true);
    setGlobalError(null);
    try {
      const saved = await invoke<SaveServerConfigResult>("save_server_config", {
        request: {
          cs2Path,
          json: serverConfigDraft,
          expectedFingerprint: serverConfigDocument.fingerprint ?? null,
          replaceExisting: false,
        },
      });
      setServerConfigDocument(saved.document);
      setServerConfigDraft(saved.document.normalizedJson || saved.document.json);
      setServerConfigValidation(saved.document.validation);
      playTaskSound("success");
      return true;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      playTaskSound("failure");
      return false;
    } finally {
      setSavingServerConfig(false);
    }
  }

  async function preflightOutput(destination: string): Promise<OutputPreflight | null> {
    if (!analysis) return null;
    try {
      const preflight = await invoke<OutputPreflight>("preflight_output", {
        request: { analysisId: analysis.analysisId, outputDir: destination },
      });
      setOutputRoot(preflight.root);
      return preflight;
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
      return null;
    }
  }

  function toggleRound(round: RoundInfo) {
    if (round.status === "suspicious" && !settings.includeSuspicious) return;
    setSelectedRounds((current) => {
      const next = new Set(current);
      if (next.has(round.round)) next.delete(round.round);
      else next.add(round.round);
      return next;
    });
  }

  function restoreRecommended() {
    if (!analysis) return;
    setSelectedRounds(new Set(analysis.rounds.filter((round) => round.selectedByDefault).map((round) => round.round)));
  }

  function handleAllowSuspicious(checked: boolean) {
    setSettings((current) => ({ ...current, includeSuspicious: checked }));
    if (!checked && analysis) {
      const blocked = new Set(analysis.rounds.filter((round) => round.status === "suspicious").map((round) => round.round));
      setSelectedRounds((current) => new Set([...current].filter((round) => !blocked.has(round))));
    }
  }

  function updateSettings(patch: Partial<ConverterSettings>) {
    if (patch.exportCosmetics === false) setCosmeticPhrase("");
    setSettings((current) => ({ ...current, ...patch }));
  }

  function requestCosmeticExport() {
    if (cosmeticConsentAccepted) {
      setSettings((current) => ({ ...current, exportCosmetics: true }));
      return;
    }
    setCosmeticPhrase("");
    setCosmeticOpen(true);
  }

  function restoreDefaultSettings() {
    setSettings({ ...DEFAULT_SETTINGS });
    setCosmeticPhrase("");
    if (analysis) {
      const suspicious = new Set(analysis.rounds.filter((round) => round.status === "suspicious").map((round) => round.round));
      setSelectedRounds((current) => new Set([...current].filter((round) => !suspicious.has(round))));
    }
  }

  async function beginConvert() {
    if (!analysis || selectedRounds.size === 0) return;
    await runConversionStartExclusive(async () => {
      let destination = outputDir;
      if (!destination) destination = (await chooseOutput()) ?? "";
      if (!destination) return;
      if (settings.exportCosmetics && !cosmeticConsentAccepted) {
        requestCosmeticExport();
        return;
      }
      const preflight = await preflightOutput(destination);
      if (!preflight) return;
      if (preflight.exists) {
        setOverwriteConflict(preflight);
        return;
      }
      await performConvert(false, destination);
    });
  }

  async function runConversionStartExclusive(action: () => Promise<void>) {
    if (conversionStartLockRef.current || isBusyRef.current) return;
    conversionStartLockRef.current = true;
    setConversionStartPending(true);
    try {
      await action();
    } finally {
      conversionStartLockRef.current = false;
      setConversionStartPending(false);
    }
  }

  async function performConvert(overwrite: boolean, destination = outputDir) {
    if (!analysis || selectedRounds.size === 0 || !destination) return;
    primeTaskSound();
    const token = ++taskTokenRef.current;
    taskWarningsRef.current = [];
    setGlobalError(null);
    setValidationError("");
    setConversionWarnings([]);
    setOverwriteConflict(null);
    setResult(null);
    setProgress(emptyProgress());
    setSingleTask("conversion");
    setSingleTaskPanelOpen(true);
    setActiveTaskSourcePath(sourcePath);
    setPhase("converting");
    recordActivityLog(
      "info",
      "conversion",
      `Started conversion: ${selectedRounds.size} rounds · ${fileName(sourcePath) || sourcePath}`,
    );

    const events = new Channel<TaskEvent>();
    events.onmessage = (event) => absorbEvent(event, token, "conversion");
    try {
      const summary = await invoke<ConversionSummary>("convert_demo", {
        request: {
          analysisId: analysis.analysisId,
          outputDir: destination,
          selectedRounds: [...selectedRounds].sort((left, right) => left - right),
          includeSuspicious: settings.includeSuspicious,
          fullRound: settings.fullRound,
          side: settings.side,
          subtickMode: settings.subtickMode,
          freezePrerollSeconds: settings.freezePrerollSeconds,
          maxRoundSeconds: analyzedMaxRoundSecondsRef.current,
          exportVoice: settings.exportVoice,
          exportCosmetics: settings.exportCosmetics,
          exportStickers: settings.exportCosmetics && settings.exportStickers,
          exportCharms: settings.exportCosmetics && settings.exportCharms,
          cosmeticConsent: settings.exportCosmetics ? { phrase: COSMETIC_PHRASE } : null,
          overwrite: overwrite ? "replace" : "deny",
        },
        events,
      });
      if (token !== taskTokenRef.current) return;
      setResult(summary);
      invalidateManifestCache(summary.manifestPath);
      setOutputRoot(summary.root);
      setConversionWarnings(taskWarningsRef.current);
      dispatchLibraryWorkspace({
        type: "setCommandMode",
        mode: summary.rounds.length > 1 ? "sequence" : "round",
      });
      setProgress((current) => ({ ...current, phase: "complete" }));
      setLibraryPreferences((current) => ({
        exportRoot: destination,
        roots: withExportRoot(current.roots, destination),
      }));
      setOutputDir(destination);
      void scanLibrary(withExportRoot(libraryRoots, destination));
      setPhase("complete");
      setSingleTaskPanelOpen(false);
      recordActivityLog(
        "info",
        "conversion",
        `Conversion completed: ${summary.roundsExported} rounds · ${summary.filesWritten} files`,
      );
      playTaskSound("success");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      const error = parseCommandError(reason);
      recordActivityLog("error", "conversion", `Conversion failed (${error.code}): ${fileName(sourcePath) || sourcePath}`);
      if (error.code === "output_exists") {
        setOverwriteConflict({ root: error.path || outputRoot, exists: true });
        setPhase("selecting");
      } else if (error.code === "validation_failed") {
        setValidationError(userFacingErrorMessage(error, language));
        setPhase("validationFailed");
      } else {
        setGlobalError(error);
        setPhase("selecting");
      }
      setSingleTaskPanelOpen(false);
      playTaskSound("failure");
    } finally {
      if (token === taskTokenRef.current) setSingleTask(null);
    }
  }

  async function openPath(path: string) {
    if (!path) return;
    try {
      await invoke("open_output", { request: { path } });
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function revealPath(path: string) {
    if (!path) return;
    try {
      await invoke("reveal_output", { request: { path } });
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function reparseLibraryEntry(entry: DemoLibraryEntry) {
    if (isBusy) return;
    try {
      setGlobalError(null);
      const resolvedSource = await resolveManifestDemoSource(entry);
      if (resolvedSource) await runAnalysis(resolvedSource, entry.demoSha256);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  function confirmReparse() {
    const target = reparseTarget;
    if (!target) return;
    setReparseTarget(null);
    if (target.kind === "archive") {
      void reconvertArchive(target.archive);
    } else {
      void reparseLibraryEntry(target.entry);
    }
  }

  async function copyText(value: string, target: CopyTarget) {
    try {
      try {
        await navigator.clipboard.writeText(value);
      } catch {
        const textArea = document.createElement("textarea");
        textArea.value = value;
        textArea.style.position = "fixed";
        textArea.style.opacity = "0";
        document.body.appendChild(textArea);
        textArea.select();
        const copied = document.execCommand("copy");
        textArea.remove();
        if (!copied) throw new Error(words.copyFailed);
      }
      setCopiedTarget(target);
      setLiveMessage(words.copied);
      window.setTimeout(() => {
        setCopiedTarget((current) => current === target ? null : current);
        setLiveMessage("");
      }, 2000);
    } catch (reason) {
      setGlobalError({ code: "copy_failed", message: parseCommandError(reason).message });
      setLiveMessage(words.copyFailed);
    }
  }

  async function openExternal(url: string) {
    try {
      await invoke("open_external", { request: { url } });
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function startInventorySimulatorBatch(items: InventorySimulatorItem[], batchLanguage: Language) {
    const panelWasAvailable = inventorySimulatorPanelAvailable;
    setInventorySimulatorPanelOpen(true);
    try {
      let bounds: InventorySimulatorPanelBounds | null = null;
      for (let attempt = 0; attempt < 4 && !bounds; attempt += 1) {
        await new Promise<void>((resolve) => window.requestAnimationFrame(() => resolve()));
        const host = inventorySimulatorHostRef.current;
        if (host) bounds = measureInventorySimulatorPanel(host);
      }
      if (!bounds) throw new Error("Inventory Simulator panel layout is unavailable.");
      await invoke("start_inventory_simulator_batch", {
        request: { items, language: batchLanguage },
        bounds,
      });
      setInventorySimulatorPanelAvailable(true);
      setLiveMessage(words.inventorySimulatorBatchOpened.replace("{count}", String(items.length)));
      window.setTimeout(() => setLiveMessage(""), 3000);
    } catch (reason) {
      if (!panelWasAvailable) setInventorySimulatorPanelOpen(false);
      setGlobalError(parseCommandError(reason));
      throw reason;
    }
  }

  function closePlayerAnalysis() {
    const selectionKey = selectedPlayer ? playerSelectionKey(selectedPlayer) : null;
    dispatchLibraryWorkspace({ type: "selectPlayer", player: null });
    if (!selectionKey) return;
    window.requestAnimationFrame(() => {
      const trigger = [...document.querySelectorAll<HTMLButtonElement>("[data-player-key]")]
        .find((button) => button.dataset.playerKey === selectionKey);
      const focusTarget = trigger ?? document.querySelector<HTMLButtonElement>("[data-player-key]");
      const disclosure = focusTarget?.closest<HTMLDetailsElement>("details");
      if (disclosure) disclosure.open = true;
      focusTarget?.focus({ preventScroll: true });
    });
  }

  function resetSession() {
    if (singleTask) {
      setSingleTaskPanelOpen(true);
      return;
    }
    ++taskTokenRef.current;
    ++manifestReadTokenRef.current;
    localStorage.removeItem(LIBRARY_SESSION_STORAGE_KEY);
    libraryRestoreRef.current = null;
    dispatchLibraryWorkspace({ type: "clear" });
    setSingleTask(null);
    setActiveTaskSourcePath("");
    setPhase("idle");
    setSourcePath("");
    setOutputRoot("");
    setAnalysis(null);
    setResult(null);
    setSelectedRounds(new Set());
    setProgress(emptyProgress());
    setAnalysisError("");
    setValidationError("");
    setGlobalError(null);
    setCosmeticPhrase("");
    setSettings((current) => ({ ...current, includeSuspicious: false }));
  }

  async function requestWindowClose() {
    if (!("__TAURI_INTERNALS__" in window)) return;
    try {
      await getCurrentWindow().close();
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  async function exitDesktopApp() {
    try {
      await exitApp(0);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }

  const selectingView = analysis && (phase === "selecting" || singleTask === "conversion") ? (
    <div className="selection-layout">
      <RoundWorkspace
        words={words}
        language={language}
        analysis={analysis}
        selectedRounds={selectedRounds}
        allowSuspicious={settings.includeSuspicious}
        outputDir={outputDir}
        outputRoot={outputRoot}
        copiedTarget={copiedTarget}
        selectedPlayer={selectedPlayer}
        convertPending={conversionStartPending || singleTask === "conversion"}
        onToggleRound={toggleRound}
        onRestoreRecommended={restoreRecommended}
        onClearSelection={() => setSelectedRounds(new Set())}
        onAllowSuspiciousChange={handleAllowSuspicious}
        onChooseOutput={() => void chooseOutput()}
        onConvert={() => void beginConvert()}
        onSelectPlayer={(player) => dispatchLibraryWorkspace({ type: "selectPlayer", player })}
        onClosePlayer={closePlayerAnalysis}
        onCopy={(value, target) => void copyText(value, target)}
        onOpenExternal={(url) => void openExternal(url)}
        onSyncInventorySimulator={startInventorySimulatorBatch}
        formatNumber={(value) => numberFormat.format(value)}
      />
      {inspectorVisible ? (
        <ExportInspector
          words={words}
          settings={settings}
          disabled={conversionStartPending || singleTask === "conversion"}
          onChange={updateSettings}
          onRequestCosmetics={requestCosmeticExport}
          onRestoreDefaults={restoreDefaultSettings}
        />
      ) : null}
    </div>
  ) : null;

  const guiUpdateDialogStatus = guiUpdate.phase === "checking" ? words.releaseChecking
    : guiUpdate.phase === "current" ? words.releaseUpToDate
      : guiUpdate.phase === "available" ? words.releaseUpdateAvailable
        : guiUpdate.phase === "downloading" ? words.releaseDownloading
          : guiUpdate.phase === "installing" ? words.releaseInstalling
            : guiUpdate.phase === "error" ? words.releaseCheckUnavailable
              : words.releaseNotChecked;
  const guiUpdateDialogProgress = guiUpdate.totalBytes && guiUpdate.downloadedBytes != null
    ? Math.min(100, Math.round((guiUpdate.downloadedBytes / guiUpdate.totalBytes) * 100))
    : null;

  return (
    <div className={`app-shell${sidebarCollapsed ? " is-sidebar-collapsed" : ""}`}>
      <AppChrome
        words={words}
        sessionTitle={sessionTitle}
        sessionMeta={sessionMeta}
        faqActive={activeSection === "faq"}
        onOpenFaq={() => dispatchLibraryWorkspace({ type: "navigate", section: "faq" })}
        onOpenGithub={() => void openExternal("https://github.com/unicbm/demotracer")}
        onRequestClose={() => void requestWindowClose()}
      />

      <div className="app-body">
        <AppSidebar
          words={words}
          appVersion={appVersion}
          busy={isBusy}
          importActive={activeSection === "batch"}
          libraryActive={activeSection === "library"}
          analysisActive={activeSection === "analysis"}
          analysisAvailable={analysisAvailable}
          logsActive={activeSection === "logs"}
          settingsActive={activeSection === "settings"}
          collapsed={sidebarCollapsed}
          onOpenImport={() => {
            if (batchInvocationActive || canResumeBatch || hasRetryableBatchJobs) {
              dispatchLibraryWorkspace({ type: "navigate", section: "batch" });
            } else {
              void chooseDemos();
            }
          }}
          onOpenLibrary={() => dispatchLibraryWorkspace({ type: "navigate", section: "library" })}
          onOpenAnalysis={() => dispatchLibraryWorkspace({ type: "navigate", section: "analysis" })}
          onOpenLogs={() => dispatchLibraryWorkspace({ type: "navigate", section: "logs" })}
          onOpenSettings={() => dispatchLibraryWorkspace({ type: "navigate", section: "settings" })}
          onToggleCollapsed={() => setSidebarCollapsed((collapsed) => !collapsed)}
        />
        <main className="app-workspace">
        {globalError ? (
          <div className="error-strip system-feedback-toast" role="alert" aria-live="assertive">
            <AlertIcon size={17} />
            <div>
              <span className="system-feedback-heading"><strong>{userFacingErrorTitle(globalError, language)}</strong><code title={globalError.message}>{globalError.code}</code></span>
              <span>{userFacingErrorMessage(globalError, language)}</span>
            </div>
            <button className="icon-button" type="button" onClick={() => setGlobalError(null)} aria-label={words.dismiss}><CloseIcon size={15} /></button>
          </div>
        ) : null}

        {demoPreflightProgress ? (
          <div className="background-task-strip" role="status" aria-live="polite">
            <i aria-hidden="true" />
            <strong>
              {language === "zh"
                ? `正在检查本地档案 ${demoPreflightProgress.current}/${demoPreflightProgress.total}`
                : `Checking local library ${demoPreflightProgress.current}/${demoPreflightProgress.total}`}
            </strong>
            <code title={demoPreflightProgress.fileName}>{demoPreflightProgress.fileName}</code>
          </div>
        ) : null}
        {!demoPreflightProgress && singleTask && !singleTaskPanelOpen ? (
          <button
            className="background-task-strip batch-task-return"
            type="button"
            onClick={() => setSingleTaskPanelOpen(true)}
          >
            <i aria-hidden="true" />
            <strong>{singleTask === "conversion" ? words.conversionTitle : words.analyzingTitle}</strong>
            <code title={activeTaskSourcePath}>{fileName(activeTaskSourcePath)}</code>
          </button>
        ) : null}
        {!demoPreflightProgress && (batchInvocationActive || canResumeBatch || hasRetryableBatchJobs) && activeSection !== "batch" ? (
          <button
            className="background-task-strip batch-task-return"
            type="button"
            onClick={() => dispatchLibraryWorkspace({ type: "navigate", section: "batch" })}
          >
            <i aria-hidden="true" />
            <strong>{language === "zh" ? "多个 Demo 入库" : "Multiple demo import"}</strong>
            <code>{batchInvocationActive
              ? (language === "zh" ? `还有 ${batchActiveCount} 个` : `${batchActiveCount} remaining`)
              : hasRetryableBatchJobs
                ? (language === "zh" ? `${batchSummary.failed} 个需要处理` : `${batchSummary.failed} need attention`)
                : (language === "zh" ? `${batchActiveCount} 个待继续` : `${batchActiveCount} ready to resume`)}</code>
          </button>
        ) : null}

        {activeSection === "faq" ? (
          <FaqWorkspace language={language} />
        ) : activeSection === "logs" ? (
          <LogsWorkspace
            words={words}
            entries={activityLogs}
            gsiStatus={gsiRuntimeStatus}
            loading={activityLogsLoading}
            onRefresh={() => void refreshActivityLogs()}
            onOpenFolder={openActivityLogDirectory}
            onClear={clearActivityLogs}
          />
        ) : activeSection === "settings" ? (
          <SettingsWorkspace
            words={words}
            language={language}
            theme={theme}
            resolvedTheme={resolvedTheme}
            uiFontSize={uiFontSize}
            themeCustomization={themeCustomization}
            customCssProfiles={customCssProfiles}
            activeCustomCssProfileId={activeCustomCssProfileId}
            environment={localEnvironment}
            exportRoot={libraryRoot}
            archiveRoots={libraryRoots}
            converter={settings}
            cosmeticConsentAccepted={cosmeticConsentAccepted}
            playback={playbackPreset}
            candidates={installCandidates}
            report={environmentReport}
            serverConfigDocument={serverConfigDocument}
            serverConfigDraft={serverConfigDraft}
            serverConfigValidation={serverConfigValidation}
            loadingServerConfig={loadingServerConfig}
            savingServerConfig={savingServerConfig}
            detecting={detectingInstallations}
            detectionCompleted={installDetectionCompleted}
            inspecting={inspectingEnvironment}
            appVersion={appVersion}
            guiUpdate={guiUpdate}
            playbackRelease={playbackRelease}
            playbackUpdate={playbackUpdate}
            playbackReleaseError={playbackReleaseError}
            releaseAction={releaseAction}
            releaseNotice={releaseNotice}
            onUiFontSizeChange={setUiFontSize}
            onThemeCustomizationChange={setThemeCustomization}
            onSaveCustomCssProfile={(profile) => {
              const normalized = normalizeCustomCssProfiles([profile])[0];
              if (!normalized) return;
              setCustomCssProfiles((current) => {
                const index = current.findIndex((candidate) => candidate.id === normalized.id);
                if (index < 0) return [...current, normalized];
                return current.map((candidate) => candidate.id === normalized.id ? normalized : candidate);
              });
              setActiveCustomCssProfileId(normalized.id);
            }}
            onActivateCustomCssProfile={setActiveCustomCssProfileId}
            onDeleteCustomCssProfile={(profileId) => {
              setCustomCssProfiles((current) => current.filter((profile) => profile.id !== profileId));
              setActiveCustomCssProfileId((current) => current === profileId ? null : current);
            }}
            onLanguageChange={setLanguage}
            onThemeChange={setTheme}
            onCs2PathChange={(cs2Path) => {
              setLocalEnvironment((current) => ({ ...current, cs2Path }));
              setEnvironmentReport(null);
              setServerConfigDocument(null);
              setServerConfigDraft("");
              setServerConfigValidation(null);
            }}
            onBrowseCs2={() => void chooseCs2Directory()}
            onDetectCs2={() => void detectCs2Installations()}
            onUseCandidate={useCs2Candidate}
            onInspectEnvironment={() => void runEnvironmentInspection()}
            onCheckGuiUpdate={() => void checkGuiApplicationUpdate()}
            onInstallGuiUpdate={() => setGuiUpdateDialogOpen(true)}
            onCheckPlaybackUpdate={() => void checkPlaybackUpdate()}
            onInstallLatestPlayback={() => void installLatestPlaybackBundle()}
            onInstallPlaybackBundle={() => void installPlaybackBundle()}
            onRollbackPlayback={() => void rollbackPlaybackInstall()}
            onLoadServerConfig={loadServerConfig}
            onServerConfigDraftChange={(json) => {
              setServerConfigDraft(json);
              setServerConfigValidation(null);
            }}
            onValidateServerConfig={validateServerConfigDraft}
            onSaveServerConfig={saveServerConfig}
            onChooseExportRoot={() => void chooseLibraryRoot()}
            onAddArchiveRoot={() => void addLibraryRoot()}
            onRemoveArchiveRoot={removeLibraryRoot}
            onAddDemoRoot={() => void addDemoRoot()}
            onRemoveDemoRoot={removeDemoRoot}
            onOpenPath={(path) => void openPath(path)}
            onOpenLogDirectory={openActivityLogDirectory}
            onOpenExternal={(url) => void openExternal(url)}
            onEnvironmentChange={(patch) => setLocalEnvironment((current) => ({ ...current, ...patch }))}
            onConverterChange={updateSettings}
            onRequestCosmetics={requestCosmeticExport}
            onPlaybackChange={(patch) => setPlaybackPreset((current) => ({ ...current, ...patch }))}
          />
        ) : activeSection === "batch" || activeSection === "library" ? (
          <>
            <LibraryWorkspace
              words={words}
              language={language}
              exportRoot={libraryRoot}
              roots={libraryRoots}
              scan={libraryScan}
              loading={libraryLoading}
              taskBusy={singleTask !== null || batchInvocationActive}
              archiveOpenDisabled={singleTask === "conversion"}
              repairingManifest={repairingManifest}
              repairingLibrary={repairingLibrary}
              importingArchives={importingArchives}
              notice={libraryNotice}
              query={libraryQuery}
              mapFilter={libraryMap}
              platformFilter={libraryPlatform}
              sort={librarySort}
              onQueryChange={setLibraryQuery}
              onMapFilterChange={setLibraryMap}
              onPlatformFilterChange={setLibraryPlatform}
              onSortChange={setLibrarySort}
              onAddRoot={() => void addLibraryRoot()}
              onRemoveRoot={removeLibraryRoot}
              onChooseExportRoot={() => void chooseLibraryRoot()}
              onRefresh={() => void scanLibrary(libraryRoots)}
              onImportArchives={() => void importArchives()}
              onRepairLibrary={() => void repairLibraryMetadata()}
              onConvert={() => void chooseDemos()}
              onOpenEntry={(entry: DemoLibraryEntry) => void runManifest(entry.manifestPath)}
              onInspectEntry={inspectLibraryEntry}
              onRepairEntry={(entry: DemoLibraryEntry) => void repairArchiveMetadata(entry)}
              onRevealManifest={(entry: DemoLibraryEntry) => void revealPath(entry.manifestPath)}
              onRevealDemo={(entry: DemoLibraryEntry) => void revealPath(entry.sourcePath || entry.demoPath)}
              onCopyDemoPath={(entry: DemoLibraryEntry) => void copyText(entry.sourcePath || entry.demoPath, "demoPath")}
              onReparseEntry={(entry: DemoLibraryEntry) => setReparseTarget({ kind: "library", entry })}
              onDeleteEntry={setArchiveDeleteTarget}
            />
            {activeSection === "batch" ? <BatchWorkspace
              words={words}
              language={language}
              notice={batchScanError}
              candidates={batchCandidates}
              selectedCandidateIds={batchSelectedIds}
              concurrency={batchConcurrency}
              runState={currentBatchRunState}
              startDisabled={singleTask !== null}
              canResume={canResumeBatch}
              jobs={batchJobs}
              summary={batchSummary}
              soundNotifications={localEnvironment.soundNotifications}
              exportCosmetics={batchCosmeticSettings.exportCosmetics}
              exportStickers={batchCosmeticSettings.exportStickers}
              exportCharms={batchCosmeticSettings.exportCharms}
              cosmeticOptionsLocked={batchCosmeticOptionsLocked}
              onChooseDemos={() => void chooseDemos()}
              onBack={leaveBatchWorkspace}
              onSelectionChange={setBatchSelectedIds}
              onConcurrencyChange={setBatchConcurrency}
              onRequestCosmetics={requestCosmeticExport}
              onCosmeticOptionsChange={updateSettings}
              onSoundNotificationsChange={(enabled) => {
                if (enabled) primeTaskSound(true);
                setLocalEnvironment((current) => ({ ...current, soundNotifications: enabled }));
              }}
              onStart={(candidateIds) => void startBatchImport(candidateIds)}
              onResume={() => void resumeBatchImport()}
              onStop={() => void stopBatchImport()}
              onFinish={() => finishBatchWorkspace(batchSummary.completed)}
              onRetryJob={(jobId) => void resumeBatchImport(jobId)}
              onOpenArchive={(job) => {
                if (job.outputPath) void runManifest(job.outputPath);
              }}
            /> : null}
          </>
        ) : (
          <>
        {phase === "openingArchive" ? <OpeningArchiveView words={words} manifestName={fileName(archivePath)} /> : null}
        {phase === "archive" && archive ? (
          <ArchiveWorkspace
            words={words}
            language={language}
            archive={archive}
            seriesEntries={activeArchiveSeries}
            busy={Boolean(repairingManifest)}
            savingNote={savingArchiveNote}
            selectedRound={selectedArchiveRound ?? -1}
            commandMode={commandMode}
            playbackPreset={playbackPreset}
            copiedTarget={copiedTarget}
            selectedPlayer={selectedPlayer}
            onSelectRound={(round) => {
              dispatchLibraryWorkspace({
                type: "selectRound",
                round,
                forceRoundMode: archive.rounds.find((item) => item.round === round)?.sequenceLength === 0,
              });
            }}
            onCommandModeChange={(mode) => dispatchLibraryWorkspace({ type: "setCommandMode", mode })}
            onPlaybackPresetChange={(patch) => setPlaybackPreset((current) => ({ ...current, ...patch }))}
            onCopy={(value, target) => void copyText(value, target)}
            onOpenExternal={(url) => void openExternal(url)}
            onSyncInventorySimulator={startInventorySimulatorBatch}
            onOpenFolder={() => void openPath(archive.root)}
            onSelectPlayer={(player) => dispatchLibraryWorkspace({ type: "selectPlayer", player })}
            onClosePlayer={closePlayerAnalysis}
            onSelectSeriesMap={(manifestPath) => {
              if (normalizedDiagnosticPath(manifestPath) !== normalizedDiagnosticPath(archive.manifestPath)) {
                void runManifest(manifestPath);
              }
            }}
            onBackToLibrary={() => dispatchLibraryWorkspace({ type: "navigate", section: "library" })}
            onReconvert={() => setReparseTarget({ kind: "archive", archive })}
            onSaveNote={saveArchiveNote}
            onChooseManifest={() => void chooseManifest()}
          />
        ) : null}
        {phase === "analysisFailed" ? (
          <AnalysisFailedView words={words} error={analysisError} retryButtonRef={retryButtonRef} onRetry={() => void runAnalysis(sourcePath)} onChangeDemo={() => void chooseDemo(sourcePath)} />
        ) : null}
        {selectingView}
        {phase === "validationFailed" ? (
          <ValidationFailedView words={words} error={validationError} outputRoot={outputRoot} onOpenFolder={() => void openPath(outputRoot)} onBack={() => setPhase("selecting")} />
        ) : null}
        {phase === "complete" && result ? (
          <ResultView
            words={words}
            language={language}
            result={result}
            warnings={conversionWarnings}
            copiedTarget={copiedTarget}
            resultHeadingRef={resultHeadingRef}
            onCopy={(value, target) => void copyText(value, target)}
            onOpenFolder={() => void openPath(result.root)}
            onBrowseManifest={() => void runManifest(result.manifestPath)}
            onBack={() => setPhase("selecting")}
            onNewDemo={resetSession}
            formatNumber={(value) => numberFormat.format(value)}
            formatBytes={formatBytes}
          />
        ) : null}
          </>
        )}
        {singleTask && singleTaskPanelOpen ? (
          <SingleTaskPanel
            words={words}
            task={singleTask}
            sourcePath={activeTaskSourcePath}
            elapsedSeconds={elapsedSeconds}
            progress={progress}
            outputRoot={outputRoot}
            cancelPending={analysisCancelPending}
            onCancelAnalysis={() => void cancelAnalysis()}
            onMinimize={() => setSingleTaskPanelOpen(false)}
          />
        ) : null}
        </main>
        <InventorySimulatorPanel
          available={inventorySimulatorPanelAvailable}
          hostRef={inventorySimulatorHostRef}
          language={language}
          open={inventorySimulatorPanelOpen}
          resizing={inventorySimulatorPanelResizing}
          width={inventorySimulatorPanelWidth}
          onCollapse={() => {
            setInventorySimulatorPanelResizing(false);
            setInventorySimulatorPanelOpen(false);
          }}
          onExpand={() => setInventorySimulatorPanelOpen(true)}
          onResizeEnd={() => setInventorySimulatorPanelResizing(false)}
          onResizeStart={() => setInventorySimulatorPanelResizing(true)}
          onWidthChange={(width) => setInventorySimulatorPanelWidth(normalizeInventorySimulatorPanelWidth(width))}
          onWidthReset={() => setInventorySimulatorPanelWidth(normalizeInventorySimulatorPanelWidth(
            INVENTORY_SIMULATOR_PANEL_DEFAULT_WIDTH,
          ))}
        />
      </div>

      {dragActive ? (
        <div className="drop-overlay" role="status">
          <FolderIcon size={24} />
          <strong>{words.dropDemo}</strong>
          <span>{words.dropTypes}</span>
        </div>
      ) : null}

      {guiUpdateDialogOpen ? (
        <DialogPrimitive
          labelledBy="gui-update-title"
          describedBy="gui-update-description"
          onDismiss={() => {
            if (guiUpdate.phase !== "downloading" && guiUpdate.phase !== "installing") {
              setGuiUpdateDialogOpen(false);
            }
          }}
          initialFocusRef={guiUpdateLaterRef}
          dismissOnScrimClick={false}
          className={`dialog-surface gui-update-dialog is-${guiUpdate.phase}`}
        >
          <header className="dialog-header update-dialog-header">
            <div className="update-dialog-heading">
              <span className="update-dialog-mark" aria-hidden="true"><ReplayIcon size={20} /></span>
              <div>
                <span className="dialog-eyebrow">{words.releaseUpdateStatus}</span>
                <h2 id="gui-update-title">{words.releaseUpdateTitle}</h2>
              </div>
            </div>
            <div className="update-dialog-header-actions">
              <span className={`update-dialog-state is-${guiUpdate.phase}`} role="status">
                <i aria-hidden="true" />{guiUpdateDialogStatus}
              </span>
              <button
                className="icon-button"
                type="button"
                disabled={guiUpdate.phase === "downloading" || guiUpdate.phase === "installing"}
                onClick={() => setGuiUpdateDialogOpen(false)}
                aria-label={words.close}
              >
                <CloseIcon size={16} />
              </button>
            </div>
          </header>

          <div className="update-dialog-version" aria-label={words.releaseUpdateTitle}>
            <div><span>{words.releaseCurrentVersion}</span><strong>v{guiUpdate.currentVersion || "—"}</strong></div>
            <ArrowIcon size={18} />
            <div><span>{words.releaseLatestVersion}</span><strong>v{guiUpdate.availableVersion || "—"}</strong></div>
          </div>

          <section className="update-dialog-release-notes" aria-labelledby="gui-update-notes-title">
            <h3 id="gui-update-notes-title">{words.releaseUpdateNotes}</h3>
            <p>{releaseNotesForLanguage(guiUpdate.notes, language) || words.releaseGenericNotes}</p>
          </section>

          <p id="gui-update-description" className="update-dialog-scope">{words.releaseUpdateScope}</p>

          {guiUpdate.phase === "downloading" || guiUpdate.phase === "installing" ? (
            <div className="update-dialog-progress" role="status" aria-live="polite">
              <div>
                <span>{guiUpdate.phase === "installing" ? words.releaseInstalling : words.releaseDownloading}</span>
                <strong>{guiUpdateDialogProgress != null ? `${guiUpdateDialogProgress}%` : "…"}</strong>
              </div>
              <div className={`release-progress${guiUpdateDialogProgress == null ? " is-indeterminate" : ""}`}>
                <span style={{ width: `${guiUpdateDialogProgress ?? 36}%` }} />
              </div>
            </div>
          ) : null}

          {guiUpdate.phase === "installing" ? (
            <p className="update-dialog-status" role="status">{words.releaseInstallingDesktop}</p>
          ) : null}
          {guiUpdate.phase === "error" ? <p className="release-error update-dialog-error"><AlertIcon size={15} />{words.releaseCheckUnavailable}</p> : null}

          <footer className="dialog-actions">
            <button
              ref={guiUpdateLaterRef}
              className="secondary-button"
              type="button"
              disabled={guiUpdate.phase === "downloading" || guiUpdate.phase === "installing"}
              onClick={() => setGuiUpdateDialogOpen(false)}
            >
              {words.releaseLater}
            </button>
            <button
              className="primary-button"
              type="button"
              disabled={guiUpdate.phase === "checking" || guiUpdate.phase === "downloading" || guiUpdate.phase === "installing"}
              onClick={() => {
                if (guiUpdate.phase === "error") void checkGuiApplicationUpdate();
                else void installGuiApplicationUpdate();
              }}
            >
              {guiUpdate.phase === "checking" ? <RefreshIcon className="release-spin" size={15} /> : <ReplayIcon size={15} />}
              {guiUpdate.phase === "checking" ? words.releaseChecking
                : guiUpdate.phase === "downloading" ? words.releaseDownloading
                  : guiUpdate.phase === "installing" ? words.releaseInstalling
                    : guiUpdate.phase === "error" ? words.releaseCheckNow
                      : words.releaseInstallNow}
            </button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {overwriteConflict ? (
        <DialogPrimitive labelledBy="overwrite-title" describedBy="overwrite-description" onDismiss={() => setOverwriteConflict(null)} initialFocusRef={chooseOtherOutputRef} dismissOnScrimClick={false}>
          <header className="dialog-header">
            <h2 id="overwrite-title">{words.overwriteTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setOverwriteConflict(null)} aria-label={words.close}><CloseIcon size={16} /></button>
          </header>
          <p id="overwrite-description" className="dialog-description">{words.overwriteBody}</p>
          <code className="dialog-path">{overwriteConflict.root}</code>
          <button className="text-button dialog-inline-action" type="button" onClick={() => void openPath(overwriteConflict.root)}><FolderIcon size={15} />{words.openExisting}</button>
          <footer className="dialog-actions three-actions">
            <button className="secondary-button" type="button" onClick={() => setOverwriteConflict(null)}>{words.cancel}</button>
            <button ref={chooseOtherOutputRef} className="secondary-button" type="button" onClick={() => {
              setOverwriteConflict(null);
              void chooseOutput();
            }}>{words.chooseAnotherOutput}</button>
            <button
              className="danger-button"
              type="button"
              disabled={conversionStartPending}
              onClick={() => void runConversionStartExclusive(() => performConvert(true))}
            >{words.replaceAndConvert}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {duplicateDemoConflict ? (
        <DialogPrimitive
          labelledBy="duplicate-demo-title"
          describedBy="duplicate-demo-description"
          onDismiss={() => setDuplicateDemoConflict(null)}
          initialFocusRef={openExistingArchiveRef}
          dismissOnScrimClick={false}
        >
          <header className="dialog-header">
            <h2 id="duplicate-demo-title">{words.duplicateDemoTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setDuplicateDemoConflict(null)} aria-label={words.close}><CloseIcon size={16} /></button>
          </header>
          <p id="duplicate-demo-description" className="dialog-description">
            {duplicateDemoConflict.batch
              ? (language === "zh"
                ? `本次选择的 ${duplicateDemoConflict.batch.selections.length} 个 Demo 中有 ${duplicateDemoConflict.batch.replaceSourceIds.length} 个已经存在。重新分析会保留完整队列，并逐个替换这些既有档案。`
                : `${duplicateDemoConflict.batch.replaceSourceIds.length} of the ${duplicateDemoConflict.batch.selections.length} selected demos already exist. Analyzing again keeps the full queue and replaces those archives individually.`)
              : words.duplicateDemoBody}
            {!duplicateDemoConflict.batch && duplicateDemoConflict.primary.matches.length > 1
              ? ` ${words.duplicateDemoMatchCount.replace("{count}", String(duplicateDemoConflict.primary.matches.length))}`
              : ""}
          </p>
          <strong className="dialog-target-name">
            {duplicateDemoConflict.primary.matches[0].displayName
              || fileName(duplicateDemoConflict.primary.matches[0].root)
              || duplicateDemoConflict.primary.matches[0].demoId}
          </strong>
          <code className="dialog-path">{duplicateDemoConflict.primary.matches[0].root}</code>
          <footer className="dialog-actions three-actions">
            <button className="secondary-button" type="button" onClick={() => setDuplicateDemoConflict(null)}>{words.cancel}</button>
            <button
              className={duplicateDemoConflict.batch ? "danger-button" : "secondary-button"}
              type="button"
              onClick={() => {
                const conflict = duplicateDemoConflict;
                setDuplicateDemoConflict(null);
                if (conflict.batch) {
                  stageBatchSelections(
                    conflict.batch.selections,
                    conflict.batch.replaceSourceIds,
                    conflict.batch.mergedSegments,
                    conflict.batch.relinkedDuplicates,
                  );
                } else {
                  void runAnalysis(conflict.primary.sourcePath);
                }
              }}
            >
              {duplicateDemoConflict.batch
                ? (language === "zh"
                  ? `重新分析 ${duplicateDemoConflict.batch.selections.length} 个 Demo`
                  : `Analyze ${duplicateDemoConflict.batch.selections.length} demos again`)
                : words.analyzeAgain}
            </button>
            <button ref={openExistingArchiveRef} className="primary-button" type="button" onClick={() => {
              const manifestPath = duplicateDemoConflict.primary.matches[0].manifestPath;
              setDuplicateDemoConflict(null);
              void runManifest(manifestPath);
            }}>{words.openExistingArchive}<ArrowIcon size={15} /></button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {archiveDeleteTarget ? (
        <DialogPrimitive
          labelledBy="delete-archive-title"
          describedBy="delete-archive-description"
          onDismiss={() => { if (!deletingManifest) setArchiveDeleteTarget(null); }}
          initialFocusRef={cancelArchiveDeleteRef}
          dismissOnScrimClick={false}
        >
          <header className="dialog-header warning-header">
            <span><AlertIcon size={18} /></span>
            <h2 id="delete-archive-title">{words.deleteArchiveTitle}</h2>
            <button className="icon-button" type="button" disabled={Boolean(deletingManifest)} onClick={() => setArchiveDeleteTarget(null)} aria-label={words.close}><CloseIcon size={16} /></button>
          </header>
          <p id="delete-archive-description" className="dialog-description">{words.deleteArchiveBody}</p>
          <strong className="dialog-target-name">{archiveDeleteTarget.displayName || fileName(archiveDeleteTarget.root) || archiveDeleteTarget.demoId}</strong>
          <footer className="dialog-actions">
            <button ref={cancelArchiveDeleteRef} className="secondary-button" type="button" disabled={Boolean(deletingManifest)} onClick={() => setArchiveDeleteTarget(null)}>{words.cancel}</button>
            <button className="danger-button" type="button" disabled={Boolean(deletingManifest)} onClick={() => void deleteArchiveEntry(archiveDeleteTarget)}>{deletingManifest ? words.deletingArchive : words.deleteArchive}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {reparseTarget ? (
        <DialogPrimitive
          labelledBy="reparse-title"
          describedBy="reparse-description"
          onDismiss={() => setReparseTarget(null)}
          dismissOnScrimClick={false}
        >
          <header className="dialog-header warning-header">
            <span><AlertIcon size={18} /></span>
            <h2 id="reparse-title">{words.reparseConfirmTitle}</h2>
          </header>
          <p id="reparse-description" className="dialog-description">{words.reparseConfirmBody}</p>
          <footer className="dialog-actions">
            <button className="secondary-button" type="button" onClick={() => setReparseTarget(null)}>{words.cancel}</button>
            <button className="danger-button" type="button" onClick={confirmReparse}>{words.reparseConfirmAction}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {cosmeticOpen ? (
        <DialogPrimitive labelledBy="cosmetic-title" describedBy="cosmetic-description" onDismiss={() => setCosmeticOpen(false)} initialFocusRef={cosmeticInputRef} dismissOnScrimClick={false} className="dialog-surface cosmetic-dialog">
          <header className="dialog-header warning-header">
            <span><AlertIcon size={18} /></span>
            <h2 id="cosmetic-title">{words.cosmeticTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setCosmeticOpen(false)} aria-label={words.close}><CloseIcon size={16} /></button>
          </header>
          <p id="cosmetic-description" className="dialog-description">{words.cosmeticBody}</p>
          <div className="phrase-field">
            <label htmlFor="cosmetic-confirmation-phrase">{words.typePhrase}</label>
            <button className="phrase-copy-button" type="button" onClick={() => void copyText(COSMETIC_PHRASE, "phrase")} aria-label={words.copyPhrase}>
              <code>{COSMETIC_PHRASE}</code>
              <span>{copiedTarget === "phrase" ? <CheckIcon size={14} /> : <CopyIcon size={14} />}{copiedTarget === "phrase" ? words.copied : words.copyPhrase}</span>
            </button>
            <input id="cosmetic-confirmation-phrase" ref={cosmeticInputRef} autoComplete="off" spellCheck={false} value={cosmeticPhrase} onChange={(event) => setCosmeticPhrase(event.target.value)} />
            <small>{words.phraseCaseSensitive}</small>
          </div>
          <footer className="dialog-actions">
            <button className="secondary-button" type="button" onClick={() => setCosmeticOpen(false)}>{words.cancel}</button>
            <button className="primary-button" type="button" disabled={!consentIsValid(cosmeticPhrase)} onClick={() => {
              setCosmeticConsentAccepted(true);
              setSettings((current) => ({ ...current, exportCosmetics: true }));
              setCosmeticPhrase("");
              setCosmeticOpen(false);
            }}>{words.enableCosmetics}<ArrowIcon size={15} /></button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {closeOpen ? (
        <DialogPrimitive labelledBy="close-task-title" describedBy="close-task-description" onDismiss={() => setCloseOpen(false)} initialFocusRef={keepWorkingRef} dismissOnScrimClick={false}>
          <header className="dialog-header warning-header">
            <span><AlertIcon size={18} /></span>
            <h2 id="close-task-title">{words.closeTaskTitle}</h2>
          </header>
          <p id="close-task-description" className="dialog-description">{words.closeTaskBody}</p>
          <footer className="dialog-actions">
            <button ref={keepWorkingRef} className="primary-button" type="button" onClick={() => setCloseOpen(false)}>{words.keepWorking}</button>
            <button className="danger-button" type="button" onClick={() => void exitDesktopApp()}>{words.closeAnyway}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      <span className="sr-only" role="status" aria-live="polite">{liveMessage}</span>
    </div>
  );
}

export default App;
