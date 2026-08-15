/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readFileSync } from "node:fs";

const config = JSON.parse(readFileSync(new URL("../src-tauri/tauri.conf.json", import.meta.url), "utf8"));
const packageJson = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));

if (config.build?.beforeDevCommand !== "pnpm run dev:web") {
  throw new Error("tauri.conf.json must start Vite before the Tauri development application");
}
if (config.build?.devUrl !== "http://localhost:1420") {
  throw new Error("tauri.conf.json must load the canonical Vite development URL");
}
if (packageJson.scripts?.["dev:acceptance"] !== "tauri dev --release") {
  throw new Error("package.json must keep the real-backend GUI acceptance command");
}
if (packageJson.scripts?.dev !== "pnpm run dev:acceptance") {
  throw new Error("package.json dev command must use the GUI acceptance workflow");
}

const updater = config.plugins?.updater;
if (!updater || typeof updater !== "object") {
  throw new Error("tauri.conf.json must configure the signed desktop updater");
}
if (typeof updater.pubkey !== "string" || updater.pubkey.trim().length < 100) {
  throw new Error("tauri.conf.json updater public key is missing or truncated");
}
if (!Array.isArray(updater.endpoints)
    || updater.endpoints.length !== 1
    || updater.endpoints[0] !== "https://releases.detr.site/channels/stable/latest.json") {
  throw new Error("tauri.conf.json must use the canonical HTTPS stable update manifest");
}
if (updater.windows?.installMode !== "passive") {
  throw new Error("tauri.conf.json updater install mode must remain passive on Windows");
}
if (config.bundle?.active !== true || !config.bundle.targets?.includes("nsis")) {
  throw new Error("tauri.conf.json must build the supported NSIS installer");
}
if (config.bundle?.windows?.nsis?.installMode !== "currentUser") {
  throw new Error("tauri.conf.json NSIS install mode must remain currentUser");
}
if (config.bundle?.windows?.nsis?.installerHooks !== "windows/installer-hooks.nsh") {
  throw new Error("tauri.conf.json must keep the Windows shortcut repair hook enabled");
}

console.log("Tauri development, NSIS, and signed stable updater configuration are valid.");
