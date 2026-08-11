/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useMemo } from "react";
import type { TextDictionary } from "../i18n";
import {
  AlertIcon,
  ArrowIcon,
  CheckIcon,
  CloseIcon,
  FolderIcon,
  ReplayIcon,
} from "../icons";
import type { Language } from "../types";
import { SwitchControl } from "./SwitchControl";
import "./batch-workspace.css";

export const BATCH_SELECTION_LIMIT = 8;

export type BatchConcurrency = "auto" | 2 | 4 | 6 | 8;
export type BatchCandidateStatus = "ready" | "imported" | "duplicate" | "unsupported";
export type BatchRunState = "idle" | "running" | "stopping" | "interrupted" | "complete";
export type BatchJobPhase =
  | "queued"
  | "decompressing"
  | "parsing"
  | "analyzing"
  | "selecting"
  | "converting"
  | "validating"
  | "completed"
  | "failed"
  | "skipped";

export interface BatchScanCandidate {
  id: string;
  path: string;
  fileName: string;
  sizeBytes: number | string;
  compressed?: boolean;
  modifiedAtMs?: number | null;
  status: BatchCandidateStatus;
  reason?: string | null;
}

export interface BatchJobItem {
  id: string;
  candidateId: string;
  path: string;
  fileName: string;
  phase: BatchJobPhase;
  /** Normalized 0..1 progress. Omit when the active phase is indeterminate. */
  progress?: number | null;
  stage?: string | null;
  elapsedSeconds?: number | null;
  error?: string | null;
  outputPath?: string | null;
}

export interface BatchRunSummary {
  total: number;
  completed: number;
  failed: number;
  skipped: number;
}

export interface BatchWorkspaceProps {
  words: TextDictionary;
  language: Language;
  notice?: string | null;
  candidates: readonly BatchScanCandidate[];
  selectedCandidateIds: readonly string[];
  concurrency: BatchConcurrency;
  runState: BatchRunState;
  startDisabled: boolean;
  canResume: boolean;
  jobs: readonly BatchJobItem[];
  summary: BatchRunSummary;
  soundNotifications: boolean;
  exportCosmetics: boolean;
  exportStickers: boolean;
  exportCharms: boolean;
  cosmeticOptionsLocked: boolean;
  onChooseDemos: () => void;
  onBack: () => void;
  onSelectionChange: (candidateIds: string[]) => void;
  onConcurrencyChange: (value: BatchConcurrency) => void;
  onSoundNotificationsChange: (enabled: boolean) => void;
  onRequestCosmetics: () => void;
  onCosmeticOptionsChange: (patch: {
    exportCosmetics?: boolean;
    exportStickers?: boolean;
    exportCharms?: boolean;
  }) => void;
  onStart: (candidateIds: string[]) => void;
  onResume: () => void;
  onStop: () => void;
  onFinish: () => void;
  onRetryJob?: (jobId: string) => void;
  onOpenArchive?: (job: BatchJobItem) => void;
}

interface BatchCopy {
  title: string;
  reselect: string;
  close: string;
  demos: string;
  selected: string;
  selectAll: string;
  clear: string;
  noCandidates: string;
  candidateStatus: Record<BatchCandidateStatus, string>;
  replace: string;
  settings: string;
  concurrency: string;
  cosmetics: string;
  stickers: string;
  charms: string;
  sound: string;
  progress: string;
  notStarted: string;
  completed: string;
  failed: string;
  skipped: string;
  processed: string;
  phase: Record<BatchJobPhase, string>;
  elapsed: string;
  retry: string;
  openArchive: string;
  start: string;
  resume: string;
  stop: string;
  stopping: string;
  finish: string;
}

