/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { useCallback, useEffect, useState } from "react";
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
