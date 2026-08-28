/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Channel, invoke } from "@tauri-apps/api/core";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { exit as exitApp } from "@tauri-apps/plugin-process";
import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type CSSProperties } from "react";
import {
  DEFAULT_SETTINGS,
  INITIAL_LIBRARY_PREFERENCES,
  BATCH_PREFERENCES_STORAGE_KEY,
  COSMETIC_CONSENT_STORAGE_KEY,
  INVENTORY_SIMULATOR_PANEL_DEFAULT_WIDTH,
  INITIAL_LIBRARY_SESSION,
  type StoredBatchPreferences,
  type BatchItemProgress,
  type DemoPreflightProgress,
  type DuplicateDemoConflictState,
  type SaveArchiveNoteResult,
  type ReparseTarget,
  type InventorySimulatorPanelBounds,
  measureInventorySimulatorPanel,
  normalizeInventorySimulatorPanelWidth,
  storedInventorySimulatorPanelWidth,
  storedBatchPreferences,
  batchJobPhase,
  batchRunState,
  nextBatchItemProgress,
  emptyProgress,
  storedLanguage,
  storedUiFontSize,
  storedCosmeticConsent,
  storedSettings,
  storedPlaybackPreset,
  storedLocalEnvironment,
  ENVIRONMENT_REPORT_STORAGE_KEY,
  type StoredEnvironmentReport,
  normalizedDiagnosticPath,
  storedEnvironmentReport,
  fileName,
  isDemoFilePath,
  commonParentDirectory,
  formatBytes,
  parseCommandError,
  userFacingErrorMessage,
  userFacingErrorTitle,
  phaseFromBackend,
  useElapsed,
  useMediaQuery,
  loadCustomCssProfiles,
} from "./appSupport";
import { AppChrome, AppSidebar } from "./components/AppChrome";
import {
  CloseTaskDialog,
  CosmeticConsentDialog,
  DeleteArchiveDialog,
  DuplicateDemoDialog,
  OverwriteDialog,
  ReparseDialog,
  UpdateDialog,
} from "./components/AppDialogs";
import { activeBatchItemCount, findRestorableBatch } from "./batchSession";
import { ArchiveWorkspace } from "./components/ArchiveWorkspace";
import type { InventorySimulatorItem } from "./inventorySimulator";
import {
  BATCH_SELECTION_LIMIT,
  BatchWorkspace,
  type BatchConcurrency,
  type BatchJobItem,
  type BatchScanCandidate,
} from "./components/BatchWorkspace";
import { ExportInspector } from "./components/ExportInspector";
import { FaqWorkspace } from "./components/FaqWorkspace";
import { LibraryWorkspace, type LibrarySort } from "./components/LibraryWorkspace";
import { LogsWorkspace } from "./components/LogsWorkspace";
import { InventorySimulatorPanel } from "./components/InventorySimulatorPanel";
import type { PlaybackPresetOptions } from "./components/PlaybackCommandBuilder";
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
import { AlertIcon, ArrowIcon, CloseIcon, FolderIcon } from "./icons";
import { COSMETIC_PHRASE, TEXT } from "./i18n";
import { useActivityLogController } from "./hooks/useActivityLogController";
import { useAppearanceRuntime } from "./hooks/useAppearanceRuntime";
import { useGuiPreferencesPersistence } from "./hooks/useGuiPreferencesPersistence";
import { useUpdateController } from "./hooks/useUpdateController";
import {
  AGGREGATE_TELEMETRY_STORAGE_KEY,
  PRESENCE_TELEMETRY_CONSENT_STORAGE_KEY,
  storedAggregateTelemetryEnabled,
  storedPresenceTelemetryConsent,
  telemetryDemoSource,
  telemetryDurationBucket,
  telemetryRoundsBucket,
  type TelemetryPresenceConsent,
  type TelemetrySubmission,
} from "./telemetry";
import {
  ACTIVE_CUSTOM_CSS_PROFILE_STORAGE_KEY,
  CUSTOM_CSS_STORAGE_KEY,
  normalizeActiveCustomCssProfileId,
  normalizeCustomCss,
  normalizeCustomCssProfiles,
  normalizeSidebarCollapsed,
  normalizeTheme,
  normalizeThemeCustomization,
  resolveTheme,
  SIDEBAR_COLLAPSED_STORAGE_KEY,
  THEME_CUSTOMIZATION_STORAGE_KEY,
  THEME_STORAGE_KEY,
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
  uniqueLibraryRoots,
  withExportRoot,
} from "./library";
import {
  buildArchiveSessionMeta,
  EMPTY_LIBRARY_WORKSPACE,
  LIBRARY_SESSION_STORAGE_KEY,
  libraryWorkspaceReducer,
  writeStoredLibrarySession,
  type StoredLibrarySession,
} from "./librarySession";
import type {
  AnalysisResult,
  BatchEvent,
  BatchItem,
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
  ImportArchivesResult,
  Language,
  LocalEnvironmentSettings,
  ManifestArchive,
  OutputPreflight,
  Phase,
  ProgressState,
  RefreshArchiveMetadataResult,
  RefreshLibraryMetadataResult,
  ResolveArchiveSourceResult,
  RoundInfo,
  SaveServerConfigResult,
  ServerConfigDocument,
  ServerConfigValidation,
  TaskEvent,
  Theme,
  WorkspaceBackground,
} from "./types";

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
  const [workspaceBackground, setWorkspaceBackground] = useState<WorkspaceBackground | null>(null);
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
  const [aggregateTelemetryEnabled, setAggregateTelemetryEnabled] = useState(storedAggregateTelemetryEnabled);
  const [presenceTelemetryConsent, setPresenceTelemetryConsent] = useState<TelemetryPresenceConsent>(
    storedPresenceTelemetryConsent,
  );
  const [installCandidates, setInstallCandidates] = useState<Cs2InstallCandidate[]>([]);
  const [installDetectionCompleted, setInstallDetectionCompleted] = useState(false);
  const [environmentReport, setEnvironmentReport] = useState<EnvironmentDiagnosticReport | null>(
    () => storedEnvironmentReport(storedLocalEnvironment().cs2Path),
  );
  const [detectingInstallations, setDetectingInstallations] = useState(false);
  const [inspectingEnvironment, setInspectingEnvironment] = useState(false);
  const [serverConfigDocument, setServerConfigDocument] = useState<ServerConfigDocument | null>(null);
  const [serverConfigDraft, setServerConfigDraft] = useState("");
  const [serverConfigValidation, setServerConfigValidation] = useState<ServerConfigValidation | null>(null);
  const [loadingServerConfig, setLoadingServerConfig] = useState(false);
  const [savingServerConfig, setSavingServerConfig] = useState(false);
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
  const {
    entries: activityLogs,
    loading: activityLogsLoading,
    range: activityLogRange,
    setRange: setActivityLogRange,
    gsiStatus: gsiRuntimeStatus,
    record: recordActivityLog,
    refresh: refreshActivityLogs,
    openDirectory: openActivityLogDirectory,
    clear: clearActivityLogs,
  } = useActivityLogController({
    language,
    logsActive: activeSection === "logs",
    cs2Path: localEnvironment.cs2Path,
    onError: setGlobalError,
  });

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
  const updateLaterRef = useRef<HTMLButtonElement | null>(null);
  const cancelArchiveDeleteRef = useRef<HTMLButtonElement | null>(null);
  const inventorySimulatorHostRef = useRef<HTMLDivElement | null>(null);
  const systemDark = useMediaQuery("(prefers-color-scheme: dark)");
  const resolvedTheme = resolveTheme(theme, systemDark);
  const {
    appVersion,
    guiUpdate,
    dialogOpen: updateDialogOpen,
    setDialogOpen: setUpdateDialogOpen,
    promptDismissed: updatePromptDismissed,
    ignoredVersions: ignoredUpdateVersions,
    playbackRelease,
    playbackUpdate,
    playbackReleaseError,
    playbackInstallBlockedByCs2,
    releaseAction,
    playbackInstallProgress,
    releaseNotice,
    actionableUpdateAvailable,
    guiUpdateAvailable,
    guiUpdateRetryRequired,
    guiUpdateOffered,
    playbackUpdateOffered,
    availableUpdateCount,
    promptTitle: updatePromptTitle,
    promptSummary: updatePromptSummary,
    dialogBusy: updateDialogBusy,
    dialogStatus: updateDialogStatus,
    dialogProgressActive: updateDialogProgressActive,
    dialogProgress: updateDialogProgress,
    playbackInstallStatus,
    dismissPrompt: dismissUpdatePrompt,
    ignoreAvailableVersions: ignoreAvailableUpdateVersions,
    installAvailableUpdates,
    reviewGuiUpdate,
    checkGuiApplicationUpdate,
    checkPlaybackUpdate,
    installLatestPlaybackBundle,
    installPlaybackBundle,
    rollbackPlaybackInstall,
  } = useUpdateController({
    language,
    cs2Path: localEnvironment.cs2Path,
    onInspectEnvironment: runEnvironmentInspection,
  });

  useGuiPreferencesPersistence({
    language,
    setLanguage,
    theme,
    setTheme,
    uiFontSize,
    setUiFontSize,
    sidebarCollapsed,
    setSidebarCollapsed,
    themeCustomization,
    setThemeCustomization,
    customCssProfiles,
    setCustomCssProfiles,
    activeCustomCssProfileId,
    setActiveCustomCssProfileId,
    onError: setGlobalError,
  });

  useAppearanceRuntime({
    language,
    theme,
    resolvedTheme,
    sidebarCollapsed,
    ignoredUpdateVersions,
    uiFontSize,
    setUiFontSize,
    themeCustomization,
    customCssProfiles,
    activeCustomCssProfileId,
    setWorkspaceBackground,
    inventoryPanelAvailable: inventorySimulatorPanelAvailable,
    inventoryPanelOpen: inventorySimulatorPanelOpen,
    inventoryPanelResizing: inventorySimulatorPanelResizing,
    inventoryPanelWidth: inventorySimulatorPanelWidth,
    setInventoryPanelWidth: setInventorySimulatorPanelWidth,
    inventoryPanelHostRef: inventorySimulatorHostRef,
    onError: setGlobalError,
  });

  const invalidateManifestCache = useCallback((path?: string) => {
    manifestCacheGenerationRef.current += 1;
    if (path) manifestCacheRef.current.delete(normalizedDiagnosticPath(path));
    else manifestCacheRef.current.clear();
  }, []);

  const words = TEXT[language];
  const chooseWorkspaceBackground = useCallback(async () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    try {
      const selected = await invoke<WorkspaceBackground | null>("choose_workspace_background", {
        request: {
          title: TEXT[language].workspaceBackgroundChoose,
          filterLabel: TEXT[language].workspaceBackgroundPngFilter,
        },
      });
      if (selected) setWorkspaceBackground(selected);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }, [language]);
  const clearWorkspaceBackground = useCallback(async () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    try {
      await invoke<boolean>("clear_workspace_background");
      setWorkspaceBackground(null);
    } catch (reason) {
      setGlobalError(parseCommandError(reason));
    }
  }, []);
  const presenceTelemetryEnabled = presenceTelemetryConsent === "enabled";
  const submitTelemetry = useCallback((submission: TelemetrySubmission) => {
    const enabled = submission.kind === "session" ? presenceTelemetryEnabled : aggregateTelemetryEnabled;
    if (!enabled || !("__TAURI_INTERNALS__" in window)) return;
    void invoke<void>("submit_telemetry", {
      event: {
        playbackVersion: playbackRelease?.currentVersion ?? null,
        demoSource: "unknown",
        errorCode: "-",
        roundsBucket: "unknown",
        durationBucket: "unknown",
        ...submission,
      },
    }).catch(() => undefined);
  }, [aggregateTelemetryEnabled, playbackRelease?.currentVersion, presenceTelemetryEnabled]);
  const libraryRoot = libraryPreferences.exportRoot;
  const libraryRoots = libraryPreferences.roots;
  const numberFormat = useMemo(() => new Intl.NumberFormat(language === "zh" ? "zh-CN" : "en-US"), [language]);
  const isRepairing = repairingLibrary || Boolean(repairingManifest);
  const isMaintainingLibrary = isRepairing || importingArchives || Boolean(deletingManifest);
  const isBusy = singleTask !== null || phase === "openingArchive" || isMaintainingLibrary || batchInvocationActive || demoPreflightActive || conversionStartPending;
  isBusyRef.current = isBusy;
  const inspectorVisible = analysis !== null
    && (phase === "selecting" || singleTask === "conversion");
  const elapsedSeconds = useElapsed(singleTask === "analysis");
  const sourceFileName = analysis?.fileName || fileName(sourcePath);
  const analysisSessionTitle = activeSection === "analysis" && phase === "archive" && archive
    ? archive.displayName || fileName(archive.demoPath) || archive.demoId
    : activeSection === "analysis" ? sourceFileName : "";
  const analysisSessionMeta = activeSection === "analysis" && phase === "archive" && archive
    ? buildArchiveSessionMeta(
      analysisSessionTitle,
      fileName(archive.sourcePath || archive.demoPath),
      archive.map,
      archive.rounds.length,
      words.rounds,
    )
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
          ? words.batchCandidateReplacing
          : imported
            ? words.batchCandidateImported
            : null,
      };
    });
  }, [batchReplaceSources, batchScan, importedBatchSources, words]);
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
    localStorage.setItem(
      AGGREGATE_TELEMETRY_STORAGE_KEY,
      aggregateTelemetryEnabled ? "enabled" : "disabled",
    );
    if (presenceTelemetryConsent !== "unknown") {
      localStorage.setItem(PRESENCE_TELEMETRY_CONSENT_STORAGE_KEY, presenceTelemetryConsent);
    }
    if (!("__TAURI_INTERNALS__" in window)) return;

    let disposed = false;
    let timer: number | undefined;
    void invoke<void>("configure_telemetry", {
      aggregateEnabled: aggregateTelemetryEnabled,
      presenceEnabled: presenceTelemetryEnabled,
    }).then(() => {
      if (disposed || !presenceTelemetryEnabled) return;
      submitTelemetry({ kind: "session", outcome: "ping" });
      timer = window.setInterval(() => {
        submitTelemetry({ kind: "session", outcome: "ping" });
      }, 5 * 60_000);
    }).catch(() => undefined);
    return () => {
      disposed = true;
      if (timer != null) window.clearInterval(timer);
    };
  }, [aggregateTelemetryEnabled, presenceTelemetryConsent, presenceTelemetryEnabled, submitTelemetry]);



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
        const firstRound = document.querySelector<HTMLInputElement>('.round-mantine-table input[data-round-select="true"]:not(:disabled)');
        const suspiciousToggle = document.querySelector<HTMLInputElement>(".round-suspicious-switch input");
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

    const telemetryStartedAt = Date.now();

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
      submitTelemetry({
        kind: "analysis",
        outcome: "success",
        demoSource: telemetryDemoSource(next.demoSource),
        roundsBucket: telemetryRoundsBucket(next.rounds.length),
        durationBucket: telemetryDurationBucket(Date.now() - telemetryStartedAt),
      });
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
      submitTelemetry({
        kind: "analysis",
        outcome: "failure",
        errorCode: error.code,
        durationBucket: telemetryDurationBucket(Date.now() - telemetryStartedAt),
      });
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
  }, [absorbEvent, language, libraryRoot, playTaskSound, primeTaskSound, recordActivityLog, settings.maxRoundSeconds, submitTelemetry, words.invalidDemo]);

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
        ? words.batchNoticeReplacing.replace("{count}", String(replaceSourceIds.length))
        : "",
      mergedSegments > 0
        ? words.batchNoticeMergedSegments.replace("{count}", String(mergedSegments))
        : "",
      relinkedDuplicates > 0
        ? words.batchNoticeRelinked.replace("{count}", String(relinkedDuplicates))
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
        message: words.demoSelectionInvalid,
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

  async function saveArchiveNote(manifestPath: string, note: string): Promise<boolean> {
    if (!manifestPath || savingArchiveNote) return false;
    setSavingArchiveNote(true);
    setGlobalError(null);
    try {
      const saved = await invoke<SaveArchiveNoteResult>("save_archive_note", {
        request: { manifestPath, note },
      });
      if (archive && normalizedDiagnosticPath(archive.manifestPath) === normalizedDiagnosticPath(saved.manifestPath)) {
        const updated = { ...archive, note: saved.note };
        dispatchLibraryWorkspace({ type: "replaceArchive", archive: updated });
        manifestCacheRef.current.set(normalizedDiagnosticPath(updated.manifestPath), updated);
      } else {
        const cacheKey = normalizedDiagnosticPath(saved.manifestPath);
        const cached = manifestCacheRef.current.get(cacheKey);
        if (cached) manifestCacheRef.current.set(cacheKey, { ...cached, note: saved.note });
      }
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
        submitTelemetry({
          kind: "analysis",
          outcome: "success",
          demoSource: telemetryDemoSource(event.demoSource),
          roundsBucket: telemetryRoundsBucket(event.roundsExported),
        });
        submitTelemetry({
          kind: "conversion",
          outcome: "success",
          roundsBucket: telemetryRoundsBucket(event.roundsExported),
          durationBucket: telemetryDurationBucket(
            Date.now() - (batchProgressByItem[event.itemId]?.startedAtMs ?? Date.now()),
          ),
        });
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
        submitTelemetry({
          kind: "conversion",
          outcome: "failure",
          errorCode: event.error.code,
          durationBucket: telemetryDurationBucket(
            Date.now() - (batchProgressByItem[event.itemId]?.startedAtMs ?? Date.now()),
          ),
        });
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
        message: words.batchSelectionChanged,
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
      setLibraryNotice(words.batchImportCompleted.replace("{count}", String(completed)));
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
          message: words.demoDropInvalid,
        });
        return;
      }
      void prepareDemoSelections(paths);
    }).then((stop) => { unlisten = stop; });
    return () => unlisten?.();
  }, [isBusy, prepareDemoSelections, runManifest, words]);

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
    const telemetryStartedAt = Date.now();
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
      submitTelemetry({
        kind: "conversion",
        outcome: "success",
        roundsBucket: telemetryRoundsBucket(summary.roundsExported),
        durationBucket: telemetryDurationBucket(Date.now() - telemetryStartedAt),
      });
      playTaskSound("success");
    } catch (reason) {
      if (token !== taskTokenRef.current) return;
      const error = parseCommandError(reason);
      submitTelemetry({
        kind: "conversion",
        outcome: "failure",
        errorCode: error.code,
        durationBucket: telemetryDurationBucket(Date.now() - telemetryStartedAt),
      });
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
        analysis={analysis}
        selectedRounds={selectedRounds}
        allowSuspicious={settings.includeSuspicious}
        convertPending={conversionStartPending || singleTask === "conversion"}
        onToggleRound={toggleRound}
        onRestoreRecommended={restoreRecommended}
        onClearSelection={() => setSelectedRounds(new Set())}
        onAllowSuspiciousChange={handleAllowSuspicious}
        formatNumber={(value) => numberFormat.format(value)}
      />
      {inspectorVisible ? (
        <ExportInspector
          words={words}
          settings={settings}
          selectedRoundCount={selectedRounds.size}
          outputDir={outputDir}
          outputRoot={outputRoot}
          disabled={conversionStartPending || singleTask === "conversion"}
          onChange={updateSettings}
          onRequestCosmetics={requestCosmeticExport}
          onRestoreDefaults={restoreDefaultSettings}
          onChooseOutput={() => void chooseOutput()}
          onConvert={() => void beginConvert()}
        />
      ) : null}
    </div>
  ) : null;


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

      <div
        className="app-body"
        data-has-workspace-background={workspaceBackground ? "true" : undefined}
        style={workspaceBackground
          ? ({ "--workspace-background-image": `url(${workspaceBackground.dataUrl})` } as CSSProperties)
          : undefined}
      >
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
          updateAvailable={actionableUpdateAvailable}
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
        {actionableUpdateAvailable && !updatePromptDismissed ? (
          <aside className="update-discovery-banner" aria-labelledby="update-banner-title">
            <span className="update-discovery-mark" aria-hidden="true"><ArrowIcon size={16} /></span>
            <div className="update-discovery-copy">
              <strong id="update-banner-title">{updatePromptTitle}</strong>
              <span>{updatePromptSummary}</span>
            </div>
            <button className="secondary-button update-discovery-action" type="button" onClick={() => setUpdateDialogOpen(true)}>
              {words.releaseReviewUpdate}
            </button>
            <button className="icon-button update-discovery-dismiss" type="button" onClick={dismissUpdatePrompt} aria-label={words.releaseLater} title={words.releaseLater}>
              <CloseIcon size={14} />
            </button>
          </aside>
        ) : null}
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
            <strong>{words.batchImportTask}</strong>
            <code>{batchInvocationActive
              ? words.batchRemaining.replace("{count}", String(batchActiveCount))
              : hasRetryableBatchJobs
                ? words.batchNeedAttention.replace("{count}", String(batchSummary.failed))
                : words.batchReadyToResume.replace("{count}", String(batchActiveCount))}</code>
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
            range={activityLogRange}
            onRangeChange={setActivityLogRange}
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
            workspaceBackground={workspaceBackground}
            customCssProfiles={customCssProfiles}
            activeCustomCssProfileId={activeCustomCssProfileId}
            environment={localEnvironment}
            aggregateTelemetryEnabled={aggregateTelemetryEnabled}
            presenceTelemetryEnabled={presenceTelemetryEnabled}
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
            updateAvailable={actionableUpdateAvailable}
            playbackRelease={playbackRelease}
            playbackUpdate={playbackUpdate}
            playbackReleaseError={playbackReleaseError}
            releaseAction={releaseAction}
            playbackInstallProgress={playbackInstallProgress}
            releaseNotice={releaseNotice}
            onUiFontSizeChange={setUiFontSize}
            onThemeCustomizationChange={setThemeCustomization}
            onChooseWorkspaceBackground={() => void chooseWorkspaceBackground()}
            onClearWorkspaceBackground={() => void clearWorkspaceBackground()}
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
            onInstallGuiUpdate={reviewGuiUpdate}
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
            onAggregateTelemetryEnabledChange={setAggregateTelemetryEnabled}
            onPresenceTelemetryEnabledChange={(enabled) => {
              setPresenceTelemetryConsent(enabled ? "enabled" : "disabled");
            }}
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
              onCopyManifestPath={(entry: DemoLibraryEntry) => void copyText(entry.manifestPath, "manifest")}
              onCopyDemoPath={(entry: DemoLibraryEntry) => void copyText(entry.sourcePath || entry.demoPath, "demoPath")}
              onSaveNote={(entry, note) => saveArchiveNote(entry.manifestPath, note)}
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
            busy={Boolean(repairingManifest) || singleTask !== null}
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
        {demoPreflightProgress ? (
          <SingleTaskPanel
            words={words}
            task="preflight"
            sourcePath={demoPreflightProgress.fileName}
            elapsedSeconds={0}
            progress={progress}
            outputRoot=""
            cancelPending={false}
            preflightProgress={demoPreflightProgress}
            onCancelAnalysis={() => undefined}
            onMinimize={() => undefined}
          />
        ) : singleTask && singleTaskPanelOpen ? (
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

      {presenceTelemetryConsent === "unknown" ? (
        <aside
          className="telemetry-choice-toast"
          role="dialog"
          aria-labelledby="telemetry-choice-title"
          aria-describedby="telemetry-choice-description"
        >
          <div className="telemetry-choice-copy">
            <strong id="telemetry-choice-title">{words.telemetryChoiceTitle}</strong>
            <span id="telemetry-choice-description">{words.telemetryChoiceBody}</span>
          </div>
          <div className="telemetry-choice-actions">
            <button className="secondary-button" type="button" onClick={() => setPresenceTelemetryConsent("disabled")}>
              {words.telemetryPresenceKeepOff}
            </button>
            <button className="primary-button" type="button" onClick={() => setPresenceTelemetryConsent("enabled")}>
              {words.telemetryPresenceAllow}
            </button>
          </div>
          <button
            className="icon-button telemetry-choice-dismiss"
            type="button"
            onClick={() => setPresenceTelemetryConsent("disabled")}
            aria-label={words.dismiss}
          >
            <CloseIcon size={14} />
          </button>
        </aside>
      ) : null}

      {updateDialogOpen ? (
        <UpdateDialog
          words={words}
          language={language}
          busy={updateDialogBusy}
          status={updateDialogStatus}
          playbackInstallBlockedByCs2={playbackInstallBlockedByCs2}
          availableUpdateCount={availableUpdateCount}
          guiUpdateOffered={guiUpdateOffered}
          playbackUpdateOffered={playbackUpdateOffered}
          guiUpdate={guiUpdate}
          playbackRelease={playbackRelease}
          playbackUpdate={playbackUpdate}
          progressActive={updateDialogProgressActive}
          progress={updateDialogProgress}
          playbackInstallStatus={playbackInstallStatus}
          playbackReleaseError={playbackReleaseError}
          releaseAction={releaseAction}
          guiUpdateRetryRequired={guiUpdateRetryRequired}
          guiUpdateAvailable={guiUpdateAvailable}
          initialFocusRef={updateLaterRef}
          onDismiss={dismissUpdatePrompt}
          onIgnore={ignoreAvailableUpdateVersions}
          onInstall={() => void installAvailableUpdates()}
        />
      ) : null}

      {overwriteConflict ? (
        <OverwriteDialog
          words={words}
          conflict={overwriteConflict}
          conversionStartPending={conversionStartPending}
          initialFocusRef={chooseOtherOutputRef}
          onDismiss={() => setOverwriteConflict(null)}
          onOpenExisting={() => void openPath(overwriteConflict.root)}
          onChooseAnother={() => {
            setOverwriteConflict(null);
            void chooseOutput();
          }}
          onReplace={() => void runConversionStartExclusive(() => performConvert(true))}
        />
      ) : null}

      {duplicateDemoConflict ? (
        <DuplicateDemoDialog
          words={words}
          conflict={duplicateDemoConflict}
          initialFocusRef={openExistingArchiveRef}
          onDismiss={() => setDuplicateDemoConflict(null)}
          onAnalyzeAgain={(conflict) => {
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
          onOpenExisting={(manifestPath) => {
            setDuplicateDemoConflict(null);
            void runManifest(manifestPath);
          }}
        />
      ) : null}

      {archiveDeleteTarget ? (
        <DeleteArchiveDialog
          words={words}
          target={archiveDeleteTarget}
          deleting={Boolean(deletingManifest)}
          initialFocusRef={cancelArchiveDeleteRef}
          onDismiss={() => setArchiveDeleteTarget(null)}
          onDelete={() => void deleteArchiveEntry(archiveDeleteTarget)}
        />
      ) : null}

      {reparseTarget ? (
        <ReparseDialog
          words={words}
          onDismiss={() => setReparseTarget(null)}
          onConfirm={confirmReparse}
        />
      ) : null}

      {cosmeticOpen ? (
        <CosmeticConsentDialog
          words={words}
          phrase={cosmeticPhrase}
          copied={copiedTarget === "phrase"}
          initialFocusRef={cosmeticInputRef}
          onDismiss={() => setCosmeticOpen(false)}
          onPhraseChange={setCosmeticPhrase}
          onCopyPhrase={() => void copyText(COSMETIC_PHRASE, "phrase")}
          onEnable={() => {
            setCosmeticConsentAccepted(true);
            setSettings((current) => ({ ...current, exportCosmetics: true }));
            setCosmeticPhrase("");
            setCosmeticOpen(false);
          }}
        />
      ) : null}

      {closeOpen ? (
        <CloseTaskDialog
          words={words}
          initialFocusRef={keepWorkingRef}
          onDismiss={() => setCloseOpen(false)}
          onExit={() => void exitDesktopApp()}
        />
      ) : null}

      <span className="sr-only" role="status" aria-live="polite">{liveMessage}</span>
    </div>
  );
}

export default App;
