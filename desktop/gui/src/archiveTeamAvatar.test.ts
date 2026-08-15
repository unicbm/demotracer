/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { teamAvatarEvidence } from "./components/archiveTeamAvatar.ts";

const players = [
  { steamId: "76561198000000001" },
  { steamId: "76561198000000002" },
  { steamId: "76561198000000003" },
];

describe("archive team avatar evidence", () => {
  it("accepts one shared manifest image backed by multiple team players", () => {
    const evidence = teamAvatarEvidence({
      avatarOverrides: [
        { steamId: players[0].steamId, path: "avatars/team.png", sha256: "ab".repeat(32) },
        { steamId: players[1].steamId, path: "avatars/team.png", sha256: "ab".repeat(32) },
      ],
    }, players);

    assert.equal(evidence?.path, "avatars/team.png");
  });

  it("rejects a single-player override as team identity", () => {
    const evidence = teamAvatarEvidence({
      avatarOverrides: [
        { steamId: players[0].steamId, path: "avatars/player.png", sha256: "ab".repeat(32) },
      ],
    }, players);

    assert.equal(evidence, null);
  });

  it("does not count duplicate overrides for one player as team evidence", () => {
    const evidence = teamAvatarEvidence({
      avatarOverrides: [
        { steamId: players[0].steamId, path: "avatars/team.png", sha256: "ab".repeat(32) },
        { steamId: players[0].steamId, path: "avatars/team-copy.png", sha256: "ab".repeat(32) },
      ],
    }, players);

    assert.equal(evidence, null);
  });

  it("rejects conflicting manifest images within one team", () => {
    const evidence = teamAvatarEvidence({
      avatarOverrides: [
        { steamId: players[0].steamId, path: "avatars/first.png", sha256: "ab".repeat(32) },
        { steamId: players[1].steamId, path: "avatars/second.png", sha256: "cd".repeat(32) },
      ],
    }, players);

    assert.equal(evidence, null);
  });
});
