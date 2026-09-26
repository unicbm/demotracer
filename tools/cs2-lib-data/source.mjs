/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readFileSync } from "node:fs";

export const source = JSON.parse(readFileSync(new URL("source.json", import.meta.url), "utf8"));
const manifest = JSON.parse(readFileSync(new URL("package.json", import.meta.url), "utf8"));
const installed = JSON.parse(readFileSync(new URL("node_modules/@ianlucas/cs2-lib/package.json", import.meta.url), "utf8"));
const lock = JSON.parse(readFileSync(new URL("package-lock.json", import.meta.url), "utf8"));
const entry = lock.packages?.["node_modules/@ianlucas/cs2-lib"];
if (source.package !== "@ianlucas/cs2-lib" ||
    !/^\d+\.\d+\.\d+$/.test(source.version) ||
    !/^[a-f0-9]{40}$/.test(source.commit) ||
    manifest.dependencies[source.package] !== source.version ||
    installed.name !== source.package || installed.version !== source.version ||
    entry?.version !== source.version || !entry.integrity ||
    lock.packages?.[""].dependencies?.[source.package] !== source.version) {
  throw new Error("cs2-lib source.json, exact dependency, lockfile and installed package must agree");
}
export const integrity = entry.integrity;
const layout = JSON.parse(readFileSync(new URL("layout.json", import.meta.url), "utf8"));
export const catalogPath = new URL(layout.catalog, import.meta.url);
export const econPath = new URL(layout.econ, import.meta.url);
