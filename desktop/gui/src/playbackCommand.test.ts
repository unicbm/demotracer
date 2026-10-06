/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { buildPlaybackCommand, DEFAULT_PLAYBACK_ADVANCED_OPTIONS, type PlaybackPresetOptions } from "./playbackCommand.ts";

function preset(friendlyFire: PlaybackPresetOptions["friendlyFire"]): PlaybackPresetOptions {
  return {
    weapons: true,
    cosmetics: false,
    steamIdentity: false,
    avatar: false,
    voice: false,
    playoff: false,
    ...DEFAULT_PLAYBACK_ADVANCED_OPTIONS,
    friendlyFire,
  };
}

describe("playback command friendly fire", () => {
  it("explicitly disables friendly fire by default", () => {
    assert.equal(DEFAULT_PLAYBACK_ADVANCED_OPTIONS.friendlyFire, "off");
    assert.equal(
      buildPlaybackCommand("dtr_go 3", 1, preset(DEFAULT_PLAYBACK_ADVANCED_OPTIONS.friendlyFire)),
      "mp_friendlyfire 0; dtr_preset 0x01; dtr_go 3",
    );
  });

  it("enables friendly fire only when selected", () => {
    assert.equal(
      buildPlaybackCommand("dtr_go 3", 1, preset("on")),
      "mp_friendlyfire 1; dtr_preset 0x01; dtr_go 3",
    );
  });
});
