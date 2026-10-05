/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { test } from "node:test";
import { DEFAULT_BACKGROUND_CROP, normalizeBackgroundCrop, panBackgroundCrop } from "./workspaceBackground.ts";

test("background crop fills wide and tall viewports without panning into empty space", () => {
  const image = { width: 2400, height: 1792 };
  const wide = { width: 1200, height: 600 };
  assert.deepEqual(panBackgroundCrop(DEFAULT_BACKGROUND_CROP, image, wide, 100, 148), { x: 0.5, y: 0, zoom: 1 });
  assert.equal(panBackgroundCrop(DEFAULT_BACKGROUND_CROP, image, wide, 0, -10000).y, 1);
  const portrait = { width: 400, height: 900 };
  assert.equal(panBackgroundCrop(DEFAULT_BACKGROUND_CROP, image, portrait, -10000, 10000).x, 1);
  assert.equal(panBackgroundCrop(DEFAULT_BACKGROUND_CROP, image, portrait, -10000, 10000).y, 0.5);
  assert.deepEqual(panBackgroundCrop({ x: 0.5, y: 0.5, zoom: 2 }, image, wide, 600, 596), { x: 0, y: 0, zoom: 2 });
});

test("old or corrupt crop preferences fall back to a centered, bounded image", () => {
  assert.deepEqual(normalizeBackgroundCrop(null), DEFAULT_BACKGROUND_CROP);
  assert.deepEqual(normalizeBackgroundCrop({ x: NaN, y: Infinity, zoom: "2" }), DEFAULT_BACKGROUND_CROP);
  assert.deepEqual(normalizeBackgroundCrop({ x: -1, y: 2, zoom: 20 }), { x: 0, y: 1, zoom: 3 });
});
