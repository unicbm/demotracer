/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildReplayRetentionCommand,
  canPrioritizeReplayRoster,
  encodeReplayRetentionPermutation,
  moveReplayRetentionPlayer,
  normalizeReplayRetentionOrder,
} from "./replayRetention.ts";

const a = ["76561198000000001", "76561198000000002", "76561198000000003"];
const b = ["76561198000000011", "76561198000000012"];

describe("replay retention priority", () => {
  it("keeps a stored permutation and rejects stale membership", () => {
    assert.deepEqual(normalizeReplayRetentionOrder(a, [a[2], a[0], a[1]]), [a[2], a[0], a[1]]);
    assert.deepEqual(normalizeReplayRetentionOrder(a, [a[0], a[1], b[0]]), a);
  });

  it("moves exactly one player", () => {
    assert.deepEqual(moveReplayRetentionPlayer(a, 2, 0), [a[2], a[0], a[1]]);
  });

  it("builds one manifest-plan command", () => {
    assert.equal(buildReplayRetentionCommand({ a, b }), `dtr_retain ${a.join(",")} ${b.join(",")}`);
  });

  it("does not emit five-player retention commands for a larger casual roster", () => {
    const casual = Array.from({ length: 10 }, (_, index) => `765611980000000${String(index + 1).padStart(2, "0")}`);
    assert.equal(canPrioritizeReplayRoster(casual), false);
    assert.equal(buildReplayRetentionCommand({ a: casual, b: [] }), null);
  });

  it("encodes each complete five-player side as one permutation number", () => {
    const teamA = [
      "76561198000000001",
      "76561198000000002",
      "76561198000000003",
      "76561198000000004",
      "76561198000000005",
    ];
    const teamB = [
      "76561198000000011",
      "76561198000000012",
      "76561198000000013",
      "76561198000000014",
      "76561198000000015",
    ];
    assert.equal(
      buildReplayRetentionCommand(
        { a: [teamA[2], teamA[0], teamA[4], teamA[1], teamA[3]], b: [...teamB].reverse() },
        { t: [...teamA].reverse(), ct: teamB },
      ),
      "dtr_retain 52 119",
    );
  });

  it("assigns a unique code to all 120 five-player permutations", () => {
    const canonical = [
      "76561198000000001",
      "76561198000000002",
      "76561198000000003",
      "76561198000000004",
      "76561198000000005",
    ];
    const permutations = (items: string[]): string[][] => items.length === 0
      ? [[]]
      : items.flatMap((item, index) =>
        permutations(items.filter((_, candidate) => candidate !== index))
          .map((suffix) => [item, ...suffix]));
    const codes = permutations(canonical)
      .map((permutation) => encodeReplayRetentionPermutation(canonical, permutation));
    assert.equal(new Set(codes).size, 120);
    assert.deepEqual([...new Set(codes)].sort((left, right) => (left ?? 0) - (right ?? 0)), Array.from({ length: 120 }, (_, index) => index));
  });

  it("falls back to SteamIDs when the selected round is not a complete 5v5 roster", () => {
    assert.equal(
      buildReplayRetentionCommand({ a, b }, { t: a, ct: b }),
      `dtr_retain ${a.join(",")} ${b.join(",")}`,
    );
  });

  it("encodes the selected T and CT sides after a halftime swap", () => {
    const teamA = Array.from({ length: 5 }, (_, index) => `7656119800000000${index + 1}`);
    const teamB = Array.from({ length: 5 }, (_, index) => `7656119800000001${index + 1}`);
    assert.equal(
      buildReplayRetentionCommand(
        { a: [...teamA].reverse(), b: teamB },
        { t: teamB, ct: teamA },
      ),
      "dtr_retain 0 119",
    );
  });
});
