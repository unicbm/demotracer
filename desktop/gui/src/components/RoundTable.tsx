/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { type KeyboardEvent, useRef } from "react";
import { Badge, Checkbox, Table, Text } from "@mantine/core";
import type { RoundInfo } from "../types";

export interface RoundTableLabels {
  caption: string;
  select: string;
  round: string;
  duration: string;
  teams: string;
  status: string;
  problems: string;
  recommended: string;
  partial: string;
  suspicious: string;
  noProblems: string;
  suspiciousLocked: string;
}

function formatDuration(seconds: number): string {
  const totalSeconds = Math.max(0, Math.round(seconds));
  return `${Math.floor(totalSeconds / 60)}:${String(totalSeconds % 60).padStart(2, "0")}`;
}

interface RoundTableProps {
  labels: RoundTableLabels;
  rounds: RoundInfo[];
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  disabled: boolean;
  onToggle: (round: RoundInfo) => void;
}

export function RoundTable({
  labels,
  rounds,
  selectedRounds,
  allowSuspicious,
  disabled,
  onToggle,
}: RoundTableProps) {
  const tableRef = useRef<HTMLTableElement>(null);

  function moveCheckboxFocus(event: KeyboardEvent<HTMLInputElement>) {
    if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
    const checkboxes = Array.from(
      tableRef.current?.querySelectorAll<HTMLInputElement>('input[data-round-select="true"]:not(:disabled)') ?? [],
    );
    const currentIndex = checkboxes.indexOf(event.currentTarget);
    if (currentIndex < 0 || checkboxes.length === 0) return;
    const nextIndex = event.key === "Home"
      ? 0
      : event.key === "End"
        ? checkboxes.length - 1
        : event.key === "ArrowUp"
          ? Math.max(0, currentIndex - 1)
          : Math.min(checkboxes.length - 1, currentIndex + 1);
    event.preventDefault();
    checkboxes[nextIndex]?.focus();
  }

  return (
    <Table.ScrollContainer minWidth={640}>
      <Table
        className="round-mantine-table"
        ref={tableRef}
        striped
        highlightOnHover
        horizontalSpacing="md"
        verticalSpacing="sm"
        tabularNums
      >
        <Table.Caption className="sr-only">{labels.caption}</Table.Caption>
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={52}><span className="sr-only">{labels.select}</span></Table.Th>
            <Table.Th w={72}>{labels.round}</Table.Th>
            <Table.Th w={88}>{labels.duration}</Table.Th>
            <Table.Th w={88}>{labels.teams}</Table.Th>
            <Table.Th w={144}>{labels.status}</Table.Th>
            <Table.Th>{labels.problems}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rounds.map((round) => {
            const suspicious = round.status === "suspicious";
            const partial = round.status === "partial";
            const selectionDisabled = suspicious && !allowSuspicious;
            const selected = selectedRounds.has(round.round);
            const statusLabel = suspicious ? labels.suspicious : partial ? labels.partial : labels.recommended;
            const statusColor = suspicious ? "yellow" : partial ? "blue" : "green";
            return (
              <Table.Tr
                key={round.round}
                c={selectionDisabled ? "dimmed" : undefined}
                opacity={selectionDisabled ? 0.6 : 1}
              >
                <Table.Td>
                  <Checkbox
                    size="sm"
                    data-round-select="true"
                    data-round-number={round.round}
                    checked={selected}
                    disabled={disabled || selectionDisabled}
                    aria-label={`${labels.select} ${labels.round} ${round.round}, ${statusLabel}${selectionDisabled ? `, ${labels.suspiciousLocked}` : ""}`}
                    title={selectionDisabled ? labels.suspiciousLocked : undefined}
                    onChange={() => onToggle(round)}
                    onKeyDown={moveCheckboxFocus}
                  />
                </Table.Td>
                <Table.Th scope="row">
                  <Text component="span" ff="monospace" fw={600} size="sm">
                    {String(round.round).padStart(2, "0")}
                  </Text>
                </Table.Th>
                <Table.Td>{formatDuration(round.durationSeconds)}</Table.Td>
                <Table.Td>{round.tPlayers}v{round.ctPlayers}</Table.Td>
                <Table.Td>
                  <Badge color={statusColor} variant="light" size="sm">{statusLabel}</Badge>
                </Table.Td>
                <Table.Td title={round.problems.join(" · ")}>
                  <Text component="span" size="sm" c="dimmed" lineClamp={1}>
                    {round.problems.length > 0 ? round.problems.join(" · ") : labels.noProblems}
                  </Text>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
