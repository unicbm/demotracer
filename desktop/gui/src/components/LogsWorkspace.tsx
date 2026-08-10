/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useMemo, useRef, useState } from "react";
import { FolderIcon, RefreshIcon, SearchIcon, TrashIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ActivityLogLevel, AppLogEntry, GsiStatus } from "../types";
import "./logs-workspace.css";

type LogFilter = "all" | ActivityLogLevel;

interface LogsWorkspaceProps {
  words: TextDictionary;
  entries: AppLogEntry[];
  gsiStatus: GsiStatus | null;
  loading: boolean;
  onRefresh: () => void;
  onOpenFolder: () => void;
  onClear: () => void;
}

function sourceLabel(words: TextDictionary, source: string): string {
  if (source === "analysis") return words.logsSourceAnalysis;
  if (source === "conversion") return words.logsSourceConversion;
  if (source === "batch") return words.logsSourceBatch;
  if (source === "gsi") return words.logsSourceGsi;
  if (source === "app") return words.logsSourceApp;
  return source;
}

function levelLabel(words: TextDictionary, level: ActivityLogLevel): string {
  if (level === "debug") return words.logsLevelDebug;
  if (level === "warn") return words.logsLevelWarn;
  if (level === "error") return words.logsLevelError;
  return words.logsLevelInfo;
}

function formatLogTime(timestampMs: number): string {
  const date = new Date(timestampMs);
  const part = (value: number) => String(value).padStart(2, "0");
  return `${part(date.getMonth() + 1)}-${part(date.getDate())} ${part(date.getHours())}:${part(date.getMinutes())}:${part(date.getSeconds())}`;
}

export function LogsWorkspace({
  words,
  entries,
  gsiStatus,
  loading,
  onRefresh,
  onOpenFolder,
  onClear,
}: LogsWorkspaceProps) {
  const [level, setLevel] = useState<LogFilter>("all");
  const [query, setQuery] = useState("");
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const normalizedQuery = query.trim().toLocaleLowerCase();
  const filtered = useMemo(() => entries.filter((entry) => {
    if (level !== "all" && entry.level !== level) return false;
    if (!normalizedQuery) return true;
    return `${entry.source} ${entry.message}`.toLocaleLowerCase().includes(normalizedQuery);
  }), [entries, level, normalizedQuery]);
  useEffect(() => {
    const node = scrollRef.current;
    if (node) node.scrollTop = node.scrollHeight;
  }, [filtered.length]);

  const gsiTone = gsiStatus?.error ? "error"
    : gsiStatus?.connected ? "connected"
      : gsiStatus?.configured && gsiStatus.listening ? "waiting"
        : "idle";
  const gsiLabel = gsiStatus?.error ? words.gsiUnavailable
    : gsiStatus?.connected ? words.gsiConnected
      : gsiStatus?.configured ? words.gsiWaiting
        : words.gsiNotConfigured;
  const gsiDetail = [
    gsiStatus?.map,
    gsiStatus?.round != null ? `R${gsiStatus.round}` : null,
    gsiStatus?.roundPhase,
    gsiStatus?.playerActivity,
  ].filter(Boolean).join(" · ");

  return (
    <section className="logs-workspace" aria-labelledby="logs-workspace-title">
      <header className="logs-heading">
        <h1 id="logs-workspace-title">{words.logsTitle}</h1>
      </header>

      <section className={`gsi-status-strip is-${gsiTone}`} aria-label={words.gsiTitle}>
        <span className="gsi-status-dot" aria-hidden="true" />
        <strong>{words.gsiTitle}</strong>
        <b>{gsiLabel}</b>
        {gsiDetail ? <code>{gsiDetail}</code> : null}
        {gsiStatus?.lastUpdateMs ? (
          <small>{words.gsiLastUpdate.replace("{time}", formatLogTime(gsiStatus.lastUpdateMs))}</small>
        ) : null}
      </section>

      <div className="logs-toolbar">
        <label className="logs-level-filter">
          <span className="sr-only">{words.logsFilter}</span>
          <select value={level} onChange={(event) => setLevel(event.target.value as LogFilter)}>
            <option value="all">{words.logsLevelAll.toLocaleUpperCase()}</option>
            <option value="debug">DEBUG</option>
            <option value="info">INFO</option>
            <option value="warn">WARN</option>
            <option value="error">ERROR</option>
          </select>
        </label>
        <label className="logs-search">
          <SearchIcon size={16} />
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder={words.logsSearchPlaceholder} />
        </label>
        <div className="logs-toolbar-actions">
          <button className="icon-button" type="button" onClick={onOpenFolder} aria-label={words.logsOpenFolder} title={words.logsOpenFolder}>
            <FolderIcon size={16} />
          </button>
          <button className="icon-button" type="button" disabled={loading} onClick={onRefresh} aria-label={words.logsRefresh} title={words.logsRefresh}>
            <RefreshIcon className={loading ? "release-spin" : undefined} size={16} />
          </button>
          <button className="icon-button logs-clear-button" type="button" disabled={entries.length === 0} onClick={onClear} aria-label={words.logsClear} title={words.logsClear}>
            <TrashIcon size={16} />
          </button>
        </div>
      </div>

      <div className="logs-scroll" ref={scrollRef} role="log" aria-live="polite">
        {filtered.length > 0 ? filtered.map((entry) => (
          <article className={`log-entry is-${entry.level}`} key={entry.id}>
            <header>
              <time dateTime={new Date(entry.timestampMs).toISOString()}>{formatLogTime(entry.timestampMs)}</time>
              <strong>{levelLabel(words, entry.level).toLocaleUpperCase()}</strong>
            </header>
            <p>[{sourceLabel(words, entry.source)}] {entry.message}</p>
          </article>
        )) : (
          <div className="logs-empty">{words.logsEmpty}</div>
        )}
      </div>
    </section>
  );
}