const COPY: Record<Language, BatchCopy> = {
  zh: {
    title: "导入 Demo",
    reselect: "重新选择",
    close: "关闭",
    demos: "Demo",
    selected: "{count} / 8",
    selectAll: "全选",
    clear: "清空",
    noCandidates: "没有可处理的 Demo",
    candidateStatus: {
      ready: "可转换",
      imported: "已入库",
      duplicate: "重复",
      unsupported: "不支持",
    },
    replace: "替换",
    settings: "设置",
    concurrency: "并发",
    cosmetics: "饰品",
    stickers: "贴纸",
    charms: "挂件",
    sound: "完成提醒",
    progress: "进度",
    notStarted: "尚未开始",
    completed: "已入库",
    failed: "失败",
    skipped: "跳过",
    processed: "{done} / {total}",
    phase: {
      queued: "等待",
      decompressing: "解压",
      parsing: "解析",
      analyzing: "分析",
      selecting: "准备",
      converting: "写入",
      validating: "验证",
      completed: "已入库",
      failed: "失败",
      skipped: "已跳过",
    },
    elapsed: "{time}",
    retry: "重试",
    openArchive: "打开",
    start: "导入 {count} 个 Demo",
    resume: "继续",
    stop: "停止派发",
    stopping: "正在停止",
    finish: "结束并返回回放库",
  },
  en: {
    title: "Import demos",
    reselect: "Choose again",
    close: "Close",
    demos: "Demos",
    selected: "{count} / 8",
    selectAll: "Select all",
    clear: "Clear",
    noCandidates: "No demos available",
    candidateStatus: {
      ready: "Ready",
      imported: "Imported",
      duplicate: "Duplicate",
      unsupported: "Unsupported",
    },
    replace: "Replace",
    settings: "Settings",
    concurrency: "Concurrency",
    cosmetics: "Cosmetics",
    stickers: "Stickers",
    charms: "Charms",
    sound: "Completion sound",
    progress: "Progress",
    notStarted: "Not started",
    completed: "Imported",
    failed: "Failed",
    skipped: "Skipped",
    processed: "{done} / {total}",
    phase: {
      queued: "Queued",
      decompressing: "Decompressing",
      parsing: "Parsing",
      analyzing: "Analyzing",
      selecting: "Preparing",
      converting: "Writing",
      validating: "Validating",
      completed: "Imported",
      failed: "Failed",
      skipped: "Skipped",
    },
    elapsed: "{time}",
    retry: "Retry",
    openArchive: "Open",
    start: "Import {count} demos",
    resume: "Resume",
    stop: "Stop dispatch",
    stopping: "Stopping",
    finish: "Finish and return to library",
  },
};

function formatBytes(value: number | string): string {
  const bytes = typeof value === "number" ? value : Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return String(value);
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let amount = bytes / 1024;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit += 1;
  }
  return `${amount >= 10 ? amount.toFixed(1) : amount.toFixed(2)} ${units[unit]}`;
}

