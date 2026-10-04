/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ACTIVITY_LOG_LIMIT, activityLogSinceMs, mergeActivityLogs, parseCommandError } from "../appSupport";
import type { ActivityLogRange } from "../components/LogsWorkspace";
import type { ActivityLogLevel, AppLogEntry, CommandErrorDto, ServerDiagnostics } from "../types";

export function useActivityLogController({ cs2Path, onError }: {
  cs2Path: string;
  onError: (error: CommandErrorDto) => void;
}) {
  const [guiEntries, setGuiEntries] = useState<AppLogEntry[]>([]);
  const [diagnostics, setDiagnostics] = useState<ServerDiagnostics | null>(null);
  const [transportError, setTransportError] = useState<string | null>(null);
  const [monitorError, setMonitorError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [range, setRange] = useState<ActivityLogRange>("today");
  const [visibleSince, setVisibleSince] = useState(0);
  const sessionRef = useRef("");

  const receive = useCallback((next: ServerDiagnostics) => {
    if (next.session !== sessionRef.current) return;
    setDiagnostics((previous) => previous?.session === next.session && previous.revision > next.revision ? previous : next);
  }, []);

  const record = useCallback((level: ActivityLogLevel, source: string, message: string) => {
    if (!message.trim()) return;
    if (!("__TAURI_INTERNALS__" in window)) {
      setGuiEntries((current) => mergeActivityLogs(current, [{ id: crypto.randomUUID(), timestampMs: Date.now(), level, source, message }]));
      return;
    }
    void invoke<AppLogEntry>("append_activity_log", { request: { level, source, message } })
      .then((entry) => setGuiEntries((current) => mergeActivityLogs(current, [entry])))
      .catch((reason) => setTransportError(parseCommandError(reason).message));
  }, []);

  const refresh = useCallback(async () => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    setLoading(true);
    try {
      const [history, status] = await Promise.all([
        invoke<AppLogEntry[]>("list_activity_logs", { limit: ACTIVITY_LOG_LIMIT }),
        invoke<ServerDiagnostics>("server_diagnostics"),
      ]);
      setGuiEntries((current) => mergeActivityLogs(history, current));
      receive(status);
      setVisibleSince(0);
      setTransportError(null);
    } catch (reason) {
      setTransportError(parseCommandError(reason).message);
    } finally {
      setLoading(false);
    }
  }, [receive]);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    let disposed = false;
    let stop: (() => void) | undefined;
    void listen<{ message: string }>("log://log", ({ payload }) => {
      if (disposed) return;
      try {
        const entry = JSON.parse(payload.message) as AppLogEntry;
        setGuiEntries((current) => mergeActivityLogs(current, [entry]));
      } catch (reason) {
        setTransportError(String(reason));
      }
    }).then((unlisten) => {
      if (disposed) unlisten();
      else { stop = unlisten; void refresh(); }
    }).catch((reason) => { if (!disposed) setTransportError(parseCommandError(reason).message); });
    const onWindowError = (event: ErrorEvent) => record("error", "webview", event.error?.stack || event.message);
    const onRejection = (event: PromiseRejectionEvent) => record("error", "webview", event.reason?.stack || String(event.reason));
    window.addEventListener("error", onWindowError);
    window.addEventListener("unhandledrejection", onRejection);
    return () => {
      disposed = true;
      stop?.();
      window.removeEventListener("error", onWindowError);
      window.removeEventListener("unhandledrejection", onRejection);
    };
  }, [record, refresh]);

  useEffect(() => {
    setDiagnostics(null);
    setMonitorError(null);
    if (!("__TAURI_INTERNALS__" in window)) return;
    const session = crypto.randomUUID();
    sessionRef.current = session;
    let disposed = false;
    let stop: (() => void) | undefined;
    void listen<ServerDiagnostics>("server-diagnostics", ({ payload }) => { if (!disposed) receive(payload); })
      .then(async (unlisten) => {
        if (disposed) { unlisten(); return; }
        stop = unlisten;
        const status = await invoke<ServerDiagnostics>("configure_server_diagnostics", { cs2Path, session });
        if (!disposed) receive(status);
      }).catch((reason) => { if (!disposed) setMonitorError(parseCommandError(reason).message); });
    return () => { disposed = true; stop?.(); };
  }, [cs2Path, receive]);

  const openDirectory = useCallback(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    void invoke<void>("open_activity_log_directory").catch((reason) => onError(parseCommandError(reason)));
  }, [onError]);

  const entries = useMemo(() => {
    const since = Math.max(visibleSince, activityLogSinceMs(range) || 0);
    return mergeActivityLogs(guiEntries, diagnostics?.entries || []).filter((entry) => entry.timestampMs >= since);
  }, [guiEntries, diagnostics, range, visibleSince]);

  return { entries, loading, range, setRange, diagnostics, transportError: monitorError || transportError, record, refresh, openDirectory,
    clear: () => setVisibleSince(Date.now()),
  };
}
