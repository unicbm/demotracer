/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { decodePreviewCrosshair, rasterizeCrosshair } from "./crosshairPreviewModel.ts";

const CODE = "CSYu7YAbXx3kUn2OtU2Xvw8zmBcvMWXEB6HtAPihTs4hoM";
function currentCrosshair() {
  const value = decodePreviewCrosshair(CODE);
  assert.equal(value.format, "cs2-v1");
  return value;
}
function pixel(image: ReturnType<typeof rasterizeCrosshair>, x: number, y: number) {
  const offset = (y * image.width + x) * 4;
  return Array.from(image.data.slice(offset, offset + 4));
}

describe("CS2 crosshair previews", () => {
  it("accepts the upstream CS2 format alongside legacy demo codes", () => {
    assert.deepEqual(currentCrosshair(), {
      format: "cs2-v1", screenHeight: 1080, style: 9, followRecoil: true,
      centerDotEnabled: true, tStyleEnabled: false,
      red: 85, green: 232, blue: 255, alpha: 128,
      outlineRed: 255, outlineGreen: 32, outlineBlue: 64, outlineAlpha: 192,
      thickness: 2, outlineMode: 2, gap: -3, length: 8, dynamicSpreadLimit: 200,
      splitDistance: 3, innerSplitAlpha: 0.37, outerSplitAlpha: 0.59, splitSizeRatio: 0.61,
      scopeDotUseCrosshairColor: true, scopeDotScale: 1.35,
    });
    assert.equal(decodePreviewCrosshair("CSGO-GA9km-msST6-yyjrG-PYKNi-DeCcO").format, "legacy-v1");
  });
  it("renders independent outline color and opacity, including half outlines", () => {
    const dot = { ...currentCrosshair(), style: 6, centerDotEnabled: false };
    const half = rasterizeCrosshair(dot);
    assert.deepEqual(pixel(half, 22, 23), [255, 32, 64, 192]);
    assert.equal(pixel(half, 25, 23)[3], 0);
    const full = rasterizeCrosshair({ ...dot, outlineMode: 1 });
    assert.deepEqual(pixel(full, 25, 23), [255, 32, 64, 192]);
    const outlineOnly = rasterizeCrosshair({ ...dot, alpha: 0 });
    assert.deepEqual(pixel(outlineOnly, 23, 23), [255, 32, 64, 192]);
    const noOutline = rasterizeCrosshair({ ...dot, outlineAlpha: 0 });
    assert.deepEqual(pixel(noOutline, 23, 23), [85, 232, 255, 128]);
  });
  it("renders Static Quadrant as four ring sectors controlled by split-size ratio", () => {
    const quad = { ...currentCrosshair(), centerDotEnabled: false, gap: 8,
      thickness: 4, splitSizeRatio: 0.5, outlineMode: 0, alpha: 255 };
    const image = rasterizeCrosshair(quad);
    assert.equal(pixel(image, 23, 12)[3], 0); // No bar on the vertical axis.
    assert.ok(pixel(image, 31, 15)[3] > 240); // Diagonal ring sector.
    assert.equal(pixel(image, 23, 23)[3], 0);
    const fullRing = rasterizeCrosshair({ ...quad, splitSizeRatio: 1 });
    assert.ok(pixel(fullRing, 23, 12)[3] > 0);
  });
});
