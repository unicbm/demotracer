/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { type RefObject } from "react";
import {
  AlertIcon,
  ArrowIcon,
  CheckIcon,
  ChevronIcon,
  CopyIcon,
  FolderIcon,
  ReplayIcon,
} from "../icons";
import type { TextDictionary } from "../i18n";
import type { ConversionSummary } from "../types";

export type CopyTarget =
  | "playback"
  | "phrase"
  | "output"
  | "manifest"
  | "demoPath"
  | `player:${string}:${"steam" | "crosshair" | "viewmodel" | "inspect"}:${number}`;
export type CommandMode = "sequence" | "round";

interface OpeningArchiveViewProps {
  words: TextDictionary;
  manifestName: string;
}

export function OpeningArchiveView({ words, manifestName }: OpeningArchiveViewProps) {
  return (
    <section className="task-state-view task-progress-view archive-opening-view" aria-labelledby="archive-opening-title">
      <div className="task-progress-copy">
        <h1 id="archive-opening-title">{words.openingManifestTitle}</h1>
        <strong>{manifestName}</strong>
        <p>{words.openingManifestBody}</p>
      </div>
      <div className="indeterminate-progress" role="progressbar" aria-label={words.readingArchive}><span /></div>
      <div className="task-progress-meta" role="status" aria-live="polite">
        <span>{words.readingArchive}</span>
      </div>
    </section>
  );
}

interface AnalysisFailedViewProps {
  words: TextDictionary;
  error: string;
  retryButtonRef: RefObject<HTMLButtonElement | null>;
  onRetry: () => void;
  onChangeDemo: () => void;
}

export function AnalysisFailedView({ words, error, retryButtonRef, onRetry, onChangeDemo }: AnalysisFailedViewProps) {
  return (
    <section className="task-state-view failure-view" aria-labelledby="analysis-failed-title">
      <span className="failure-symbol" aria-hidden="true"><AlertIcon size={22} /></span>
      <h1 id="analysis-failed-title" tabIndex={-1}>{words.analysisFailedTitle}</h1>
      <p>{error}</p>
      <div className="view-actions">
        <button ref={retryButtonRef} className="primary-button" type="button" onClick={onRetry}><ReplayIcon size={16} />{words.retryAnalysis}</button>
        <button className="secondary-button" type="button" onClick={onChangeDemo}>{words.changeDemo}</button>
      </div>
    </section>
  );
}

interface ValidationFailedViewProps {
  words: TextDictionary;
  error: string;
  outputRoot: string;
  onOpenFolder: () => void;
  onBack: () => void;
}

export function ValidationFailedView({ words, error, outputRoot, onOpenFolder, onBack }: ValidationFailedViewProps) {
  return (
    <section className="task-state-view failure-view validation-failure" aria-labelledby="validation-failed-title">
      <span className="failure-symbol" aria-hidden="true"><AlertIcon size={22} /></span>
      <h1 id="validation-failed-title" tabIndex={-1}>{words.validationFailedTitle}</h1>
      <p>{words.validationFailedBody}</p>
      <p className="failure-detail">{error}</p>
      <div className="path-readout"><span>{words.outputTarget}</span><code>{outputRoot}</code></div>
      <div className="view-actions">
        <button className="secondary-button" type="button" onClick={onOpenFolder}><FolderIcon size={16} />{words.openFolder}</button>
        <button className="primary-button" type="button" onClick={onBack}>{words.backToRounds}</button>
      </div>
    </section>
  );
}

interface ResultViewProps {
  words: TextDictionary;
  result: ConversionSummary;
  warnings: string[];
  copiedTarget: CopyTarget | null;
  resultHeadingRef: RefObject<HTMLHeadingElement | null>;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenFolder: () => void;
  onBrowseManifest: () => void;
  onBack: () => void;
  onNewDemo: () => void;
  formatNumber: (value: number) => string;
  formatBytes: (value: number | string) => string;
}

