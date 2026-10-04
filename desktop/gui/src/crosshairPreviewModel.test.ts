/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { decodeCrosshairShareCode, type CrosshairLegacyV1, type CrosshairLegacyV4 } from "csgo-sharecode";
import { buildCrosshairRects, decodePreviewCrosshair, rasterizeCrosshair, resolveCrosshairColor, resolveCrosshairOpacity } from "./crosshairPreviewModel.ts";

function previewCrosshair(overrides: Partial<CrosshairLegacyV1>): CrosshairLegacyV1 {
  return {
    format: "legacy-v1",
    length: 2.5,
    thickness: 2,
    gap: -3,
    fixedCrosshairGap: -3,
    style: 4,
    tStyleEnabled: false,
    centerDotEnabled: false,
    ...overrides,
  } as CrosshairLegacyV1;
}

describe("crosshair preview raster alignment", () => {
  it("keeps donk666's small Anubis crosshair symmetric on the 48px preview grid", () => {
    const crosshair = decodeCrosshairShareCode("CSGO-GA9km-msST6-yyjrG-PYKNi-DeCcO");
    const [right, left, bottom, top] = buildCrosshairRects(crosshair, 48);

    assert.deepEqual(right, { x: 25, y: 23, width: 2, height: 2 });
    assert.deepEqual(left, { x: 21, y: 23, width: 2, height: 2 });
    assert.deepEqual(bottom, { x: 23, y: 25, width: 2, height: 2 });
    assert.deepEqual(top, { x: 23, y: 21, width: 2, height: 2 });
  });

  it("keeps even-width strokes on integer pixel boundaries", () => {
    const shapes = buildCrosshairRects(previewCrosshair({ thickness: 1.5 }), 48);

    for (const shape of shapes) {
      assert.equal(Number.isInteger(shape.x), true);
      assert.equal(Number.isInteger(shape.y), true);
      assert.equal(Number.isInteger(shape.width), true);
      assert.equal(Number.isInteger(shape.height), true);
    }
  });
});

// Real V3/V4 codes from demos; expected bytes checked against the native layout.
const V4_CROSS = "CSGO-F5x8c-aqWRz-PK48S-f35jY-EGV4M";
const V3_CIRCLE = "CSGO-MWnwz-Zd4Zf-YbcB5-XiStM-wGeRF";
const V4_DOT = "CSGO-Tsb5q-nLewQ-2SmK9-EaVNF-TyDEB";

function pixelCrosshair(overrides: Partial<CrosshairLegacyV4> = {}): CrosshairLegacyV4 {
  const decoded = decodePreviewCrosshair(V4_CROSS);
  assert.equal(decoded.format, "legacy-v4");
  return { ...decoded, screenHeight: 1080, gap: 4, length: 8, thickness: 1, ...overrides };
}

function pixel(image: ReturnType<typeof rasterizeCrosshair>, x: number, y: number) {
  const index = (y * image.width + x) * 4;
  return Array.from(image.data.slice(index, index + 4));
}

