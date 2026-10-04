/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import type { Crosshair, CrosshairLegacyV1 } from "csgo-sharecode";

export type PixelCrosshair = Exclude<Crosshair, CrosshairLegacyV1>;
export type CrosshairColor = readonly [number, number, number, number];
export interface CrosshairPrimitive {
  kind: 0 | 1 | 2; // Rectangle (inclusive edges), ring, quadrant ring.
  geometry: readonly [number, number, number, number];
  arc: number;
  outline: readonly [number, number];
  fill: CrosshairColor; // Linear RGB, straight alpha.
  border: CrosshairColor;
}
export interface CrosshairAnimation {
  time: number;
  recoil: number;
  untilShot: number;
}
export interface CrosshairFrame {
  width: number;
  height: number;
  time?: number;
  animate?: boolean;
  recoil?: number;
  referenceHeight?: number;
  // Projection supplied by the host. The standalone preview uses a 90-degree
  // 4:3 reference camera; it has no pawn, weapon or live game view matrix.
  projectedSpread?: number;
}
export const CROSSHAIR_REFERENCE_HEIGHT = 1080;
export const isAnimatedCrosshair = (style: number) => [0, 1, 2, 5, 7].includes(style);
export const createCrosshairAnimation = (): CrosshairAnimation => ({ time: 0, recoil: 0, untilShot: 0 });
const clamp = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, n));
const f = Math.fround;
const round = (n: number) => Math.sign(n) * Math.floor(Math.abs(n) + 0.5);
const evenRound = (n: number) => n % 1 === 0.5 ? Math.round(n / 2) * 2 : Math.round(n);
const linear = (n: number) => n <= 0.04045 ? n / 12.92 : ((n + 0.055) / 1.055) ** 2.4;
const srgb = (n: number) => n <= 0.0031308 ? n * 12.92 : 1.055 * n ** (1 / 2.4) - 0.055;
const color = (r: number, g: number, b: number, a: number): CrosshairColor => [linear(r / 255), linear(g / 255), linear(b / 255), a / 255];
const alpha = (c: CrosshairColor, amount: number, truncate = false): CrosshairColor =>
  [c[0], c[1], c[2], (truncate ? Math.trunc : round)(f(f(c[3] * 255) * f(amount))) / 255];

// Each canvas owns this state. Hiding/unmounting a preview must stop its clock.
export function advanceCrosshairAnimation(state: CrosshairAnimation, seconds: number, random = Math.random): void {
  const dt = clamp(seconds, 0, 0.1);
  state.time += dt;
  state.untilShot -= dt;
  if (state.untilShot < 0) {
    state.recoil += 15;
    state.untilShot = 0.2 + clamp(random(), 0, 1) * 0.4;
  }
  state.recoil = clamp(state.recoil, 0, 25);
  state.recoil = state.recoil > 0 ? state.recoil - dt * 42 : 0;
}

export function normalizePixelCrosshair(c: PixelCrosshair, height = CROSSHAIR_REFERENCE_HEIGHT): PixelCrosshair {
  if (c.screenHeight <= 0 || c.screenHeight === height) return c;
  const scale = f(height / c.screenHeight);
  const pixels = (n: number) => n === 0 ? 0 : Math.sign(n) * Math.max(1, Math.abs(round(f(n * scale))));
  return { ...c, screenHeight: height, gap: pixels(c.gap), length: pixels(c.length),
    thickness: pixels(c.thickness), dynamicSpreadLimit: pixels(c.dynamicSpreadLimit), splitDistance: pixels(c.splitDistance) };
}

