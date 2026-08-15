/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ACTIVITY_LOG_LIMIT,
  activityLogSinceMs,
  mergeActivityLogs,
  parseCommandError,
} from "../appSupport";
import type { ActivityLogRange } from "../components/LogsWorkspace";
import { TEXT } from "../i18n";
import type {
  ActivityLogLevel,
  ActivityLogMaintenance,
  AppLogEntry,
  CommandErrorDto,
  GsiStatus,
  Language,
} from "../types";

interface ActivityLogControllerOptions {
  language: Language;
  logsActive: boolean;
  cs2Path: string;
  onError: (error: CommandErrorDto) => void;
}

export function useActivityLogController({
  language,
  logsActive,
  cs2Path,
  onError,
}: ActivityLogControllerOptions) {
  const [entries, setEntries] = useState<AppLogEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [range, setRange] = useState<ActivityLogRange>("today");
  const [gsiStatus, setGsiStatus] = useState<GsiStatus | null>(null);
  const browserPreviewSeededRef = useRef(false);

  const record = useCallback((level: ActivityLogLevel, source: string, message: string) => {
    if (!message.trim()) return;
    if (!("__TAURI_INTERNALS__" in window)) {
      const timestampMs = Date.now();
      setEntries((current) => mergeActivityLogs(current, [{
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
      setEntries((current) => mergeActivityLogs(current, [entry]));
    }).catch(() => undefined);
  }, []);

  const refresh = useCallback(async () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    setLoading(true);
    try {
      const [nextEntries, status] = await Promise.all([
        invoke<AppLogEntry[]>("list_activity_logs", {
          limit: ACTIVITY_LOG_LIMIT,
          sinceMs: activityLogSinceMs(range),
        }),
        invoke<GsiStatus>("gsi_status"),
      ]);
      setEntries(nextEntries);
      setGsiStatus(status);
    } finally {
      setLoading(false);
    }
  }, [range]);

  const openDirectory = useCallback(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    void invoke<void>("open_activity_log_directory").catch((reason) => {
      onError(parseCommandError(reason));
    });
  }, [onError]);

  const clear = useCallback(() => {
    if (!window.confirm(TEXT[language].logsClearConfirm)) return;
    if (!("__TAURI_INTERNALS__" in window)) {
      setEntries([]);
      return;
    }
    setLoading(true);
    void invoke<number>("clear_activity_logs").then(() => {
      setEntries([]);
    }).catch((reason) => {
      onError(parseCommandError(reason));
    }).finally(() => setLoading(false));
  }, [language, onError]);

  useEffect(() => {
    if ("__TAURI_INTERNALS__" in window || !import.meta.env.DEV || browserPreviewSeededRef.current) return;
    browserPreviewSeededRef.current = true;
    const now = Date.now();
    setEntries([
      { id: "preview-1", timestampMs: now - 42_000, level: "info", source: "app", message: "CS2 DemoTracer 1.1.7 started" },
      { id: "preview-2", timestampMs: now - 31_000, level: "debug", source: "analysis", message: "phase=parsing" },
      { id: "preview-3", timestampMs: now - 24_000, level: "info", source: "analysis", message: "Parsed match.dem.zst: 24 rounds · 10 players" },
      { id: "preview-4", timestampMs: now - 15_000, level: "warn", source: "conversion", message: "Round 12: partial player evidence was preserved" },
      { id: "preview-5", timestampMs: now - 4_000, level: "info", source: "gsi", message: "map=de_anubis · round=7 · roundPhase=freezetime · activity=playing" },
    ]);
    setGsiStatus({
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
    void refresh();
    void invoke<ActivityLogMaintenance>("maintain_activity_logs").catch(() => undefined);
    const timer = window.setInterval(() => {
      void invoke<ActivityLogMaintenance>("maintain_activity_logs").catch(() => undefined);
    }, 15 * 60 * 1_000);
    return () => window.clearInterval(timer);
  }, [refresh]);

  useEffect(() => {
    if (!logsActive || !("__TAURI_INTERNALS__" in window)) return;
    void refresh();
    const timer = window.setInterval(() => void refresh(), 2_500);
    return () => window.clearInterval(timer);
  }, [logsActive, refresh]);

  useEffect(() => {
    const normalizedCs2Path = cs2Path.trim();
    if (!normalizedCs2Path || !("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    void invoke<GsiStatus>("configure_gsi", { cs2Path: normalizedCs2Path }).then((status) => {
      if (!disposed) setGsiStatus(status);
    }).catch(async (reason) => {
      const error = parseCommandError(reason);
      const status = await invoke<GsiStatus>("gsi_status").catch(() => null);
      if (disposed) return;
      setGsiStatus(status ? { ...status, error: error.message } : null);
      record("warn", "gsi", `GSI configuration skipped: ${error.code}`);
    });
    return () => { disposed = true; };
  }, [cs2Path, record]);

  return {
    entries,
    loading,
    range,
    setRange,
    gsiStatus,
    record,
    refresh,
    openDirectory,
    clear,
  };
}
