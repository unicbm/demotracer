/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import {
  normalizeIgnoredUpdateVersions,
  normalizePendingPlaybackUpdate,
  updateVersionIsIgnored,
} from "./updatePrompt.ts";

test("normalizes independently ignored GUI and playback versions", () => {
  assert.deepEqual(
    normalizeIgnoredUpdateVersions('{"gui":" 1.2.0 ","playback":"1.1.1","extra":true}'),
    { gui: "1.2.0", playback: "1.1.1" },
  );
  assert.deepEqual(normalizeIgnoredUpdateVersions("not-json"), {});
  assert.deepEqual(normalizeIgnoredUpdateVersions({ gui: "", playback: 12 }), {});
});

test("only suppresses the exact ignored component version", () => {
  const ignored = { gui: "1.2.0", playback: "1.1.1" };
  assert.equal(updateVersionIsIgnored(ignored, "gui", "1.2.0"), true);
  assert.equal(updateVersionIsIgnored(ignored, "gui", "1.2.1"), false);
  assert.equal(updateVersionIsIgnored(ignored, "playback", "1.1.1"), true);
  assert.equal(updateVersionIsIgnored(ignored, "playback", undefined), false);
});

test("accepts only a complete GUI-to-playback continuation", () => {
  assert.deepEqual(
    normalizePendingPlaybackUpdate('{"guiVersion":"1.2.0","playbackVersion":"1.1.1"}'),
    { guiVersion: "1.2.0", playbackVersion: "1.1.1" },
  );
  assert.equal(normalizePendingPlaybackUpdate('{"guiVersion":"1.2.0"}'), null);
  assert.equal(normalizePendingPlaybackUpdate("not-json"), null);
});
