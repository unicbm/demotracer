/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { decodeCrosshairShareCode, type Crosshair } from "csgo-sharecode";

// V3/V4 store pixel dimensions at the author's screen height. Previews use a
// fixed 1080p reference, with no weapon inaccuracy or recoil simulation.
export const CROSSHAIR_REFERENCE_HEIGHT = 1080;

export function decodePreviewCrosshair(code: string): Crosshair {
  if (!/^CSGO(?:-[ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789]{5}){5}$/.test(code)) {
    throw new Error("Invalid crosshair code");
  }
  const crosshair = decodeCrosshairShareCode(code);
  const maxStyle = crosshair.version === 1 ? 5 : crosshair.version === 3 ? 7 : 8;
  if (crosshair.style > maxStyle || (crosshair.version === 4 && crosshair.outlineMode > 2)) {
    throw new Error("Unsupported crosshair settings");
  }
  return crosshair;
}

export interface CrosshairRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface CrosshairViewBox {
  x: number;
  y: number;
  size: number;
}

const PRESET_COLORS = ["#ff0000", "#00ff00", "#ffff00", "#0000ff", "#00ffff"];

export function resolveCrosshairColor(crosshair: Crosshair): string {
  if (crosshair.version === 1 && crosshair.color >= 0 && crosshair.color < PRESET_COLORS.length) {
    return PRESET_COLORS[crosshair.color];
  }
  return `rgb(${crosshair.red} ${crosshair.green} ${crosshair.blue})`;
}

export function resolveCrosshairOpacity(crosshair: Crosshair): number {
  return crosshair.version !== 1 || crosshair.alphaEnabled ? crosshair.alpha / 255 : 1;
}

export function resolveCrosshairOutline(crosshair: Crosshair): number {
  if (crosshair.version === 4) return crosshair.outlineMode === 0 ? 0 : 1;
  if (crosshair.version === 3) return crosshair.outlineEnabled ? 1 : 0;
  return crosshair.outlineEnabled ? Math.max(0, crosshair.outline) : 0;
}

export function resolveCrosshairGap(crosshair: Crosshair): number {
  return crosshair.version === 1 && crosshair.style === 1 ? crosshair.fixedCrosshairGap : crosshair.gap;
}

export function buildCrosshairRects(crosshair: Crosshair, viewboxSize = 48): CrosshairRect[] {
  if (crosshair.version !== 1) return buildPixelCrosshair(crosshair, viewboxSize).rects;
  const pixelScale = viewboxSize / 64;
  const baseLength = Math.max(0, Math.floor(crosshair.length * 2));
  const logicalLength = Math.floor(crosshair.length) > 2 ? baseLength + 1 : baseLength;
  const logicalThickness = Math.max(1, Math.floor(crosshair.thickness * 2));
  const length = logicalLength > 0 ? Math.max(1, Math.round(logicalLength * pixelScale)) : 0;
  const thickness = Math.max(1, Math.round(logicalThickness * pixelScale));
  const gap = Math.round(Math.ceil(resolveCrosshairGap(crosshair) + 4) * pixelScale);
  // An even-sized SVG has no single center pixel. Align odd-width strokes to
  // a pixel center and even-width strokes to a pixel boundary so every rect
  // starts and ends on the same raster grid in all four directions.
  const center = Math.floor(viewboxSize / 2) + (thickness % 2 === 0 ? 0 : 0.5);
  const offset = thickness / 2 + gap;
  const shapes: CrosshairRect[] = [];

  if (length > 0) {
    shapes.push(
      { x: center + offset, y: center - thickness / 2, width: length, height: thickness },
      { x: center - offset - length, y: center - thickness / 2, width: length, height: thickness },
      { x: center - thickness / 2, y: center + offset, width: thickness, height: length },
    );
    if (!crosshair.tStyleEnabled) {
      shapes.push({ x: center - thickness / 2, y: center - offset - length, width: thickness, height: length });
    }
  }

  if (crosshair.centerDotEnabled) {
    shapes.push({ x: center - thickness / 2, y: center - thickness / 2, width: thickness, height: thickness });
  }
  return shapes;
}

interface CrosshairCircle {
  x: number;
  y: number;
  radius: number;
  thickness: number;
}

function buildPixelCrosshair(crosshair: Exclude<Crosshair, { version: 1 }>, size: number) {
  const scale = crosshair.screenHeight > 0 ? CROSSHAIR_REFERENCE_HEIGHT / crosshair.screenHeight : 1;
  const pixels = (value: number) => value > 0 ? Math.max(1, Math.round(value * scale)) : 0;
  const gap = pixels(crosshair.gap);
  const length = pixels(crosshair.length);
  const thickness = pixels(crosshair.thickness);
  const center = Math.floor(size / 2);
  const before = Math.ceil(thickness / 2);
  const even = thickness % 2 === 0;
  const rects: CrosshairRect[] = [];
  const circles: CrosshairCircle[] = [];
  if (thickness === 0) return { rects, circles };

  if (crosshair.style === 1 || crosshair.style === 3) {
    circles.push({
      x: center - (even ? 0 : 0.5), y: center - (even ? 0 : 0.5),
      radius: (crosshair.style === 3 ? Math.max(1, gap) : gap) + thickness,
      thickness: Math.max(1, thickness - 1),
    });
  } else if (crosshair.style === 8) {
    const low = center - gap - thickness - (crosshair.centerDotEnabled && !even ? 1 : 0);
    const high = center + gap;
    rects.push(
      { x: low, y: low, width: high - low, height: thickness },
      { x: low, y: high, width: high - low, height: thickness },
      { x: low, y: low, width: thickness, height: high + thickness - low },
      { x: high, y: low, width: thickness, height: high + thickness - low },
    );
  } else if (crosshair.style !== 6 && length > 0) {
    // Native gap is measured from the center, not outside half the thickness.
    const positive = center + gap + (even ? 0 : -1);
    rects.push(
      { x: positive, y: center - before, width: length, height: thickness },
      { x: center - gap - length, y: center - before, width: length, height: thickness },
      { x: center - before, y: positive, width: thickness, height: length },
    );
    if (!crosshair.tStyleEnabled) {
      rects.push({ x: center - before, y: center - gap - length, width: thickness, height: length });
    }
  }
  if (crosshair.centerDotEnabled || crosshair.style === 6) {
    rects.push({ x: center - before, y: center - before, width: thickness, height: thickness });
  }
  return { rects, circles };
}

