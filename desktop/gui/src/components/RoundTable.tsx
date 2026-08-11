/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { type KeyboardEvent, useRef } from "react";
import type { RoundInfo } from "../types";

export interface RoundTableLabels {
  caption: string;
  select: string;
  round: string;
  status: string;
  duration: string;
  result: string;
  teams: string;
  validRows: string;
  problems: string;
  recommended: string;
  partial: string;
  suspicious: string;
  noProblems: string;
  suspiciousLocked: string;
}

interface RoundTableProps {
  labels: RoundTableLabels;
  rounds: RoundInfo[];
  selectedRounds: Set<number>;
  allowSuspicious: boolean;
  disabled: boolean;
  onToggle: (round: RoundInfo) => void;
  formatNumber?: (value: number) => string;
  formatDuration?: (seconds: number) => string;
  roundOutcomeLabel?: (round: number) => string | null;
}

function defaultFormatDuration(seconds: number): string {
  const wholeSeconds = Math.max(0, Math.round(seconds));
  const minutes = Math.floor(wholeSeconds / 60);
  return `${minutes}:${String(wholeSeconds % 60).padStart(2, "0")}`;
}

export function RoundTable({
  labels,
  rounds,
  selectedRounds,
  allowSuspicious,
  disabled,
  onToggle,
  formatNumber = (value) => value.toLocaleString(),
  formatDuration = defaultFormatDuration,
  roundOutcomeLabel,
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
    <div className="round-list-scroll">
      <table className="round-data-table round-export-table" ref={tableRef}>
        <caption className="sr-only">{labels.caption}</caption>
        <thead>
          <tr>
            <th className="round-select-column" scope="col"><span className="sr-only">{labels.select}</span></th>
            <th scope="col">{labels.round}</th>
            <th scope="col">{labels.status}</th>
            <th scope="col">{labels.duration}</th>
            <th scope="col">{labels.result}</th>
            <th scope="col">{labels.teams}</th>
            <th scope="col">{labels.validRows}</th>
            <th scope="col">{labels.problems}</th>
          </tr>
        </thead>
        <tbody>
          {rounds.map((round) => {
            const suspicious = round.status === "suspicious";
            const partial = round.status === "partial";
            const selectionDisabled = suspicious && !allowSuspicious;
            const selected = selectedRounds.has(round.round);
            const statusLabel = suspicious ? labels.suspicious : partial ? labels.partial : labels.recommended;
            const outcomeLabel = roundOutcomeLabel?.(round.round) ?? "—";
            return (
              <tr
                className={`round-data-row${selected ? " is-selected" : ""}${selectionDisabled ? " is-selection-locked" : ""}`}
                key={round.round}
              >
                <td className="round-select-cell">
                  <input
                    type="checkbox"
                    data-round-select="true"
                    data-round-number={round.round}
                    checked={selected}
                    disabled={disabled || selectionDisabled}
                    aria-label={`${labels.select} ${labels.round} ${round.round}, ${statusLabel}${selectionDisabled ? `, ${labels.suspiciousLocked}` : ""}`}
                    title={selectionDisabled ? labels.suspiciousLocked : undefined}
                    onChange={() => onToggle(round)}
                    onKeyDown={moveCheckboxFocus}
                  />
                </td>
                <th className="round-number-cell" scope="row">{String(round.round).padStart(2, "0")}</th>
                <td><span className={`round-list-status is-${round.status}`}><i aria-hidden="true" />{statusLabel}</span></td>
                <td className="round-duration-cell">{formatDuration(round.durationSeconds)}</td>
                <td className="round-outcome-cell" title={outcomeLabel}>{outcomeLabel}</td>
                <td className="round-team-count">T {round.tPlayers} / CT {round.ctPlayers}</td>
                <td className="round-record-count">{formatNumber(round.validRows)}</td>
                <td className="round-issue-cell" title={round.problems.join(" · ")}>
                  {round.problems.length > 0 ? round.problems.join(" · ") : labels.noProblems}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
