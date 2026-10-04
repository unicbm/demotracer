/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";
import { advanceCrosshairAnimation, buildCrosshairFrame, createCrosshairAnimation, isAnimatedCrosshair,
  normalizePixelCrosshair, rasterizeCrosshairFrame, type PixelCrosshair, type CrosshairPrimitive } from "./crosshairRenderer.ts";

const base: PixelCrosshair = {
  format: "cs2-v1", screenHeight: 1080, style: 4, centerDotEnabled: false, tStyleEnabled: false,
  outlineMode: 1, red: 100, green: 200, blue: 50, alpha: 180,
  outlineRed: 20, outlineGreen: 80, outlineBlue: 220, outlineAlpha: 140,
  gap: 4, thickness: 3, length: 9, dynamicSpreadLimit: 7, splitDistance: 7,
  innerSplitAlpha: 0.65, outerSplitAlpha: 0.45, splitSizeRatio: 0.33,
  followRecoil: false, scopeDotScale: 1, scopeDotUseCrosshairColor: false,
};
const frame = { width: 128, height: 128, animate: true, time: 1, recoil: 14.3 };
// Recorded by offline x64 emulation of CS2 1.41.8.8's geometry functions.
// These expected numbers are independent of the TypeScript implementation.
const fixture = JSON.parse(readFileSync(new URL("./fixtures/crosshair-native.json", import.meta.url), "utf8")) as {
  cases: { input: { style: number; thickness: number; outlineMode: number; dot: boolean }; shapes: CrosshairPrimitive[] }[];
};

describe("independent CS2 preview renderer", () => {
  for (const { input, shapes } of fixture.cases) {
    it(`matches native style ${input.style}, thickness ${input.thickness}, outline ${input.outlineMode}`, () => {
      const actual = buildCrosshairFrame({ ...base, ...input, centerDotEnabled: input.dot }, frame);
      assert.equal(actual.length, shapes.length);
      for (let i = 0; i < shapes.length; i++) {
        assert.equal(actual[i].kind, shapes[i].kind);
        for (const field of ["geometry", "outline", "fill", "border"] as const) {
          actual[i][field].forEach((value, j) => assert.ok(Math.abs(value - shapes[i][field][j]) < 0.00001,
            `${field}[${j}]: ${value} != ${shapes[i][field][j]}`));
        }
        assert.ok(Math.abs(actual[i].arc - shapes[i].arc) < 0.00001);
      }
    });
  }
  it("animates precisely the five preview styles, keeping the configured dot opaque", () => {
    for (let style = 0; style < 10; style++) {
      const c = { ...base, style, centerDotEnabled: true };
      const still = buildCrosshairFrame(c, { ...frame, animate: false, time: 0, recoil: 0 });
      const moving = buildCrosshairFrame(c, frame);
      if (isAnimatedCrosshair(style)) assert.notDeepEqual(moving, still);
      else assert.deepEqual(moving, still);
      assert.deepEqual(moving[0], still[0]);
    }
  });
  it("retains negative gaps and scales spread limits and split distance with authored resolution", () => {
    const c = normalizePixelCrosshair({ ...base, screenHeight: 2160, gap: -3, thickness: 1, length: 0 });
    assert.equal(c.gap, -2); assert.equal(c.thickness, 1); assert.equal(c.length, 0);
    assert.equal(c.dynamicSpreadLimit, 4); assert.equal(c.splitDistance, 4);
    assert.equal(normalizePixelCrosshair({ ...base, screenHeight: 0 }).gap, 4);
  });
  it("uses the legacy dynamic split lengths and independent inner/outer alpha", () => {
    const shapes = buildCrosshairFrame({ ...base, style: 2 }, frame);
    assert.equal(shapes.length, 8);
    assert.equal(shapes[0].geometry[2] - shapes[0].geometry[0] + 1, 2);
    assert.equal(shapes[4].geometry[2] - shapes[4].geometry[0] + 1, 7);
    assert.equal(shapes[0].fill[3], 81 / 255);
    assert.equal(shapes[4].fill[3], 116 / 255); // Float32 product, then truncation.
  });
  it("uses a ring near rest and four sectors at larger spread for Dynamic Quad", () => {
    assert.equal(buildCrosshairFrame({ ...base, style: 7 }, { ...frame, projectedSpread: 10 })[0].kind, 1);
    assert.equal(buildCrosshairFrame({ ...base, style: 7 }, { ...frame, projectedSpread: 11 })[0].kind, 2);
    assert.equal(buildCrosshairFrame({ ...base, style: 7 }, { ...frame, projectedSpread: 0 }).length, 4);
  });
  it("keeps shot feedback local to each preview and bounds long suspended frames", () => {
    const a = createCrosshairAnimation(), b = createCrosshairAnimation();
    advanceCrosshairAnimation(a, 1 / 60, () => 0.5);
    assert.ok(Math.abs(a.recoil - 14.3) < 1e-6); assert.equal(a.untilShot, 0.4);
    assert.deepEqual(b, createCrosshairAnimation());
    advanceCrosshairAnimation(a, 100, () => 0.5);
    assert.ok(a.time < 0.12); assert.ok(a.recoil < 14.3);
  });
  it("does not introduce pawn recoil or scoped HUD behavior into the settings preview", () => {
    const normal = buildCrosshairFrame(base, frame);
    assert.deepEqual(buildCrosshairFrame({ ...base, followRecoil: true, scopeDotScale: 2, scopeDotUseCrosshairColor: true }, frame), normal);
  });
  it("combines overlapping layers by maximum alpha and keeps outline color when fill is transparent", () => {
    const dot = buildCrosshairFrame({ ...base, style: 6, alpha: 0, thickness: 2 }, { width: 16, height: 16 });
    const a = rasterizeCrosshairFrame(dot, 16, 16);
    assert.deepEqual(rasterizeCrosshairFrame([...dot, ...dot], 16, 16), a);
    assert.deepEqual(Array.from(a.data.slice((7 * 16 + 7) * 4, (7 * 16 + 7) * 4 + 4)), [20, 80, 220, 140]);
  });
});
