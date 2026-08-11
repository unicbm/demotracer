/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Language, RoundOutcome } from "../types";

const ROUND_REASON_ZH: Record<string, string> = {
  bomb_exploded: "炸弹爆炸",
  bomb_defused: "炸弹拆除",
  t_killed: "T 方被全灭",
  ct_killed: "CT 方被全灭",
  time_ran_out: "时间耗尽",
  draw: "平局",
  hostage_rescued: "人质获救",
  hostages_not_rescued: "人质未获救",
  t_surrender: "T 方投降",
  ct_surrender: "CT 方投降",
  t_planted: "T 方完成目标",
  t_saved: "T 方幸存",
  ct_stopped_escape: "CT 阻止逃脱",
  terrorists_stopped: "CT 阻止 T 方",
  terrorists_not_escaped: "T 方未能逃脱",
  vip_escaped: "VIP 逃脱",
  vip_killed: "VIP 被击杀",
  vip_not_escaped: "VIP 未能逃脱",
  ct_reached_hostage: "CT 到达人质点",
};

const ROUND_REASON_EN: Record<string, string> = {
  bomb_exploded: "Bomb exploded",
  bomb_defused: "Bomb defused",
  t_killed: "T side eliminated",
  ct_killed: "CT side eliminated",
  time_ran_out: "Time expired",
  draw: "Draw",
  hostage_rescued: "Hostage rescued",
  hostages_not_rescued: "Hostages not rescued",
  t_surrender: "T side surrendered",
  ct_surrender: "CT side surrendered",
  t_planted: "T objective completed",
  t_saved: "T side survived",
  ct_stopped_escape: "CT stopped the escape",
  terrorists_stopped: "CT stopped T side",
  terrorists_not_escaped: "T side did not escape",
  vip_escaped: "VIP escaped",
  vip_killed: "VIP killed",
  vip_not_escaped: "VIP did not escape",
  ct_reached_hostage: "CT reached the hostage",
};

export function roundOutcomeDescription(
  outcome: RoundOutcome | undefined,
  language: Language,
  teamAName: string,
  teamBName: string,
): string | null {
  if (!outcome) return null;
  const winner = outcome.winnerTeam === "a"
    ? teamAName
    : outcome.winnerTeam === "b"
      ? teamBName
      : outcome.winnerSide === 2 ? "T" : outcome.winnerSide === 3 ? "CT" : "";
  const reasonKey = outcome.reason?.trim().toLowerCase() ?? "";
  const reasons = language === "zh" ? ROUND_REASON_ZH : ROUND_REASON_EN;
  const reason = reasons[reasonKey] ?? (reasonKey && reasonKey !== "still_in_progress" && reasonKey !== "game_start" ? outcome.reason : null);
  if (winner && reason) return `${winner} · ${reason}`;
  return winner || reason || null;
}