function smoothstep(from: number, to: number, value: number): number {
  const t = Math.max(0, Math.min(1, (value - from) / (to - from)));
  return t * t * (3 - 2 * t);
}

// Rasterize the native shapes before fitting them into the thumbnail. In V4,
// half outlines extend towards the top/left, not at half opacity. The circle's
// outline varies around its angle. Layer coverage uses max, so crossing bars
// and a center dot do not accumulate opacity where they overlap.
export function rasterizeCrosshair(crosshair: Crosshair, size = 48) {
  const geometry = crosshair.version === 1
    ? { rects: buildCrosshairRects(crosshair, size), circles: [] as CrosshairCircle[] }
    : buildPixelCrosshair(crosshair, size);
  const logicalOutline = resolveCrosshairOutline(crosshair);
  const outlineBefore = crosshair.version === 1 && logicalOutline > 0
    ? Math.max(1, Math.round(logicalOutline * size / 64)) : logicalOutline;
  const outlineAfter = crosshair.version === 4 && crosshair.outlineMode === 2 ? 0 : outlineBefore;
  const circleBounds = geometry.circles.map((circle) => ({
    x: circle.x - circle.radius, y: circle.y - circle.radius,
    width: circle.radius * 2 + 1, height: circle.radius * 2 + 1,
  }));
  const view = resolveCrosshairViewBox([...geometry.rects, ...circleBounds], outlineBefore, size);
  const width = Math.min(512, Math.ceil(view.size));
  const data = new Uint8ClampedArray(width * width * 4);
  const color = resolveCrosshairColor(crosshair);
  const rgb = color.startsWith("#")
    ? [1, 3, 5].map((offset) => parseInt(color.slice(offset, offset + 2), 16))
    : [crosshair.red, crosshair.green, crosshair.blue];
  const opacity = resolveCrosshairOpacity(crosshair);
  for (let y = 0; y < width; y++) {
    for (let x = 0; x < width; x++) {
      const px = view.x + x * view.size / width;
      const py = view.y + y * view.size / width;
      let fill = 0;
      let border = 0;
      for (const rect of geometry.rects) {
        if (px >= rect.x && px < rect.x + rect.width && py >= rect.y && py < rect.y + rect.height) fill = 1;
        if (outlineBefore > 0 && px >= rect.x - outlineBefore && px < rect.x + rect.width + outlineAfter &&
            py >= rect.y - outlineBefore && py < rect.y + rect.height + outlineAfter) border = 1;
      }
      for (const circle of geometry.circles) {
        const dx = px - circle.x;
        const dy = py - circle.y;
        const distance = Math.hypot(dx, dy);
        const angle = Math.atan2(dx, -dy);
        const blend = Math.max(smoothstep(0, Math.PI / 2, angle), smoothstep(-Math.PI / 2, -Math.PI, angle));
        const outer = circle.radius + outlineBefore + (outlineAfter - outlineBefore) * blend;
        const inner = circle.radius - circle.thickness - outlineAfter - (outlineBefore - outlineAfter) * blend;
        fill = Math.max(fill, smoothstep(circle.radius + 0.5, circle.radius - 0.5, distance) *
          smoothstep(circle.radius - circle.thickness - 0.5, circle.radius - circle.thickness + 0.5, distance));
        if (outlineBefore > 0) border = Math.max(border, smoothstep(outer + 0.5, outer - 0.5, distance) *
          smoothstep(inner - 0.5, inner + 0.5, distance));
      }
      const fillAlpha = fill * opacity;
      // The native shader attenuates the outline by the fill alpha before
      // mixing it under the fill, so this factor occurs twice.
      const alpha = fillAlpha + (1 - fillAlpha) ** 2 * border * opacity;
      const index = (y * width + x) * 4;
      for (let c = 0; c < 3; c++) data[index + c] = alpha > 0 ? rgb[c] * fillAlpha / alpha : 0;
      data[index + 3] = alpha * 255;
    }
  }
  return { width, height: width, data };
}

export function resolveCrosshairViewBox(
  shapes: CrosshairRect[],
  outline: number,
  baseSize = 64,
): CrosshairViewBox {
  const center = baseSize / 2;
  if (shapes.length === 0) return { x: 0, y: 0, size: baseSize };

  const minX = Math.min(...shapes.map((shape) => shape.x - outline));
  const minY = Math.min(...shapes.map((shape) => shape.y - outline));
  const maxX = Math.max(...shapes.map((shape) => shape.x + shape.width + outline));
  const maxY = Math.max(...shapes.map((shape) => shape.y + shape.height + outline));
  const halfExtent = Math.max(center - minX, maxX - center, center - minY, maxY - center);
  const halfView = Math.max(baseSize / 2, Math.ceil(halfExtent + 2));
  return { x: center - halfView, y: center - halfView, size: halfView * 2 };
}
