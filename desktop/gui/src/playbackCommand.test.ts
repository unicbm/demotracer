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

describe("playback command friendly-fire evidence", () => {
  it("defaults to friendly fire off even when the demo observed team damage", () => {
    assert.equal(DEFAULT_PLAYBACK_ADVANCED_OPTIONS.friendlyFire, "off");
    assert.equal(
      buildPlaybackCommand("dtr_go 3", 1, preset(DEFAULT_PLAYBACK_ADVANCED_OPTIONS.friendlyFire), null, {
        enabled: true,
        evidence: "observedDamage",
        damageEvents: 6,
        damage: 15,
      }),
      "mp_friendlyfire 0; dtr_preset 0x01; dtr_go 3",
    );
  });

  it("uses a known demo conclusion in auto mode", () => {
    assert.equal(
      buildPlaybackCommand("dtr_go 3", 1, preset("auto"), null, {
        enabled: true,
        evidence: "observedDamage",
        damageEvents: 1,
        damage: 12,
      }),
      "mp_friendlyfire 1; dtr_preset 0x01; dtr_go 3",
    );
  });

  it("emits no guess when the demo conclusion is unknown", () => {
    assert.equal(
      buildPlaybackCommand("dtr_go 3", 1, preset("auto"), null, {
        enabled: null,
        evidence: "unavailable",
        damageEvents: 0,
        damage: 0,
      }),
      "dtr_preset 0x01; dtr_go 3",
    );
  });

  it("lets an explicit user override replace the demo conclusion", () => {
    assert.equal(
      buildPlaybackCommand("dtr_go 3", 1, preset("off"), null, {
        enabled: true,
        evidence: "serverConVar",
        damageEvents: 0,
        damage: 0,
      }),
      "mp_friendlyfire 0; dtr_preset 0x01; dtr_go 3",
    );
  });
});
