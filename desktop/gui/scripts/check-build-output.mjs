/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import { lstatSync, readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export function checkBuildOutput(root) {
  const files = [];
  function visit(directory, prefix = "") {
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      const relative = `${prefix}${name}`;
      const stat = lstatSync(path);
      if (stat.isSymbolicLink()) throw new Error(`Linked build asset is not allowed: ${relative}`);
      if (stat.isDirectory() && relative === "assets") visit(path, "assets/");
      else if (stat.isFile()) files.push(relative);
      else throw new Error(`Unexpected build directory or entry: ${relative}`);
    }
  }
  visit(root);
  if (!files.includes("index.html")) throw new Error("Missing frontend index.html");
  for (const file of files) {
    const productAsset = /^assets\/[\w.-]+-[\w-]{8}\.(?:js|css|svg|webp|png|jpg|woff2?)$/.test(file);
    const catalog = /^assets\/(?:professionals|cosmetics)-[\w-]{8}\.json$/.test(file);
    if (file !== "index.html" && !productAsset && !catalog) {
      throw new Error(`Unexpected frontend build asset: ${file}`);
    }
    if (/\.(?:html|js|css|svg|json)$/.test(file)) {
      const text = readFileSync(join(root, file), "utf8");
      if (/[A-Z]:[\\/]+Users[\\/]|-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----|(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{30,}/i.test(text)) {
        throw new Error(`Private data detected in frontend build asset: ${file}`);
      }
    }
  }
  return files.length;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const root = fileURLToPath(new URL("../dist/", import.meta.url));
  console.log(`Frontend output allowlist passed: ${checkBuildOutput(root)} files.`);
}