describe("versioned crosshair previews", () => {
  it("decodes native V4 bytes instead of interpreting them as V1", () => {
    const crosshair = decodePreviewCrosshair(V4_CROSS);
    assert.equal(crosshair.format, "legacy-v4");
    assert.equal(crosshair.style, 4);
    assert.equal(crosshair.length, 2);
    assert.equal(crosshair.thickness, 2);
    assert.equal(crosshair.gap, 1);
    assert.equal(crosshair.screenHeight, 864);
    assert.equal(crosshair.outlineMode, 0);
    assert.equal(resolveCrosshairColor(crosshair), "rgb(85 232 255)");
    assert.equal(resolveCrosshairOpacity(crosshair), 1);
  });

  it("keeps V3 circle evidence distinct from a cross", () => {
    const crosshair = decodePreviewCrosshair(V3_CIRCLE);
    assert.equal(crosshair.format, "legacy-v3");
    assert.equal(crosshair.style, 3);
    assert.equal(crosshair.centerDotEnabled, true);
    assert.equal(buildCrosshairRects(crosshair).length, 1); // Only the center dot is rectangular.
    const image = rasterizeCrosshair(crosshair);
    assert.deepEqual(pixel(image, 21, 24), [255, 255, 255, 253]); // Current preview adds the native half-pixel circle offset.
    assert.deepEqual(pixel(image, 23, 23), [255, 255, 255, 255]); // Dot.
  });

  it("honors dot-only even when its optional center-dot bit is off", () => {
    const crosshair = decodePreviewCrosshair(V4_DOT);
    assert.equal(crosshair.centerDotEnabled, false);
    assert.deepEqual(buildCrosshairRects(crosshair), [{ x: 23, y: 23, width: 2, height: 2 }]);
  });

  it("uses pixel units and center-relative gaps with native odd/even alignment", () => {
    const [left, right, top, bottom] = buildCrosshairRects(pixelCrosshair());
    assert.deepEqual(right, { x: 27, y: 23, width: 8, height: 1 });
    assert.deepEqual(left, { x: 12, y: 23, width: 8, height: 1 });
    assert.deepEqual(bottom, { x: 23, y: 27, width: 1, height: 8 });
    assert.deepEqual(top, { x: 23, y: 12, width: 1, height: 8 });
    const even = buildCrosshairRects(pixelCrosshair({ thickness: 2, gap: 0 }));
    assert.equal(even[1].x, 24);
    assert.equal(even[0].x + even[0].width, 24);
  });

  it("scales authored pixels to 1080p, preserving zero sizes and rounding positive sizes", () => {
    const crosshair = pixelCrosshair({ screenHeight: 2160, length: 8, thickness: 2, gap: 4 });
    assert.deepEqual(buildCrosshairRects(crosshair)[1], { x: 25, y: 23, width: 4, height: 1 });
    assert.deepEqual(buildCrosshairRects(pixelCrosshair({ thickness: 0 })), []);
    assert.deepEqual(buildCrosshairRects(pixelCrosshair({ length: 0 })), []);
    assert.equal(buildCrosshairRects(pixelCrosshair({ screenHeight: 0 }))[0].width, 8);
  });

  it("draws static square from gap and thickness regardless of bar length or T-style", () => {
    const image = rasterizeCrosshair(pixelCrosshair({ style: 8, length: 0, gap: 3, thickness: 2, tStyleEnabled: true }));
    assert.equal(pixel(image, 19, 24)[3], 255);
    assert.equal(pixel(image, 28, 24)[3], 255);
    assert.equal(pixel(image, 24, 19)[3], 255);
    assert.equal(pixel(image, 24, 28)[3], 255);
    assert.equal(pixel(image, 24, 24)[3], 0);
  });

  it("bounds thumbnail allocation while retaining very large authored shapes", () => {
    const image = rasterizeCrosshair(pixelCrosshair({ screenHeight: 1, length: 255, thickness: 255, gap: 127 }));
    assert.equal(image.width, 512);
    assert.ok(image.data.some((value, index) => index % 4 === 3 && value > 0));
  });

  it("renders a half outline on the top/left at full opacity", () => {
    const half = rasterizeCrosshair(pixelCrosshair({ style: 6, thickness: 2, outlineMode: 2 }));
    const full = rasterizeCrosshair(pixelCrosshair({ style: 6, thickness: 2, outlineMode: 1 }));
    assert.deepEqual(pixel(half, 22, 23), [0, 0, 0, 255]);
    assert.equal(pixel(half, 25, 23)[3], 0);
    assert.deepEqual(pixel(full, 25, 23), [0, 0, 0, 255]);
  });

  it("does not darken overlapping bars and dots or ignore direct alpha", () => {
    const image = rasterizeCrosshair(pixelCrosshair({ gap: 0, thickness: 2, alpha: 128, centerDotEnabled: true }));
    assert.deepEqual(pixel(image, 23, 23), [85, 232, 255, 128]);
    const outlined = rasterizeCrosshair(pixelCrosshair({ style: 6, thickness: 2, alpha: 128, outlineMode: 1 }));
    assert.deepEqual(pixel(outlined, 23, 23), [76, 210, 231, 160]); // Linear-space fill/outline composition.
    assert.deepEqual(pixel(outlined, 22, 23), [0, 0, 0, 128]);
    assert.ok(rasterizeCrosshair(pixelCrosshair({ alpha: 0 })).data.every((value) => value === 0));
  });

  it("rejects unknown versions, invalid settings, checksum damage, and invalid alphabet", () => {
    // Rechecksummed payloads: unknown V2/V5, then reserved outline/style values.
    for (const code of [
      "CSGO-BHRYK-k3DPX-7mHZn-3wizt-dWJvM",
      "CSGO-HxjuJ-WFCxi-YZvPo-z5F8O-2b77M",
      "CSGO-bVJ6w-Tvw9P-zQWRq-yKdud-fPm5P",
      "CSGO-KmKaX-d9yce-vGu2r-xe7Sn-oVyjN",
    ]) assert.throws(() => decodePreviewCrosshair(code));
    assert.throws(() => decodePreviewCrosshair("CSGO-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA"));
    assert.throws(() => decodePreviewCrosshair(V4_CROSS.replace("F5x8c", "F5x8I")));
    assert.throws(() => decodePreviewCrosshair(V4_CROSS.replace("F5x8c", "F5x8d")));
  });
});
