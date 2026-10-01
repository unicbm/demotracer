/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { decodeCrosshairCode } from "./crosshairCode.ts";
import { decodePreviewCrosshair, rasterizeCrosshair } from "./crosshairPreviewModel.ts";

// Synthetic payload independently accepted and decoded by the native CS client.
const CODE = "CSG4pWURDBtO7JeYvrNjoewqFQ9rGdZuDRmzyy5QLDFNrh";
const PAYLOAD = "df0138046955e8ff80ff2040c082fdff08c88352a7177d000000000000000000";
const ALPHABET = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";
function changedCode(index: number, value: number) {
  const bytes = Buffer.from(PAYLOAD, "hex");
  bytes[index] = value;
  bytes[0] = bytes.subarray(1).reduce((sum, byte) => sum + byte, 0) & 255;
  let valueBits = BigInt(`0x${bytes.toString("hex")}`);
  let code = "CS";
  for (let i = 0; i < 44; i++) {
    code += ALPHABET[Number(valueBits % 57n)];
    valueBits /= 57n;
  }
  return code;
}
function currentCrosshair() {
  const value = decodePreviewCrosshair(CODE);
  assert.equal(value.version, "cs1");
  return value;
}
function pixel(image: ReturnType<typeof rasterizeCrosshair>, x: number, y: number) {
  const offset = (y * image.width + x) * 4;
  return Array.from(image.data.slice(offset, offset + 4));
}

describe("CS-prefixed crosshair format", () => {
  it("decodes all new fields without confusing its version 1 with CSGO V1", () => {
    assert.deepEqual(currentCrosshair(), {
      version: "cs1", screenHeight: 1080, style: 9, followRecoil: true,
      centerDotEnabled: true, tStyleEnabled: false,
      red: 85, green: 232, blue: 255, alpha: 128,
      outlineRed: 255, outlineGreen: 32, outlineBlue: 64, outlineAlpha: 192,
      thickness: 2, outlineMode: 2, gap: -3, length: 8, dynamicSpreadLimit: 200,
      splitDistance: 3, innerSplitAlpha: 0.37, outerSplitAlpha: 0.59, splitSizeRatio: 0.61,
      scopeDotMatchesCrosshairColor: true, scopeDotScale: 1.35,
    });
    assert.equal(decodePreviewCrosshair("CSGO-GA9km-msST6-yyjrG-PYKNi-DeCcO").version, 1);
  });
  it("rejects unknown versions, damaged checksums, invalid alphabet, length, and overflow", () => {
    for (const value of [changedCode(1, 2), CODE.slice(0, -1) + "A", CODE.slice(0, -1),
      CODE + "A", "CS" + "9".repeat(44), CODE.replace("URDB", "URDI")]) {
      assert.throws(() => decodeCrosshairCode(value));
    }
  });
  it("matches native bounds and retains the sign of the gap", () => {
    const style = decodeCrosshairCode(changedCode(4, 255));
    const thickness = decodeCrosshairCode(changedCode(13, 255));
    assert.equal(style.style, 9);
    assert.equal(thickness.thickness, 32);
    assert.equal(thickness.version === "cs1" && thickness.outlineMode, 2);
    const scope = decodeCrosshairCode(changedCode(22, 255));
    assert.equal(scope.version === "cs1" && scope.scopeDotScale, 2);
    assert.equal(currentCrosshair().gap, -3);
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
