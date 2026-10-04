/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useMemo, useRef, useState } from "react";
import { FolderIcon, RefreshIcon, SearchIcon, TrashIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ActivityLogLevel, AppLogEntry, ServerDiagnostics } from "../types";
import { SelectControl } from "./SelectControl";
import "./logs-workspace.css";

type LogFilter = "all" | ActivityLogLevel;
export type ActivityLogRange = "today" | "sevenDays" | "all";

interface LogsWorkspaceProps {
  words: TextDictionary;
  entries: AppLogEntry[];
  diagnostics: ServerDiagnostics | null;
  transportError: string | null;
  loading: boolean;
  range: ActivityLogRange;
  onRangeChange: (range: ActivityLogRange) => void;
  onRefresh: () => void;
  onOpenFolder: () => void;
  onClear: () => void;
}

function formatLogTime(timestampMs: number): string {
  const date = new Date(timestampMs);
  const part = (value: number) => String(value).padStart(2, "0");
  return `${part(date.getMonth() + 1)}-${part(date.getDate())} ${part(date.getHours())}:${part(date.getMinutes())}:${part(date.getSeconds())}`;
}

export function LogsWorkspace({ words, entries, diagnostics, transportError, loading, range,
  onRangeChange, onRefresh, onOpenFolder, onClear }: LogsWorkspaceProps) {
  const [level, setLevel] = useState<LogFilter>("all");
  const [source, setSource] = useState("all");
  const [query, setQuery] = useState("");
  const [following, setFollowing] = useState(true);
  const [now, setNow] = useState(Date.now);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const filtered = useMemo(() => entries.filter((entry) => {
    if (level !== "all" && entry.level !== level) return false;
    if (source !== "all" && entry.source.startsWith("server:") !== (source === "server")) return false;
    return `${entry.source} ${entry.message}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase());
  }), [entries, level, query, source]);
  useEffect(() => {
    if (following && scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [filtered, following]);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  const health = diagnostics?.health;
  const fresh = !!health && now - health.writtenAtMs >= -5000 && now - health.writtenAtMs < 30000;
  const unavailable = !health ? words.runtimeWaiting
    : !health.running ? words.runtimeStopped : !fresh ? words.runtimeStale : null;
  const modules = [
    { name: "DemoTracer", available: health?.running, label: words.runtimeRunning },
    { name: "dtr-controller", available: health?.botController.compatible, label: words.runtimeAvailable },
    { name: "dtr-hider", available: health?.botHider.available, label: words.runtimeAvailable },
    { name: "BotRandomizer", available: health?.botRandomizer.available, label: words.runtimeAvailable },
  ];
  const errors = [transportError, diagnostics?.healthError, diagnostics?.logError].filter(Boolean);

  return (
    <section className="logs-workspace" aria-label={words.logsTitle}>
      <section className="runtime-status" aria-label={words.runtimeTitle}>
        <div className="runtime-status-heading">
          <span>{words.runtimeTitle}</span>
          <small title={words.runtimeCadence}>{health ? words.runtimeLastUpdate.replace("{time}", formatLogTime(health.writtenAtMs)) : words.runtimeWaiting}</small>
        </div>
        <div className="runtime-modules">
          {modules.map((module) => (
            <div className="runtime-module" key={module.name}>
              <strong>{module.name}</strong>
              <span className={unavailable ? "" : module.available ? "is-ready" : "is-error"}>
                {unavailable || (module.available ? module.label : words.runtimeUnavailable)}
              </span>
            </div>
          ))}
        </div>
        {errors.map((error, index) => <pre className="logs-transport-error" role="alert" key={index}>{error}</pre>)}
      </section>

      <div className="logs-toolbar">
        <SelectControl value={range} label={words.logsRange} options={[
          { value: "today", label: words.logsRangeToday }, { value: "sevenDays", label: words.logsRangeSevenDays }, { value: "all", label: words.logsRangeAll },
        ]} onChange={(value) => onRangeChange(value as ActivityLogRange)} />
        <SelectControl value={level} label={words.logsFilter} options={[
          { value: "all", label: words.logsLevelAll }, { value: "debug", label: "DEBUG" }, { value: "info", label: "INFO" },
          { value: "warn", label: "WARN" }, { value: "error", label: "ERROR" },
        ]} onChange={(value) => setLevel(value as LogFilter)} />
        <SelectControl value={source} label={words.logsSource} options={[
          { value: "all", label: words.logsSourceAll }, { value: "gui", label: words.logsSourceGui }, { value: "server", label: words.logsSourceServer },
        ]} onChange={setSource} />
        <label className="logs-search">
          <SearchIcon size={16} />
          <input aria-label={words.logsSearchPlaceholder} value={query} onChange={(event) => setQuery(event.target.value)} placeholder={words.logsSearchPlaceholder} />
        </label>
        <div className="logs-toolbar-actions">
          <button className="icon-button" type="button" onClick={onOpenFolder} aria-label={words.logsOpenFolder} title={words.logsOpenFolder}><FolderIcon size={16} /></button>
          <button className="icon-button" type="button" disabled={loading} onClick={onRefresh} aria-label={words.logsRefresh} title={words.logsRefresh}><RefreshIcon className={loading ? "release-spin" : undefined} size={16} /></button>
          <button className="icon-button" type="button" disabled={entries.length === 0} onClick={onClear} aria-label={words.logsClear} title={words.logsClear}><TrashIcon size={16} /></button>
        </div>
      </div>

      <div className="logs-scroll" ref={scrollRef} role="log" aria-live="off" onScroll={(event) => {
        const node = event.currentTarget;
        setFollowing(node.scrollHeight - node.scrollTop - node.clientHeight < 24);
      }}>
        {filtered.length ? filtered.map((entry) => {
          const [firstLine, ...details] = entry.message.split("\n");
          return (
            <article className={`log-entry is-${entry.level}`} key={entry.id}>
              <time dateTime={new Date(entry.timestampMs).toISOString()}>{formatLogTime(entry.timestampMs)}</time>
              <strong>{entry.level.toUpperCase()}</strong>
              <span className="log-source" title={entry.source}>{entry.source.replace(/^server:/, "")}</span>
              {details.length ? <details><summary>{firstLine}</summary><pre>{details.join("\n")}</pre></details> : <p>{firstLine}</p>}
            </article>
          );
        }) : <div className="logs-empty">{words.logsEmpty}</div>}
      </div>
      <footer className="logs-footer">
        <span title={diagnostics?.logPath || undefined}>{diagnostics?.logPath || words.logsNoServerFile}</span>
        <button type="button" className="text-button" aria-pressed={following} onClick={() => setFollowing(!following)}>{following ? "✓ " : ""}{words.logsFollow}</button>
      </footer>
    </section>
  );
}
