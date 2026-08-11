/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useMemo } from "react";
import { Group, Text } from "@mantine/core";
import type { TextDictionary } from "../i18n";
import type { AnalysisResult, RoundInfo } from "../types";
import { RoundTable, type RoundTableLabels } from "./RoundTable";
import { SwitchControl } from "./SwitchControl";

interface RoundWorkspaceProps {
  words: TextDictionary;
  analysis: AnalysisResult;
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  convertPending: boolean;
  onToggleRound: (round: RoundInfo) => void;
  onRestoreRecommended: () => void;
  onClearSelection: () => void;
  onAllowSuspiciousChange: (checked: boolean) => void;
  formatNumber: (value: number) => string;
}

export function RoundWorkspace({
  words,
  analysis,
  selectedRounds,
  allowSuspicious,
  convertPending,
  onToggleRound,
  onRestoreRecommended,
  onClearSelection,
  onAllowSuspiciousChange,
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
  const defaultSelectedCount = useMemo(
    () => analysis.rounds.filter((round) => round.selectedByDefault).length,
    [analysis.rounds],
  );
  return (
    <section className="round-workspace" aria-label={words.rounds}>
      <div className="round-selection-panel">
        <header className="round-selection-heading">
          <div className="round-selection-heading-copy">
            <h1>{words.roundSelectionTitle}</h1>
            <p>{words.usableRoundCount.replace("{count}", formatNumber(defaultSelectedCount))}</p>
          </div>
          <span className="round-selection-count">
            <strong>{defaultSelectedCount}</strong>
            <small>/ {analysis.rounds.length}</small>
          </span>
        </header>
        <div className="round-selection-subheading">
          <Group justify="space-between" gap="md" wrap="nowrap">
            <Text size="sm" fw={600}>{words.adjustRoundSelection}</Text>
            <Text size="xs" c="var(--text-secondary)">
              {selectedRounds.size > 0
                ? words.selectedCount.replace("{count}", formatNumber(selectedRounds.size))
                : words.selectAtLeastOne}
            </Text>
          </Group>
        </div>
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
              <SwitchControl
                checked={allowSuspicious}
                label={words.allowSuspicious}
                disabled={convertPending}
                onChange={onAllowSuspiciousChange}
              />
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
        />
      </div>
    </section>
  );
}