export function ResultView({
  words,
  result,
  warnings,
  copiedTarget,
  resultHeadingRef,
  onCopy,
  onOpenFolder,
  onBrowseManifest,
  onBack,
  onNewDemo,
  formatNumber,
  formatBytes,
}: ResultViewProps) {
  const visibleWarnings = [...new Set(warnings)];
  const voiceState = result.voice.sidecars > 0
    ? words.voiceExportedCount.replace("{count}", formatNumber(result.voice.sidecars))
    : result.voice.requested === true
      ? words.voiceRequestedEmptyResult
      : result.voice.requested === false
        ? words.voiceNotRequested
        : words.voiceUnknown;
  const cosmeticState = result.cosmetics.files > 0
    ? words.cosmeticsExportedCount.replace("{count}", formatNumber(result.cosmetics.files))
    : result.cosmetics.requested === true
      ? words.cosmeticsRequestedEmptyResult
      : result.cosmetics.requested === false
        ? words.cosmeticsNotRequested
        : words.cosmeticsUnknown;

  return (
    <section className="task-state-view result-view" aria-labelledby="result-title">
      <header className="result-heading">
        <span className="success-symbol" aria-hidden="true"><CheckIcon size={20} /></span>
        <div>
          <h1 id="result-title" ref={resultHeadingRef} tabIndex={-1}>{words.completeTitle}</h1>
          <p>{words.completeSummary.replace("{rounds}", formatNumber(result.rounds.length)).replace("{files}", formatNumber(result.filesWritten))}</p>
        </div>
      </header>

      {visibleWarnings.length > 0 ? (
        <div className="result-warning" role="status">
          <AlertIcon size={17} />
          <div>
            <strong>{words.resultWarningsTitle}</strong>
            <p>{words.resultWarningsCount.replace("{count}", String(visibleWarnings.length))}</p>
          </div>
        </div>
      ) : null}

      <section className="result-next-step" aria-labelledby="result-next-title">
        <div>
          <span>{words.nextStep}</span>
          <h2 id="result-next-title">{words.resultReadyTitle}</h2>
          <p>{words.resultReadyBody}</p>
        </div>
        <button className="primary-button" type="button" onClick={onBrowseManifest}>
          <ReplayIcon size={15} />{words.preparePlayback}<ArrowIcon size={15} />
        </button>
      </section>

      <div className="result-capabilities" aria-label={words.archiveContents}>
        <div>
          <span>{words.voiceCapability}</span>
          <strong>{voiceState}</strong>
        </div>
        <div>
          <span>{words.cosmeticsCapability}</span>
          <strong>{cosmeticState}</strong>
        </div>
      </div>

      <details className="result-details">
        <summary>{words.resultDetails}<ChevronIcon size={15} /></summary>
        <div className="result-paths">
          <div className="result-path-row">
            <div><span>{words.output}</span><code title={result.root}>{result.root}</code></div>
            <div className="path-actions">
              <button className="secondary-button" type="button" onClick={onOpenFolder}><FolderIcon size={15} />{words.openFolder}</button>
              <button className="icon-button" type="button" onClick={() => onCopy(result.root, "output")} aria-label={words.copyPath} title={words.copyPath}>{copiedTarget === "output" ? <CheckIcon size={15} /> : <CopyIcon size={15} />}</button>
            </div>
          </div>
          <div className="result-path-row">
            <div><span>{words.manifest}</span><code title={result.manifestPath}>{result.manifestPath}</code></div>
            <button className="icon-button" type="button" onClick={() => onCopy(result.manifestPath, "manifest")} aria-label={words.copyPath} title={words.copyPath}>{copiedTarget === "manifest" ? <CheckIcon size={15} /> : <CopyIcon size={15} />}</button>
          </div>
        </div>
        <div className="result-statline">
          <span><b>{formatNumber(result.validatedFiles)}</b> {words.validatedFiles}</span>
          <span><b>{formatBytes(result.outputBytes)}</b> {words.outputSize}</span>
        </div>
      </details>

      <footer className="result-footer">
        <button className="quiet-button" type="button" onClick={onNewDemo}>{words.processAnother}</button>
        <button className="secondary-button" type="button" onClick={onBack}><ReplayIcon size={15} />{words.backToRounds}</button>
      </footer>
    </section>
  );
}