// Independently implemented from CS2's preview geometry and csgo_crosshair
// material behavior (2026-10-02). No game binaries or Panorama runtime needed.
export function buildCrosshairFrame(settings: PixelCrosshair, frame: CrosshairFrame): CrosshairPrimitive[] {
  const height = frame.referenceHeight ?? CROSSHAIR_REFERENCE_HEIGHT;
  const c = normalizePixelCrosshair(settings, height);
  const shapes: CrosshairPrimitive[] = [];
  const cx = Math.floor(frame.width / 2), cy = Math.floor(frame.height / 2);
  const mode = c.format === "legacy-v3" ? Number(c.outlineEnabled) : c.outlineMode;
  const outline: readonly [number, number] = [mode ? 1 : 0, mode === 1 ? 1 : 0];
  const fill = color(c.red, c.green, c.blue, c.alpha);
  const border = !mode ? color(0, 0, 0, 0) : c.format === "cs2-v1"
    ? color(c.outlineRed, c.outlineGreen, c.outlineBlue, c.outlineAlpha) : color(0, 0, 0, c.alpha);
  const thickness = c.thickness, length = c.length;
  const integerThickness = Math.max(1, evenRound(thickness));
  const before = Math.ceil(integerThickness / 2), after = integerThickness - before;
  const even = before === after;
  const rect = (x: number, y: number, right: number, bottom: number, a = fill, b = border) => {
    shapes.push({ kind: 0, geometry: [x, y, right - 1, bottom - 1], arc: 0, outline, fill: a, border: b });
  };
  const cross = (gap: number, size = length, a = fill, b = border) => {
    if (size <= 0 || thickness <= 0) return;
    gap = Math.max(0, gap);
    const left = Math.floor(cx - gap), right = Math.ceil(cx + gap) + (even ? 0 : -1);
    const top = Math.floor(cy - gap - size), bottom = Math.ceil(cy + gap) + (even ? 0 : -1);
    rect(left - size, cy - before, left, cy + after, a, b);
    rect(right, cy - before, right + size, cy + after, a, b);
    if (!c.tStyleEnabled) rect(cx - before, top, cx + after, top + size, a, b);
    rect(cx - before, bottom, cx + after, bottom + size, a, b);
  };
  const ring = (radius: number, width: number, shift: number, arc: number | undefined, a = fill, b = border) => {
    shapes.push({ kind: arc === undefined ? 1 : 2, geometry: [cx - shift - 0.5, cy - shift - 0.5, radius, width],
      arc: arc === undefined ? 0 : f(f(arc) * f(Math.PI / 180)), outline, fill: a, border: b });
  };
  const circle = (gap: number, a = fill, b = border) => {
    if (gap >= 0 && thickness > 0) ring(gap + thickness, Math.max(1, thickness - 1), even ? 0 : 0.5, undefined, a, b);
  };
  const dot = () => { if (thickness > 0) rect(cx - before, cy - before, cx + after, cy + after); };
  if (c.centerDotEnabled) dot();

  const sine = f(Math.sin(f(frame.time ?? 0)));
  const spread = frame.animate ? f(Math.abs(sine) * f(0.1)) : 0;
  const projected = frame.projectedSpread ?? round(f(spread * height * 2 / 3));
  const limit = c.dynamicSpreadLimit + 64;
  const soft = projected <= limit * 0.75 ? projected : limit - Math.exp(-(projected - limit * 0.75) / (limit * 0.25)) * limit * 0.25;
  const dynamicGap = Math.trunc(Math.max(c.centerDotEnabled ? thickness : 0, Math.min(limit, soft)));
  const opacity = f(1 - f(f(spread / 0.25) * f(0.8)));
  const dynamicFill = alpha(fill, opacity), dynamicBorder = alpha(border, opacity);
  switch (c.style) {
    case 0: cross(dynamicGap, length, dynamicFill, dynamicBorder); break;
    case 1: circle(dynamicGap, dynamicFill, dynamicBorder); break;
    case 2: {
      const raw = f(f(frame.animate ? Math.max(0, f(sine * f(0.15))) : 0) * 320);
      const expanded = round(f(f(height / 480) * raw));
      const capped = round(f(f(height / 480) * Math.min(raw, c.splitDistance)));
      const outerGap = expanded > 0 ? c.gap + expanded : Math.trunc(c.gap);
      if (raw > c.splitDistance) {
        const innerLength = Math.ceil(f(f(1 - f(c.splitSizeRatio)) * length));
        const outerLength = Math.floor(f(f(c.splitSizeRatio) * length));
        cross(outerGap + innerLength, outerLength, alpha(fill, c.outerSplitAlpha, true), alpha(border, c.outerSplitAlpha, true));
        cross(Math.max(0, c.gap + capped), innerLength, alpha(fill, c.innerSplitAlpha, true), alpha(border, c.innerSplitAlpha, true));
      } else cross(outerGap);
      break;
    }
    case 3: circle(Math.max(1, c.gap)); break;
    case 4: cross(c.gap); break;
    case 5: cross(round(Math.max(0, c.gap) + height * (frame.recoil ?? 0) / 1200)); break;
    case 6: if (!c.centerDotEnabled) dot(); break;
    case 7:
      if (projected > 0) {
        const shift = thickness >= 1 && !even ? 0.5 : 0;
        if (projected < 100 && projected <= 10) {
          const width = Math.max(1, thickness - 1);
          ring(dynamicGap + width, width, shift, undefined, dynamicFill, dynamicBorder);
        } else ring(dynamicGap + thickness, thickness, shift, 45, dynamicFill, dynamicBorder);
      }
      cross(c.gap);
      break;
    case 8: {
      if (thickness <= 0) break;
      const gap = Math.max(0, c.gap), shift = c.centerDotEnabled && !even ? -1 : 0;
      const left = Math.floor(cx - gap - thickness + shift), right = Math.floor(cx + gap);
      const top = Math.floor(cy - gap - thickness + shift), bottom = Math.floor(cy + gap);
      rect(left, top, right, top + thickness); rect(left, bottom, right, bottom + thickness);
      rect(left, top, left + thickness, bottom + thickness); rect(right, top, right + thickness, bottom + thickness);
      break;
    }
    case 9: ring(Math.max(1, c.gap) + thickness, thickness, thickness >= 1 && !even ? 0.5 : 0, f(clamp(c.splitSizeRatio, 0, 1) * 90)); break;
  }
  return shapes;
}

