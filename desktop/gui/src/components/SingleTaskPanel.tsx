/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { ChevronIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { ProgressPhase, ProgressState } from "../types";
import "./single-task-panel.css";

type SingleTaskKind = "preflight" | "analysis" | "conversion";

interface SingleTaskPanelProps {
  words: TextDictionary;
  task: SingleTaskKind;
  sourcePath: string;
  elapsedSeconds: number;
  progress: ProgressState;
  outputRoot: string;
  cancelPending: boolean;
  onCancelAnalysis: () => void;
  onMinimize: () => void;
  preflightProgress?: { current: number; total: number };
}

function fileName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path;
}

function formatElapsed(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${String(minutes).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
}

function progressPhaseIndex(phase: ProgressPhase): number {
  if (phase === "writing") return 1;
  if (phase === "artifacts") return 2;
  if (phase === "voice") return 3;
  if (phase === "validating" || phase === "complete") return 4;
  return 0;
}

export function SingleTaskPanel({
  words,
  task,
  sourcePath,
  elapsedSeconds,
  progress,
  outputRoot,
  cancelPending,
  onCancelAnalysis,
  onMinimize,
  preflightProgress,
}: SingleTaskPanelProps) {
  const preflight = task === "preflight";
  const analysis = task === "analysis";
  const decompressing = progress.phase === "decompressing";
  const stages = [words.preparing, words.writingPlayers, words.writingArtifacts, words.exportingVoice, words.validating];
  const activeIndex = progressPhaseIndex(progress.phase);
  const determinate = progress.estimated > 0 && progress.unit !== null;
  const fraction = determinate ? Math.min(1, progress.written / progress.estimated) : 0;
  const progressLabel = progress.unit === "playerFiles"
    ? words.playerFilesProgress.replace("{written}", String(progress.written)).replace("{total}", String(progress.estimated))
    : progress.unit === "artifacts"
      ? words.artifactsProgress.replace("{written}", String(progress.written)).replace("{total}", String(progress.estimated))
      : stages[activeIndex];
  const title = preflight
    ? words.preflightTask
    : analysis
    ? (decompressing ? words.decompressingTitle : words.parseAnalyzeTask)
    : words.exportDtrTask;

  return (
    <aside className={`single-task-panel is-${task}`} aria-labelledby="single-task-panel-title">
      <header className="single-task-heading">
        <h2 id="single-task-panel-title">{title}</h2>
        {!preflight ? (
          <button className="icon-button" type="button" onClick={onMinimize} aria-label={words.minimizeTask} title={words.minimizeTask}>
            <ChevronIcon size={15} />
          </button>
        ) : null}
      </header>

      <div className="single-task-body">
        <div className="single-task-source" title={sourcePath}>{fileName(sourcePath)}</div>

        {preflight ? (
          <section className="preflight-task-status" role="status" aria-live="polite">
            <strong>{words.preflightCheckingArchive}</strong>
            <b>{preflightProgress?.current ?? 0} / {preflightProgress?.total ?? 0}</b>
            <div className="single-task-progress is-determinate">
              <span style={{ width: `${preflightProgress?.total
                ? Math.min(100, (preflightProgress.current / preflightProgress.total) * 100)
                : 0}%` }} />
            </div>
          </section>
        ) : analysis ? (
          <section className="analysis-task-status" role="status" aria-live="polite">
            <strong>{decompressing ? words.decompressingTitle : words.parseAnalyzeRunning}</strong>
            <time>{formatElapsed(elapsedSeconds)}</time>
            <div className="single-task-indeterminate" aria-hidden="true"><span /></div>
          </section>
        ) : (
          <section className="conversion-task-status" role="status" aria-live="polite">
            <div className="conversion-task-current">
              <strong>{stages[activeIndex]}</strong>
              <time>{formatElapsed(elapsedSeconds)}</time>
            </div>
            <div className="conversion-task-progress-copy">
              <span>{progressLabel}</span>
              {progress.currentRound !== undefined ? (
                <b>{words.roundProgress
                  .replace("{round}", String(progress.currentRound))
                  .replace("{completed}", String(progress.completedRounds))
                  .replace("{total}", String(progress.selectedRounds))}</b>
              ) : null}
            </div>
            <div className={`single-task-progress${determinate ? " is-determinate" : " is-indeterminate"}`}>
              <span style={determinate ? { width: `${fraction * 100}%` } : undefined} />
            </div>
            {progress.currentItem ? <code className="single-task-current-item" title={progress.currentItem}>{fileName(progress.currentItem)}</code> : null}
            {outputRoot ? <code className="single-task-output" title={outputRoot}>{outputRoot}</code> : null}
          </section>
        )}
      </div>

      {analysis ? (
        <footer className="single-task-footer">
          <button className="secondary-button" type="button" disabled={cancelPending} onClick={onCancelAnalysis}>
            {cancelPending ? words.stoppingAnalysis : words.stopAnalysis}
          </button>
        </footer>
      ) : null}

      <span className="sr-only" role="status" aria-live="polite">{progress.announcement}</span>
    </aside>
  );
}
