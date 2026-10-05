/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

export interface BackgroundCrop { x: number; y: number; zoom: number }
export const DEFAULT_BACKGROUND_CROP: BackgroundCrop = { x: 0.5, y: 0.5, zoom: 1 };

export function normalizeBackgroundCrop(value: unknown): BackgroundCrop {
  const crop = (value && typeof value === "object" ? value : {}) as Partial<BackgroundCrop>;
  const bounded = (value: unknown, fallback: number, min: number, max: number) =>
    typeof value === "number" && Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback;
  return { x: bounded(crop.x, 0.5, 0, 1), y: bounded(crop.y, 0.5, 0, 1), zoom: bounded(crop.zoom, 1, 1, 3) };
}

export function panBackgroundCrop(crop: BackgroundCrop, image: { width: number; height: number },
  viewport: { width: number; height: number }, dx: number, dy: number): BackgroundCrop {
  const scale = Math.max(viewport.width / image.width, viewport.height / image.height) * crop.zoom;
  const overflowX = image.width * scale - viewport.width;
  const overflowY = image.height * scale - viewport.height;
  return normalizeBackgroundCrop({ ...crop,
    x: overflowX > 0.5 ? crop.x - dx / overflowX : crop.x,
    y: overflowY > 0.5 ? crop.y - dy / overflowY : crop.y,
  });
}