const smooth = (a: number, b: number, x: number) => {
  const t = clamp((x - a) / (b - a), 0, 1);
  return t * t * (3 - 2 * t);
};
export function crosshairCoverage(shape: CrosshairPrimitive, x: number, y: number): readonly [number, number] {
  const [a, b, c, d] = shape.geometry, [before, after] = shape.outline;
  if (shape.kind === 0) return [Number(x >= a && x <= c && y >= b && y <= d),
    Number(x >= a - before && x <= c + after && y >= b - before && y <= d + after)];
  const dx = x - a, dy = y - b, distance = Math.hypot(dx, dy), angle = Math.atan2(dx, -dy);
  const blend = Math.max(smooth(0, Math.PI / 2, angle), smooth(-Math.PI / 2, -Math.PI, angle));
  const extent = before + (after - before) * blend;
  const outer = c + extent, inner = c - d - after - (before - after) * blend;
  let fill = smooth(c + 0.5, c - 0.5, distance) * smooth(c - d - 0.5, c - d + 0.5, distance);
  let border = smooth(outer + 0.5, outer - 0.5, distance) * smooth(inner - 0.5, inner + 0.5, distance);
  if (shape.kind === 2) {
    const q = Math.abs(angle - Math.floor(angle / (Math.PI / 2)) * Math.PI / 2 - Math.PI / 4);
    const r = 1 / Math.max(distance, 1), arc = shape.arc / 2;
    fill *= smooth(arc + 0.5 * r, arc - 0.5 * r, q);
    border *= smooth(arc + (extent + 0.5) * r, arc + (extent - 0.5) * r, q);
  }
  return [fill, border];
}

// CPU reference/fallback. Canvas receives straight sRGB; the material's fill /
// outline composition happens in linear RGB before the browser color boundary.
export function rasterizeCrosshairFrame(shapes: readonly CrosshairPrimitive[], width: number, height: number, pixelScale = 1) {
  const data = new Uint8ClampedArray(width * height * 4);
  const bounds = shapes.map(({ kind, geometry: [x, y, a, b], outline: [before, after] }) => kind === 0
    ? [x - before, y - before, a + after, b + after]
    : [x - a - before - 1, y - a - before - 1, x + a + before + 1, y + a + before + 1]);
  const left = Math.max(0, Math.floor(Math.min(...bounds.map((b) => b[0])) / pixelScale));
  const top = Math.max(0, Math.floor(Math.min(...bounds.map((b) => b[1])) / pixelScale));
  const right = Math.min(width - 1, Math.ceil(Math.max(...bounds.map((b) => b[2])) / pixelScale));
  const bottom = Math.min(height - 1, Math.ceil(Math.max(...bounds.map((b) => b[3])) / pixelScale));
  for (let y = top; y <= bottom; y++) for (let x = left; x <= right; x++) {
    let fa = 0, ba = 0;
    let fill: CrosshairColor = [0, 0, 0, 0], border: CrosshairColor = fill;
    for (const shape of shapes) {
      const [fc, bc] = crosshairCoverage(shape, x * pixelScale, y * pixelScale);
      if (fc * shape.fill[3] >= fa) { fa = fc * shape.fill[3]; fill = shape.fill; }
      if (bc * shape.border[3] >= ba) { ba = bc * shape.border[3]; border = shape.border; }
    }
    ba *= (1 - fa) ** 2;
    const a = fa + ba, i = (y * width + x) * 4;
    if (a > 0) for (let c = 0; c < 3; c++) data[i + c] = srgb((fill[c] * fa + border[c] * ba) / a) * 255;
    data[i + 3] = a * 255;
  }
  return { width, height, data };
}
