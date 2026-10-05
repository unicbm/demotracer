/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Crosshair, CrosshairV1 } from "csgo-sharecode";
import { decodePreviewCrosshair } from "./crosshairPreviewModel.ts";

export const INITIAL_CROSSHAIR: CrosshairV1 = {
  format: "cs2-v1", style: 4, followRecoil: false, centerDotEnabled: false, tStyleEnabled: false,
  outlineMode: 0, red: 0, green: 255, blue: 0, alpha: 255,
  outlineRed: 0, outlineGreen: 0, outlineBlue: 0, outlineAlpha: 255,
  gap: 3, length: 6, thickness: 2, dynamicSpreadLimit: 30, splitDistance: 7,
  innerSplitAlpha: 1, outerSplitAlpha: 0.5, splitSizeRatio: 0.3, screenHeight: 1080,
  scopeDotScale: 1, scopeDotUseCrosshairColor: false,
};

export interface CrosshairEditorSession {
  original: Crosshair;
  draft: Crosshair;
  input: string;
}

export function importCrosshair(input: string): CrosshairEditorSession {
  const original = decodePreviewCrosshair(input);
  return { original, draft: original, input: input.trim() };
}
