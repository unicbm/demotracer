/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

export interface ReplayRetentionOrders {
  a: string[];
  b: string[];
}

export interface ReplayRetentionRoundSides {
  t: string[];
  ct: string[];
}

const STEAM_ID64_PATTERN = /^[1-9]\d{16}$/;
const STANDARD_REPLAY_TEAM_SIZE = 5;

export function canPrioritizeReplayRoster(steamIds: readonly string[]): boolean {
  return steamIds.length > 1
    && steamIds.length <= STANDARD_REPLAY_TEAM_SIZE
    && steamIds.every((steamId) => STEAM_ID64_PATTERN.test(steamId))
    && new Set(steamIds).size === steamIds.length;
}

export function normalizeReplayRetentionOrder(
  steamIds: readonly string[],
  preferred: readonly string[] | null | undefined,
): string[] {
  const defaults = [...steamIds];
  if (!canPrioritizeReplayRoster(defaults) || !preferred || preferred.length !== defaults.length) {
    return defaults;
  }
  const expected = new Set(defaults);
  return new Set(preferred).size === expected.size && preferred.every((steamId) => expected.has(steamId))
    ? [...preferred]
    : defaults;
}

export function moveReplayRetentionPlayer(
  order: readonly string[],
  fromIndex: number,
  toIndex: number,
): string[] {
  if (fromIndex === toIndex
    || fromIndex < 0
    || toIndex < 0
    || fromIndex >= order.length
    || toIndex >= order.length) {
    return [...order];
  }
  const next = [...order];
  const [moved] = next.splice(fromIndex, 1);
  next.splice(toIndex, 0, moved);
  return next;
}

export function orderReplayRoster<T extends { steamId: string }>(
  players: readonly T[],
  preferred: readonly string[] | null | undefined,
): T[] {
  const order = normalizeReplayRetentionOrder(players.map((player) => player.steamId), preferred);
  const rank = new Map(order.map((steamId, index) => [steamId, index]));
  return [...players].sort((left, right) =>
    (rank.get(left.steamId) ?? Number.MAX_SAFE_INTEGER)
    - (rank.get(right.steamId) ?? Number.MAX_SAFE_INTEGER));
}

export function encodeReplayRetentionPermutation(
  canonicalSteamIds: readonly string[],
  preferredSteamIds: readonly string[],
): number | null {
  if (canonicalSteamIds.length !== STANDARD_REPLAY_TEAM_SIZE
    || preferredSteamIds.length !== canonicalSteamIds.length
    || !canPrioritizeReplayRoster(canonicalSteamIds)
    || !canPrioritizeReplayRoster(preferredSteamIds)
    || !preferredSteamIds.every((steamId) => canonicalSteamIds.includes(steamId))) {
    return null;
  }

  const remaining = [...canonicalSteamIds];
  let code = 0;
  for (let index = 0; index < preferredSteamIds.length; index += 1) {
    const position = remaining.indexOf(preferredSteamIds[index]);
    if (position < 0) return null;
    code = code * (remaining.length) + position;
    remaining.splice(position, 1);
  }
  return code;
}

function buildCompactReplayRetentionCommand(
  orders: ReplayRetentionOrders,
  roundSides: ReplayRetentionRoundSides,
): string | null {
  const canonicalT = [...roundSides.t].sort();
  const canonicalCt = [...roundSides.ct].sort();
  const orderSets = [orders.a, orders.b].filter(
    (steamIds) => steamIds.length === STANDARD_REPLAY_TEAM_SIZE && canPrioritizeReplayRoster(steamIds),
  );
  if (orderSets.length !== 2
    || canonicalT.length !== STANDARD_REPLAY_TEAM_SIZE
    || canonicalCt.length !== STANDARD_REPLAY_TEAM_SIZE
    || !canPrioritizeReplayRoster(canonicalT)
    || !canPrioritizeReplayRoster(canonicalCt)
    || new Set([...canonicalT, ...canonicalCt]).size !== STANDARD_REPLAY_TEAM_SIZE * 2) {
    return null;
  }

  const preferredForSide = (canonical: readonly string[]): string[] | null => {
    const canonicalSet = new Set(canonical);
    const matching = orderSets.find(
      (steamIds) => steamIds.every((steamId) => canonicalSet.has(steamId)),
    );
    return matching ? matching.filter((steamId) => canonicalSet.has(steamId)) : null;
  };
  const preferredT = preferredForSide(canonicalT);
  const preferredCt = preferredForSide(canonicalCt);
  if (!preferredT || !preferredCt) return null;

  const tCode = encodeReplayRetentionPermutation(canonicalT, preferredT);
  const ctCode = encodeReplayRetentionPermutation(canonicalCt, preferredCt);
  return tCode === null || ctCode === null ? null : `dtr_retain ${tCode} ${ctCode}`;
}

export function buildReplayRetentionCommand(
  orders: ReplayRetentionOrders,
  roundSides?: ReplayRetentionRoundSides | null,
): string | null {
  if (roundSides) {
    const compact = buildCompactReplayRetentionCommand(orders, roundSides);
    if (compact) return compact;
  }

  const first = canPrioritizeReplayRoster(orders.a) ? orders.a : [];
  const second = canPrioritizeReplayRoster(orders.b) ? orders.b : [];
  if (first.length === 0 && second.length === 0) return null;
  const combined = [...first, ...second];
  if (new Set(combined).size !== combined.length) return null;
  return `dtr_retain ${first.join(",") || "-"} ${second.join(",") || "-"}`;
}

export function replayRetentionStorageKey(archiveIdentity: string): string {
  return `demotracer:replay-retention:v1:${archiveIdentity.trim().toLocaleLowerCase()}`;
}
