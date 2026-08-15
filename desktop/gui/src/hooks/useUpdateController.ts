/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { getVersion } from "@tauri-apps/api/app";
import { Channel, invoke } from "@tauri-apps/api/core";
import { relaunch } from "@tauri-apps/plugin-process";
import { check, type Update } from "@tauri-apps/plugin-updater";
import { useEffect, useRef, useState } from "react";
import packageMetadata from "../../package.json";
import {
  parseCommandError,
  playbackUpdateFailureStatus,
  userFacingErrorMessage,
} from "../appSupport";
import { TEXT } from "../i18n";
import {
  normalizeIgnoredUpdateVersions,
  normalizePendingPlaybackUpdate,
  PENDING_PLAYBACK_UPDATE_STORAGE_KEY,
  updateVersionIsIgnored,
  IGNORED_UPDATE_VERSIONS_STORAGE_KEY,
  type IgnoredUpdateVersions,
} from "../updatePrompt";
import type {
  GuiUpdateStatus,
  Language,
  PlaybackInstallProgress,
  PlaybackInstallResult,
  PlaybackReleaseStatus,
  PlaybackUpdateRelease,
  PlaybackUpdateStatus,
} from "../types";

interface UpdateControllerOptions {
  language: Language;
  cs2Path: string;
  onInspectEnvironment: (path: string) => Promise<void>;
}