function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "—";
  const total = Math.max(0, Math.round(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const remainder = total % 60;
  if (hours > 0) return `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
  return `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function clampProgress(value: number | null | undefined): number | null {
  if (value === null || value === undefined || !Number.isFinite(value)) return null;
  return Math.min(1, Math.max(0, value));
}

function isCandidateSelectable(candidate: BatchScanCandidate): boolean {
  return candidate.status === "ready";
}

function isJobActive(phase: BatchJobPhase): boolean {
  return ["decompressing", "parsing", "analyzing", "selecting", "converting", "validating"].includes(phase);
}

export function BatchWorkspace({
  words,
  language,
  notice,
  candidates,
  selectedCandidateIds,
  concurrency,
  runState,
  startDisabled,
  canResume,
  jobs,
  summary,
  soundNotifications,
  exportCosmetics,
  exportStickers,
  exportCharms,
  cosmeticOptionsLocked,
  onChooseDemos,
  onBack,
  onSelectionChange,
  onConcurrencyChange,
  onSoundNotificationsChange,
  onRequestCosmetics,
  onCosmeticOptionsChange,
  onStart,
  onResume,
  onStop,
  onFinish,
  onRetryJob,
  onOpenArchive,
}: BatchWorkspaceProps) {
  const copy = COPY[language];
  const selected = useMemo(
    () => new Set(selectedCandidateIds.slice(0, BATCH_SELECTION_LIMIT)),
    [selectedCandidateIds],
  );
  const working = runState === "running" || runState === "stopping";
  const selectableCandidates = candidates.filter(isCandidateSelectable);
  const atLimit = selected.size >= BATCH_SELECTION_LIMIT;
  const processed = Math.min(summary.total, summary.completed + summary.failed + summary.skipped);
  const overallProgress = summary.total > 0 ? Math.min(1, processed / summary.total) : 0;

  function toggleCandidate(candidate: BatchScanCandidate) {
    if (!isCandidateSelectable(candidate) || working) return;
    const next = new Set(selected);
    if (next.has(candidate.id)) next.delete(candidate.id);
    else if (next.size < BATCH_SELECTION_LIMIT) next.add(candidate.id);
    onSelectionChange([...next]);
  }

  function selectAll() {
    if (working) return;
    onSelectionChange(selectableCandidates.slice(0, BATCH_SELECTION_LIMIT).map((candidate) => candidate.id));
  }

  return (
    <section className="batch-workspace" aria-labelledby="batch-workspace-title">
      <header className="batch-heading">
        <div>
          <h1 id="batch-workspace-title">{copy.title}</h1>
          <span>{copy.selected.replace("{count}", String(selected.size))}</span>
        </div>
        <div className="batch-heading-actions">
          <button className="quiet-button" type="button" onClick={onChooseDemos} disabled={working}>
            <FolderIcon size={15} />
            {copy.reselect}
          </button>
          <button className="icon-button" type="button" onClick={onBack} aria-label={copy.close}>
            <CloseIcon size={16} />
          </button>
        </div>
      </header>

      {notice ? <div className="batch-notice" role="status">{notice}</div> : null}

      <div className="batch-content">
        <section className="batch-candidate-pane" aria-labelledby="batch-candidate-title">
          <header className="batch-section-heading">
            <strong id="batch-candidate-title">{copy.demos}</strong>
            <div>
              <button className="text-button" type="button" onClick={selectAll} disabled={working || selectableCandidates.length === 0 || atLimit}>
                {copy.selectAll}
              </button>
              <button className="text-button" type="button" onClick={() => onSelectionChange([])} disabled={working || selected.size === 0}>
                {copy.clear}
              </button>
            </div>
          </header>

          <div className="batch-candidate-list">
            {candidates.length > 0 ? candidates.map((candidate) => {
              const selectable = isCandidateSelectable(candidate);
              const checked = selected.has(candidate.id);
              const disabled = working || !selectable || (!checked && atLimit);
              const replacing = candidate.status === "ready" && Boolean(candidate.reason);
              const status = replacing ? copy.replace : copy.candidateStatus[candidate.status];
              return (
                <label
                  className={`batch-candidate is-${candidate.status}${checked ? " is-selected" : ""}`}
                  key={candidate.id}
                  title={candidate.reason || status}
                >
                  <input type="checkbox" checked={checked} disabled={disabled} onChange={() => toggleCandidate(candidate)} />
                  <span className="batch-candidate-check" aria-hidden="true">{checked ? <CheckIcon size={11} /> : null}</span>
                  <span className="batch-candidate-copy">
                    <strong title={candidate.path}>{candidate.fileName}</strong>
                    <small>{formatBytes(candidate.sizeBytes)}</small>
                  </span>
                  <em>{status}</em>
                </label>
              );
            }) : <div className="batch-empty">{copy.noCandidates}</div>}
          </div>
        </section>

        <div className="batch-control-pane">
          <section className="batch-settings" aria-label={copy.settings}>
            <div className="batch-setting-row">
              <span>{copy.concurrency}</span>
              <div className="batch-concurrency-options" role="radiogroup" aria-label={copy.concurrency}>
                {(["auto", 2, 4, 6, 8] as const).map((value) => (
                  <button
                    className={concurrency === value ? "is-active" : ""}
                    type="button"
                    role="radio"
                    aria-checked={concurrency === value}
                    disabled={working || cosmeticOptionsLocked}
                    key={value}
                    onClick={() => onConcurrencyChange(value)}
                  >
                    {value === "auto" ? "Auto" : value}
                  </button>
                ))}
              </div>
            </div>

            <div className="batch-toggle-list">
              <div className="batch-toggle-row">
                <span>{copy.cosmetics}</span>
                <SwitchControl
                  checked={exportCosmetics}
                  disabled={working || cosmeticOptionsLocked}
                  label={copy.cosmetics}
                  onChange={(checked) => {
                    if (checked) onRequestCosmetics();
                    else onCosmeticOptionsChange({ exportCosmetics: false });
                  }}
                />
              </div>
              {exportCosmetics ? (
                <>
                  <div className="batch-toggle-row is-dependent">
                    <span>{copy.stickers}</span>
                    <SwitchControl checked={exportStickers} disabled={working || cosmeticOptionsLocked} label={copy.stickers} onChange={(checked) => onCosmeticOptionsChange({ exportStickers: checked })} />
                  </div>
                  <div className="batch-toggle-row is-dependent">
                    <span>{copy.charms}</span>
                    <SwitchControl checked={exportCharms} disabled={working || cosmeticOptionsLocked} label={copy.charms} onChange={(checked) => onCosmeticOptionsChange({ exportCharms: checked })} />
                  </div>
                </>
              ) : null}
              <div className="batch-toggle-row">
                <span>{copy.sound}</span>
                <SwitchControl checked={soundNotifications} disabled={working} label={copy.sound} onChange={onSoundNotificationsChange} />
              </div>
            </div>
          </section>

          {jobs.length > 0 || summary.total > 0 ? <section className="batch-monitor" aria-labelledby="batch-monitor-title">
            <header className="batch-section-heading">
              <strong id="batch-monitor-title">{copy.progress}</strong>
              <div className="batch-progress-summary" aria-live="polite">
                {summary.total > 0 ? (
                  <>
                    <span className="is-complete">{copy.completed} <b>{summary.completed}</b></span>
                    {summary.failed > 0 ? <span className="is-failed">{copy.failed} <b>{summary.failed}</b></span> : null}
                    {summary.skipped > 0 ? <span>{copy.skipped} <b>{summary.skipped}</b></span> : null}
                    <span>{copy.processed.replace("{done}", String(processed)).replace("{total}", String(summary.total))}</span>
                  </>
                ) : null}
              </div>
            </header>

            {summary.total > 0 ? (
              <div className="batch-overall-progress" aria-hidden="true">
                <span style={{ width: `${overallProgress * 100}%` }} />
              </div>
            ) : null}

            <div className="batch-job-list">
              {jobs.length > 0 ? jobs.map((job) => {
                const progress = clampProgress(job.progress);
                const active = isJobActive(job.phase);
                const stage = job.stage && job.stage !== copy.phase[job.phase] ? job.stage : null;
                return (
                  <article className={`batch-job is-${job.phase}`} key={job.id}>
                    <span className="batch-job-state" aria-hidden="true">
                      {job.phase === "completed" ? <CheckIcon size={12} /> : job.phase === "failed" ? <AlertIcon size={12} /> : <i />}
                    </span>
                    <div className="batch-job-main">
                      <header>
                        <strong title={job.path}>{job.fileName}</strong>
                        <span>{copy.phase[job.phase]}</span>
                      </header>
                      <div className={`batch-job-progress${progress === null && active ? " is-indeterminate" : ""}`} aria-hidden="true">
                        <span style={progress !== null ? { width: `${progress * 100}%` } : undefined} />
                      </div>
                      {(stage || job.elapsedSeconds !== null && job.elapsedSeconds !== undefined) ? (
                        <div className="batch-job-meta">
                          {stage ? <span>{stage}</span> : null}
                          {job.elapsedSeconds !== null && job.elapsedSeconds !== undefined
                            ? <small>{copy.elapsed.replace("{time}", formatDuration(job.elapsedSeconds))}</small>
                            : null}
                        </div>
                      ) : null}
                      {job.error ? <p className="batch-job-error" role="alert">{job.error}</p> : null}
                    </div>
                    <div className="batch-job-actions">
                      {job.phase === "failed" && onRetryJob && !working ? (
                        <button className="quiet-button" type="button" onClick={() => onRetryJob(job.id)}>
                          <ReplayIcon size={13} />{copy.retry}
                        </button>
                      ) : null}
                      {job.phase === "completed" && onOpenArchive && !working ? (
                        <button className="quiet-button" type="button" onClick={() => onOpenArchive(job)}>
                          {copy.openArchive}<ArrowIcon size={13} />
                        </button>
                      ) : null}
                    </div>
                  </article>
                );
              }) : null}
            </div>
          </section> : null}
        </div>
      </div>

      <footer className="batch-footer">
        <span>{working ? copy.processed.replace("{done}", String(processed)).replace("{total}", String(summary.total)) : ""}</span>
        {working ? (
          <button className="secondary-button" type="button" onClick={onStop} disabled={runState === "stopping"}>
            {runState === "stopping" ? copy.stopping : copy.stop}
          </button>
        ) : canResume ? (
          <button className="secondary-button" type="button" onClick={onResume}>
            <ReplayIcon size={15} />{copy.resume}
          </button>
        ) : jobs.length > 0 && selected.size === 0 ? (
          <button className="primary-button" type="button" onClick={onFinish}>
            {copy.finish}<ArrowIcon size={15} />
          </button>
        ) : (
          <button className="primary-button" type="button" disabled={startDisabled || selected.size === 0} onClick={() => onStart([...selected])}>
            {copy.start.replace("{count}", String(selected.size))}<ArrowIcon size={15} />
          </button>
        )}
      </footer>

      <span className="sr-only" role="status" aria-live="polite">{words.localOnlyShort}</span>
    </section>
  );
}
