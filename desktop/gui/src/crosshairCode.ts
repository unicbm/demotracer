/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { decodeCrosshairShareCode, type Crosshair as LegacyCrosshair, type CrosshairV4 } from "csgo-sharecode";

// CS-prefixed codes use a separate 32-byte format, whose version restarts at 1.
// Keep it distinct from the legacy CSGO V1 layout.
export interface CrosshairCS1 extends Omit<CrosshairV4, "version"> {
  version: "cs1";
  outlineRed: number;
  outlineGreen: number;
  outlineBlue: number;
  outlineAlpha: number;
  scopeDotMatchesCrosshairColor: boolean;
  scopeDotScale: number;
}

export type Crosshair = LegacyCrosshair | CrosshairCS1;
const ALPHABET = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";
const LEGACY_PATTERN = new RegExp(`^CSGO(?:-[${ALPHABET}]{5}){5}$`);
const CS_PATTERN = new RegExp(`^CS[${ALPHABET}]{44}$`);

export function decodeCrosshairCode(code: string): Crosshair {
  if (LEGACY_PATTERN.test(code)) return decodeCrosshairShareCode(code);
  if (!CS_PATTERN.test(code)) throw new Error("Invalid crosshair code");
  let packed = 0n;
  for (let i = code.length - 1; i >= 2; i--) packed = packed * 57n + BigInt(ALPHABET.indexOf(code[i]));
  if (packed >= (1n << 256n)) throw new Error("Crosshair payload overflow");
  const bytes = new Uint8Array(32);
  for (let i = 31; i >= 0; i--) {
    bytes[i] = Number(packed & 255n);
    packed >>= 8n;
  }
  if (bytes[1] !== 1 || bytes[0] !== (bytes.slice(1).reduce((sum, value) => sum + value, 0) & 255)) {
    throw new Error("Unsupported or damaged crosshair code");
  }
  const data = new DataView(bytes.buffer);
  const screenHeight = data.getUint16(2, true);
  if (screenHeight === 0) throw new Error("Missing crosshair screen height");
  const split = data.getUint32(18, true);
  // Match the client decoder's bounds after validating the original checksum.
  return {
    version: "cs1",
    screenHeight: Math.max(240, screenHeight),
    style: Math.min(9, bytes[4] & 31),
    followRecoil: (bytes[4] & 32) !== 0,
    centerDotEnabled: (bytes[4] & 64) !== 0,
    tStyleEnabled: (bytes[4] & 128) !== 0,
    red: bytes[5], green: bytes[6], blue: bytes[7], alpha: bytes[8],
    outlineRed: bytes[9], outlineGreen: bytes[10], outlineBlue: bytes[11], outlineAlpha: bytes[12],
    thickness: Math.min(32, bytes[13] & 63),
    outlineMode: Math.min(2, bytes[13] >>> 6),
    gap: Math.max(-3840, Math.min(3840, data.getInt16(14, true))),
    length: bytes[16], dynamicSpreadLimit: bytes[17],
    splitDistance: split & 127,
    innerSplitAlpha: Math.min(100, (split >>> 7) & 127) / 100,
    outerSplitAlpha: (30 + Math.min(70, (split >>> 14) & 127)) / 100,
    splitSizeRatio: Math.min(100, (split >>> 21) & 127) / 100,
    scopeDotMatchesCrosshairColor: ((split >>> 28) & 1) !== 0,
    scopeDotScale: (10 + Math.min(190, bytes[22])) / 100,
  };
}
