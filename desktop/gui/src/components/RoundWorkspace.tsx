/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useMemo } from "react";
import { ArrowIcon, FolderIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import type { InventorySimulatorItem } from "../inventorySimulator";
import { useInventorySimulatorSelection } from "../inventorySimulatorSelection";
import type { AnalysisResult, Language, RoundInfo } from "../types";
import { AnalysisOverview, analysisRoster } from "./AnalysisOverview";
import { PlayerAnalysisWorkspace, type PlayerAnalysisTeam } from "./PlayerAnalysisWorkspace";
import type { PlayerSelection } from "./PlayerRoster";
import { RoundTable, type RoundTableLabels } from "./RoundTable";
import { useSteamProfiles } from "./SteamProfile";
import type { CopyTarget } from "./TaskViews";

interface RoundWorkspaceProps {
  words: TextDictionary;
  language: Language;
  analysis: AnalysisResult;
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  outputDir: string;
  outputRoot: string;
  copiedTarget: CopyTarget | null;
  selectedPlayer: PlayerSelection | null;
  convertPending: boolean;
  onToggleRound: (round: RoundInfo) => void;
  onRestoreRecommended: () => void;
  onClearSelection: () => void;
  onAllowSuspiciousChange: (checked: boolean) => void;
  onChooseOutput: () => void;
  onConvert: () => void;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onClosePlayer: () => void;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
  onSyncInventorySimulator: (items: InventorySimulatorItem[], language: Language) => Promise<void>;
  formatNumber: (value: number) => string;
}

function compactPath(path: string, limit = 72): string {
  if (path.length <= limit) return path;
  const keep = Math.floor((limit - 1) / 2);
  return `${path.slice(0, keep)}…${path.slice(-keep)}`;
}

export function RoundWorkspace({
  words,
  language,
  analysis,
  selectedRounds,
  allowSuspicious,
  outputDir,
  outputRoot,
  copiedTarget,
  selectedPlayer,
  convertPending,
  onToggleRound,
  onRestoreRecommended,
  onClearSelection,
  onAllowSuspiciousChange,
  onChooseOutput,
  onConvert,
  onSelectPlayer,
  onClosePlayer,
  onCopy,
  onOpenExternal,
  onSyncInventorySimulator,
  formatNumber,
}: RoundWorkspaceProps) {
  const recommendedCount = useMemo(() => analysis.rounds.filter((round) => round.status === "recommended").length, [analysis.rounds]);
  const partialCount = useMemo(() => analysis.rounds.filter((round) => round.status === "partial").length, [analysis.rounds]);
  const suspiciousCount = analysis.rounds.length - recommendedCount - partialCount;
  const labels: RoundTableLabels = {
    caption: words.rounds,
    select: words.selectColumn,
    round: words.roundColumn,
    status: words.statusColumn,
    duration: words.durationColumn,
    teams: words.teamsColumn,
    validRows: words.validRowsColumn,
    problems: words.issuesColumn,
    recommended: words.recommended,
    partial: words.partial,
    suspicious: words.suspicious,
    noProblems: words.noIssues,
    suspiciousLocked: words.suspiciousLocked,
  };
  const summary = words.roundSummary
    .replace("{total}", formatNumber(analysis.rounds.length))
    .replace("{recommended}", formatNumber(recommendedCount))
    .replace("{partial}", formatNumber(partialCount))
    .replace("{suspicious}", formatNumber(suspiciousCount));
  const canConvert = selectedRounds.size > 0 && Boolean(outputDir) && !convertPending;
  const roster = analysisRoster(analysis, words);
  const steamProfiles = useSteamProfiles(analysis.players.map((player) => player.steamId));
  const inventorySelection = useInventorySimulatorSelection(
    analysis.demoSha256 || analysis.analysisId,
    onSyncInventorySimulator,
  );
  const playerTeams: PlayerAnalysisTeam[] = [
    { id: "a", name: roster.teamAName, players: roster.teamA },
    { id: "b", name: roster.teamBName, players: roster.teamB },
    ...(roster.unassigned.length > 0 ? [{ id: "unknown", name: words.unassignedPlayers, players: roster.unassigned }] : []),
  ];

  if (selectedPlayer !== null) {
    return (
      <PlayerAnalysisWorkspace
        words={words}
        language={language}
        teams={playerTeams}
        steamProfiles={steamProfiles}
        selectedPlayer={selectedPlayer}
        copiedTarget={copiedTarget}
        onSelectPlayer={onSelectPlayer}
        onBack={onClosePlayer}
        onCopy={onCopy}
        onOpenExternal={onOpenExternal}
        inventorySelection={inventorySelection}
      />
    );
  }

  return (
    <section className="round-workspace" aria-label={words.rounds}>
      <AnalysisOverview
        analysis={analysis}
        words={words}
        steamProfiles={steamProfiles}
        onSelectPlayer={onSelectPlayer}
      />
      <div className="round-selection-panel">
        <header className="round-selection-heading">
          <h1>{words.roundSelectionTitle}</h1>
          <span className="round-selection-count">
            <strong>{selectedRounds.size}</strong>
            <small>/ {analysis.rounds.length}</small>
          </span>
        </header>
        <div className="round-toolbar">
          <strong className="round-summary">{summary}</strong>
          <div className="round-batch-actions">
            <button className="text-button" type="button" disabled={convertPending} onClick={onRestoreRecommended}>{words.restoreRecommended}</button>
            <button className="text-button" type="button" disabled={convertPending} onClick={onClearSelection}>{words.clearSelection}</button>
          </div>
          {suspiciousCount > 0 ? (
            <div className="allow-suspicious-control">
              <span className="wide-label">{words.allowSuspicious}</span>
              <span className="compact-label">{words.allowSuspiciousShort}</span>
              <button
                className="switch-control"
                type="button"
                role="switch"
                aria-checked={allowSuspicious}
                aria-label={words.allowSuspicious}
                disabled={convertPending}
                onClick={() => onAllowSuspiciousChange(!allowSuspicious)}
              >
                <span />
              </button>
            </div>
          ) : <span className="toolbar-spacer" />}
        </div>

        <RoundTable
          labels={labels}
          rounds={analysis.rounds}
          selectedRounds={selectedRounds}
          allowSuspicious={allowSuspicious}
          disabled={convertPending}
          onToggle={onToggleRound}
          formatNumber={formatNumber}
        />
      </div>

      <footer className="export-status-bar">
        <div className="selection-status" aria-live="polite">
          <strong>{selectedRounds.size > 0 ? words.selectedCount.replace("{count}", String(selectedRounds.size)) : words.selectAtLeastOne}</strong>
          <div className="output-status">
            <span>{words.outputParent}</span>
            <code title={outputDir}>{outputDir ? compactPath(outputDir) : words.notSelected}</code>
            {outputRoot ? <small title={outputRoot}>{words.outputTarget}: {compactPath(outputRoot)}</small> : null}
          </div>
        </div>
        <div className="export-actions">
          <button className="secondary-button output-button" type="button" onClick={onChooseOutput} disabled={convertPending}>
            <FolderIcon size={15} />
            {outputDir ? words.changeOutput : words.chooseOutput}
          </button>
          <button className="primary-button convert-button" type="button" disabled={!canConvert} aria-busy={convertPending} onClick={onConvert}>
            {convertPending ? words.preparing : words.convertCount.replace("{count}", String(selectedRounds.size))}
            <ArrowIcon size={16} />
          </button>
        </div>
      </footer>
    </section>
  );
}