export function useUpdateController({
  language,
  cs2Path,
  onInspectEnvironment,
}: UpdateControllerOptions) {
  const words = TEXT[language];
  const [appVersion, setAppVersion] = useState(packageMetadata.version);
  const [guiUpdate, setGuiUpdate] = useState<GuiUpdateStatus>({
    phase: "idle",
    currentVersion: packageMetadata.version,
  });
  const [dialogOpen, setDialogOpen] = useState(false);
  const [promptDismissed, setPromptDismissed] = useState(false);
  const [ignoredVersions, setIgnoredVersions] = useState<IgnoredUpdateVersions>(() => (
    normalizeIgnoredUpdateVersions(localStorage.getItem(IGNORED_UPDATE_VERSIONS_STORAGE_KEY))
  ));
  const [playbackRelease, setPlaybackRelease] = useState<PlaybackReleaseStatus | null>(null);
  const [playbackUpdate, setPlaybackUpdate] = useState<PlaybackUpdateStatus>({ phase: "idle" });
  const [playbackReleaseError, setPlaybackReleaseError] = useState("");
  const [playbackInstallBlockedByCs2, setPlaybackInstallBlockedByCs2] = useState(false);
  const [releaseAction, setReleaseAction] = useState<"installingOnline" | "installingFile" | "rollingBack" | null>(null);
  const [playbackInstallProgress, setPlaybackInstallProgress] = useState<PlaybackInstallProgress | null>(null);
  const [releaseNotice, setReleaseNotice] = useState("");
  const pendingGuiUpdateRef = useRef<Update | null>(null);
  const playbackContinuationStartedRef = useRef(false);

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
        setGuiUpdate({ phase: "current", currentVersion, availableVersion: currentVersion });
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
    } catch {
      setGuiUpdate({ phase: "error", currentVersion });
    }
  }

  async function installGuiApplicationUpdate() {
    const update = pendingGuiUpdateRef.current;
    if (!update || guiUpdate.phase !== "available") return;
    let downloadedBytes = 0;
    let totalBytes: number | undefined;
    setReleaseNotice("");
    setGuiUpdate((current) => ({ ...current, phase: "downloading", downloadedBytes: 0 }));
    try {
      await update.downloadAndInstall((event) => {
        if (event.event === "Started") {
          totalBytes = event.data.contentLength ?? undefined;
          setGuiUpdate((current) => ({ ...current, phase: "downloading", downloadedBytes: 0, totalBytes }));
        } else if (event.event === "Progress") {
          downloadedBytes += event.data.chunkLength;
          setGuiUpdate((current) => ({ ...current, phase: "downloading", downloadedBytes, totalBytes }));
        } else if (event.event === "Finished") {
          setGuiUpdate((current) => ({ ...current, phase: "installing", downloadedBytes, totalBytes }));
        }
      }, { timeout: 120_000 });
      pendingGuiUpdateRef.current = null;
      await relaunch();
    } catch {
      localStorage.removeItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY);
      setGuiUpdate((current) => ({ ...current, phase: "error" }));
    }
  }

  async function checkPlaybackUpdate(ignoreBusy = false) {
    const normalizedCs2Path = cs2Path.trim();
    if (!normalizedCs2Path || (!ignoreBusy && releaseAction)) return;
    setPlaybackUpdate({ phase: "checking" });
    try {
      const status = await invoke<PlaybackUpdateRelease>("playback_update_status", { cs2Path: normalizedCs2Path });
      setPlaybackUpdate({
        phase: status.updateAvailable ? "available" : "current",
        latestVersion: status.latestVersion,
        notes: status.notes,
      });
    } catch (reason) {
      setPlaybackUpdate(playbackUpdateFailureStatus(reason, language));
    }
  }

  async function finishPlaybackChange(result: PlaybackInstallResult, action: "install" | "rollback") {
    setPlaybackInstallBlockedByCs2(false);
    setReleaseNotice(action === "install"
      ? words.playbackInstalledNotice
        .replace("{version}", result.version)
        .replace("{installed}", String(result.installedFiles))
        .replace("{removed}", String(result.removedLegacyFiles))
      : words.playbackRollbackNotice);
    await onInspectEnvironment(cs2Path);
    const status = await invoke<PlaybackReleaseStatus>("playback_release_status", { cs2Path: cs2Path.trim() });
    setPlaybackRelease(status);
    await checkPlaybackUpdate(true);
  }

  async function installLatestPlaybackBundle() {
    const normalizedCs2Path = cs2Path.trim();
    if (!normalizedCs2Path || releaseAction) return false;
    setReleaseAction("installingOnline");
    setPlaybackReleaseError("");
    setPlaybackInstallBlockedByCs2(false);
    setReleaseNotice("");
    setPlaybackInstallProgress({ phase: "checking" });
    const events = new Channel<PlaybackInstallProgress>();
    events.onmessage = (progress) => {
      setPlaybackInstallProgress((current) => ({ ...current, ...progress }));
    };
    try {
      const result = await invoke<PlaybackInstallResult>("install_latest_playback_bundle", {
        cs2Path: normalizedCs2Path,
        events,
      });
      await finishPlaybackChange(result, "install");
      return true;
    } catch (reason) {
      const error = parseCommandError(reason);
      setPlaybackInstallBlockedByCs2(error.code.toLocaleLowerCase().includes("cs2_running"));
      setPlaybackReleaseError(userFacingErrorMessage(error, language));
      return false;
    } finally {
      events.onmessage = () => undefined;
      setPlaybackInstallProgress(null);
      setReleaseAction(null);
    }
  }

  async function installPlaybackBundle() {
    const normalizedCs2Path = cs2Path.trim();
    if (!normalizedCs2Path || releaseAction) return;
    setPlaybackReleaseError("");
    setReleaseNotice("");
    try {
      const packagePath = await invoke<string | null>("choose_playback_bundle", { initialPath: null });
      if (!packagePath) return;
      setReleaseAction("installingFile");
      const result = await invoke<PlaybackInstallResult>("install_playback_bundle", {
        cs2Path: normalizedCs2Path,
        packagePath,
      });
      await finishPlaybackChange(result, "install");
    } catch (reason) {
      setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    } finally {
      setReleaseAction(null);
    }
  }

  async function rollbackPlaybackInstall() {
    const normalizedCs2Path = cs2Path.trim();
    if (!normalizedCs2Path || releaseAction || !playbackRelease?.canRollback) return;
    setReleaseAction("rollingBack");
    setPlaybackReleaseError("");
    setReleaseNotice("");
    try {
      const result = await invoke<PlaybackInstallResult>("rollback_playback_install", { cs2Path: normalizedCs2Path });
      await finishPlaybackChange(result, "rollback");
    } catch (reason) {
      setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    } finally {
      setReleaseAction(null);
    }
  }

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
    if (!("__TAURI_INTERNALS__" in window)) return;
    const normalizedCs2Path = cs2Path.trim();
    let disposed = false;
    setPlaybackReleaseError("");
    setPlaybackUpdate(normalizedCs2Path ? { phase: "checking" } : { phase: "idle" });
    void invoke<PlaybackReleaseStatus>("playback_release_status", {
      cs2Path: normalizedCs2Path || null,
    }).then((status) => {
      if (!disposed) setPlaybackRelease(status);
    }).catch((reason) => {
      if (!disposed) setPlaybackReleaseError(userFacingErrorMessage(parseCommandError(reason), language));
    });
    if (normalizedCs2Path) {
      void invoke<PlaybackUpdateRelease>("playback_update_status", { cs2Path: normalizedCs2Path }).then((status) => {
        if (disposed) return;
        setPlaybackUpdate({
          phase: status.updateAvailable ? "available" : "current",
          latestVersion: status.latestVersion,
          notes: status.notes,
        });
      }).catch((reason) => {
        if (!disposed) setPlaybackUpdate(playbackUpdateFailureStatus(reason, language));
      });
    }
    return () => { disposed = true; };
  }, [cs2Path, language]);

  useEffect(() => {
    if (playbackContinuationStartedRef.current) return;
    const stored = localStorage.getItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY);
    const pending = normalizePendingPlaybackUpdate(stored);
    if (!pending) {
      if (stored) localStorage.removeItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY);
      return;
    }
    if (guiUpdate.currentVersion !== pending.guiVersion) return;
    if (playbackUpdate.phase === "current") {
      localStorage.removeItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY);
      return;
    }
    if (playbackUpdate.phase !== "available") return;
    if (playbackUpdate.latestVersion !== pending.playbackVersion) {
      localStorage.removeItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY);
      return;
    }
    playbackContinuationStartedRef.current = true;
    localStorage.removeItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY);
    setDialogOpen(true);
    void installLatestPlaybackBundle().then((installed) => {
      if (installed) setDialogOpen(false);
    });
  }, [guiUpdate.currentVersion, playbackUpdate.latestVersion, playbackUpdate.phase]);

  const guiUpdateAvailable = guiUpdate.phase === "available" && Boolean(guiUpdate.availableVersion);
  const guiUpdateRetryRequired = guiUpdate.phase === "error"
    && Boolean(guiUpdate.availableVersion && guiUpdate.availableVersion !== guiUpdate.currentVersion);
  const playbackUpdateAvailable = playbackUpdate.phase === "available" && Boolean(playbackUpdate.latestVersion);
  const actionableGuiUpdate = guiUpdateAvailable
    && !updateVersionIsIgnored(ignoredVersions, "gui", guiUpdate.availableVersion);
  const actionablePlaybackUpdate = playbackUpdateAvailable
    && !updateVersionIsIgnored(ignoredVersions, "playback", playbackUpdate.latestVersion);
  const guiUpdateOffered = actionableGuiUpdate || guiUpdateRetryRequired;
  const playbackUpdateOffered = actionablePlaybackUpdate;
  const actionableUpdateAvailable = actionableGuiUpdate || actionablePlaybackUpdate;
  const availableUpdateCount = Number(guiUpdateOffered) + Number(playbackUpdateOffered);
  const promptSummary = [
    actionableGuiUpdate
      ? `DemoTracer v${guiUpdate.currentVersion || appVersion} → v${guiUpdate.availableVersion}`
      : "",
    actionablePlaybackUpdate
      ? `Playback ${playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy} → v${playbackUpdate.latestVersion}`
      : "",
  ].filter(Boolean).join(" · ");
  const dialogBusy = guiUpdate.phase === "checking"
    || guiUpdate.phase === "downloading"
    || guiUpdate.phase === "installing"
    || releaseAction === "installingOnline";
  const playbackInstallStatus = playbackInstallProgress?.phase === "downloading" ? words.releaseDownloading
    : playbackInstallProgress?.phase === "verifying" ? words.releaseVerifying
      : playbackInstallProgress?.phase === "installing" ? words.releaseInstalling
        : words.releaseChecking;
  const dialogStatus = guiUpdate.phase === "downloading" ? words.releaseDownloading
    : guiUpdate.phase === "installing" ? words.releaseInstalling
      : releaseAction === "installingOnline" ? playbackInstallStatus
        : availableUpdateCount > 0 ? words.releaseUpdateAvailable
          : guiUpdate.phase === "error" || playbackUpdate.phase === "error" ? words.releaseCheckUnavailable
            : words.releaseUpToDate;
  const dialogProgressActive = guiUpdate.phase === "downloading"
    || guiUpdate.phase === "installing"
    || releaseAction === "installingOnline";
  const dialogProgress = guiUpdate.phase === "downloading" && guiUpdate.totalBytes && guiUpdate.downloadedBytes != null
    ? Math.min(100, Math.round((guiUpdate.downloadedBytes / guiUpdate.totalBytes) * 100))
    : releaseAction === "installingOnline" && playbackInstallProgress?.totalBytes && playbackInstallProgress.downloadedBytes != null
      ? Math.min(100, Math.round((playbackInstallProgress.downloadedBytes / playbackInstallProgress.totalBytes) * 100))
      : null;

  function dismissPrompt() {
    setPromptDismissed(true);
    setDialogOpen(false);
  }

  function ignoreAvailableVersions() {
    setIgnoredVersions((current) => ({
      ...current,
      ...(guiUpdateOffered && guiUpdate.availableVersion ? { gui: guiUpdate.availableVersion } : {}),
      ...(playbackUpdateOffered && playbackUpdate.latestVersion ? { playback: playbackUpdate.latestVersion } : {}),
    }));
    dismissPrompt();
  }

  async function installAvailableUpdates() {
    if (guiUpdateRetryRequired) {
      await checkGuiApplicationUpdate();
      return;
    }
    if (guiUpdateAvailable) {
      if (playbackUpdateOffered && guiUpdate.availableVersion && playbackUpdate.latestVersion) {
        localStorage.setItem(PENDING_PLAYBACK_UPDATE_STORAGE_KEY, JSON.stringify({
          guiVersion: guiUpdate.availableVersion,
          playbackVersion: playbackUpdate.latestVersion,
        }));
      }
      await installGuiApplicationUpdate();
      return;
    }
    if (playbackUpdateOffered) {
      const installed = await installLatestPlaybackBundle();
      if (installed) setDialogOpen(false);
    }
  }

  function reviewGuiUpdate() {
    setIgnoredVersions((current) => {
      if (!updateVersionIsIgnored(current, "gui", guiUpdate.availableVersion)) return current;
      const { gui: _ignoredGui, ...rest } = current;
      return rest;
    });
    setDialogOpen(true);
  }

  return {
    appVersion,
    guiUpdate,
    dialogOpen,
    setDialogOpen,
    promptDismissed,
    ignoredVersions,
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
    promptSummary,
    dialogBusy,
    dialogStatus,
    dialogProgressActive,
    dialogProgress,
    playbackInstallStatus,
    dismissPrompt,
    ignoreAvailableVersions,
    installAvailableUpdates,
    reviewGuiUpdate,
    checkGuiApplicationUpdate,
    checkPlaybackUpdate,
    installLatestPlaybackBundle,
    installPlaybackBundle,
    rollbackPlaybackInstall,
  };
}
