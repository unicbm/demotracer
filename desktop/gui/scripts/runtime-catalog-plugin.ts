/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { normalizePath, type Plugin } from "vite";
import { buildProfessionalPlayerRuntime } from "./runtime-catalogs.ts";

const MODULE_ID = "virtual:demotracer-catalogs";
const DEV_PREFIX = "/@demotracer-catalogs/";

export function runtimeCatalogs(): Plugin {
  let root = "";
  let development = false;
  let catalogs: Record<string, string> | undefined;
  const sourceFiles = [
    "src/data/professional-players.v2.json",
    "src/data/cs2-pro-steamid-lib.v1.jsonl",
    "src/data/cs2-cosmetic-catalog.v1.json",
    "pro-steamid-catalog-source.json",
  ];
  function readCatalogs() {
    if (catalogs) return catalogs;
    const [demo, registry, cosmetics, descriptor] = sourceFiles.map((path) => (
      readFileSync(resolve(root, path), "utf8")
    ));
    const metadata = JSON.parse(registry.split(/\r?\n/, 1)[0])._meta;
    const pinned = JSON.parse(descriptor);
    if (metadata.repository !== pinned.repository || metadata.commit !== pinned.commit
      || metadata.records !== pinned.records) {
      throw new Error("Professional identity snapshot does not match its pinned source revision");
    }
    const cosmetic = JSON.parse(cosmetics);
    if (cosmetic.schemaVersion !== 1) throw new Error("Unsupported cosmetic catalog schema");
    catalogs = {
      professionals: JSON.stringify(buildProfessionalPlayerRuntime(JSON.parse(demo), registry)),
      cosmetics: JSON.stringify({
        source: { cdnBaseUrl: cosmetic.source.cdnBaseUrl },
        inventorySimulator: cosmetic.inventorySimulator,
        items: cosmetic.items,
        agents: cosmetic.agents,
        stickers: cosmetic.stickers,
        charms: cosmetic.charms,
        musicKits: cosmetic.musicKits,
      }),
    };
    return catalogs;
  }
  return {
    name: "demotracer-runtime-catalogs",
    configResolved(config) {
      root = config.root;
      development = config.command === "serve";
    },
    buildStart() {
      catalogs = undefined;
      for (const file of sourceFiles) this.addWatchFile(resolve(root, file));
    },
    resolveId(id) {
      if (id === MODULE_ID) return `\0${MODULE_ID}`;
    },
    load(id) {
      if (id !== `\0${MODULE_ID}`) return;
      return Object.entries(readCatalogs()).map(([name, source]) => {
        const url = development
          ? JSON.stringify(`${DEV_PREFIX}${name}.json`)
          : `import.meta.ROLLUP_FILE_URL_${this.emitFile({ type: "asset", name: `${name}.json`, source })}`;
        return `export const ${name}CatalogUrl = ${url};`;
      }).join("\n");
    },
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const name = request.url?.split("?", 1)[0]?.slice(DEV_PREFIX.length).replace(/\.json$/, "");
        if (!request.url?.startsWith(DEV_PREFIX) || !name || !Object.hasOwn(readCatalogs(), name)) return next();
        response.setHeader("Content-Type", "application/json; charset=utf-8");
        response.setHeader("Cache-Control", "no-cache");
        response.end(readCatalogs()[name]);
      });
    },
    handleHotUpdate(context) {
      if (!sourceFiles.some((file) => normalizePath(resolve(root, file)) === normalizePath(context.file))) return;
      catalogs = undefined;
      context.server.ws.send({ type: "full-reload" });
    },
  };
}
