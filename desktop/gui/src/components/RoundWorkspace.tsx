/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useMemo } from "react";
import { Alert, Badge, Button, Group, Paper, Stack, Switch, Text } from "@mantine/core";
import type { TextDictionary } from "../i18n";
import type { AnalysisResult, RoundInfo } from "../types";
import { AlertIcon } from "../icons";
import { RoundTable, type RoundTableLabels } from "./RoundTable";

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
    duration: words.durationColumn,
    teams: words.teamsColumn,
    status: words.statusColumn,
    problems: words.issuesColumn,
    recommended: words.recommended,
    partial: words.partial,
    suspicious: words.suspicious,
    noProblems: words.noIssues,
    suspiciousLocked: words.suspiciousLocked,
  };
  const defaultSelectedCount = useMemo(
    () => analysis.rounds.filter((round) => round.selectedByDefault).length,
    [analysis.rounds],
  );
  const limitedReplay = analysis.replayInput.status === "limited";
  const missingReplayInputs = [
    analysis.replayInput.commandRows === 0 ? words.limitedReplayMissingCommands : null,
    analysis.replayInput.actionRows === 0 ? words.limitedReplayMissingActions : null,
    analysis.replayInput.subtickRows === 0 ? words.limitedReplayMissingSubticks : null,
    analysis.replayInput.attackHistoryRows === 0 ? words.limitedReplayMissingAttackHistory : null,
  ].filter((label): label is string => Boolean(label));
  return (
    <section className="round-workspace" aria-label={words.rounds}>
      <div className="round-selection-panel">
        <Group component="header" className="round-selection-heading" justify="space-between" gap="md" wrap="nowrap">
          <div className="round-selection-heading-copy">
            <h1>{words.roundSelectionTitle}</h1>
            <p>{words.usableRoundCount.replace("{count}", formatNumber(defaultSelectedCount))}</p>
          </div>
          <Badge color="blue" variant="light" size="md" radius="sm">
            {words.roundSelectionCount
              .replace("{selected}", formatNumber(selectedRounds.size))
              .replace("{total}", formatNumber(analysis.rounds.length))}
          </Badge>
        </Group>

        <Stack gap="sm" p="md">
          {limitedReplay ? (
            <Alert
              color="yellow"
              variant="light"
              radius="md"
              icon={<AlertIcon size={18} />}
              title={words.limitedReplayTitle}
            >
              <Text size="sm">{words.limitedReplayBody}</Text>
              <Group gap={6} mt="xs" wrap="wrap" aria-label={words.limitedReplayMissingLabel}>
                <Text size="xs" c="dimmed">{words.limitedReplayMissingLabel}:</Text>
                {missingReplayInputs.map((label, index) => (
                  <Group gap={6} wrap="nowrap" key={label}>
                    {index > 0 ? <Text size="xs" c="dimmed" aria-hidden="true">·</Text> : null}
                    <Text size="xs" fw={500}>{label}</Text>
                  </Group>
                ))}
              </Group>
            </Alert>
          ) : null}

          <Paper className="round-selection-paper" withBorder radius="md">
            <Group justify="space-between" gap="sm" p="sm" wrap="wrap">
              <Group gap="xs" wrap="wrap">
                <Text size="sm" fw={600}>{words.adjustRoundSelection}</Text>
                <Badge color="green" variant="light" size="sm">
                  {words.recommended} {formatNumber(recommendedCount)}
                </Badge>
                {partialCount > 0 ? (
                  <Badge color="blue" variant="light" size="sm">
                    {words.partial} {formatNumber(partialCount)}
                  </Badge>
                ) : null}
                {suspiciousCount > 0 ? (
                  <Badge color="yellow" variant="light" size="sm">
                    {words.suspicious} {formatNumber(suspiciousCount)}
                  </Badge>
                ) : null}
              </Group>

              <Group gap="xs" wrap="wrap">
                <Button variant="default" size="xs" disabled={convertPending} onClick={onRestoreRecommended}>
                  {words.restoreRecommended}
                </Button>
                <Button variant="subtle" color="gray" size="xs" disabled={convertPending} onClick={onClearSelection}>
                  {words.clearSelection}
                </Button>
                {suspiciousCount > 0 ? (
                  <Switch
                    className="round-suspicious-switch"
                    size="sm"
                    label={words.allowSuspicious}
                    labelPosition="left"
                    checked={allowSuspicious}
                    disabled={convertPending}
                    onChange={(event) => onAllowSuspiciousChange(event.currentTarget.checked)}
                  />
                ) : null}
              </Group>
            </Group>

            <RoundTable
              labels={labels}
              rounds={analysis.rounds}
              selectedRounds={selectedRounds}
              allowSuspicious={allowSuspicious}
              disabled={convertPending}
              onToggle={onToggleRound}
            />
          </Paper>
        </Stack>
      </div>
    </section>
  );
}
