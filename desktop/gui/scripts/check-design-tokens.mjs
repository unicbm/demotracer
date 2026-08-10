/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readdirSync, readFileSync } from "node:fs";
import { basename, extname, join, resolve } from "node:path";

const sourceRoot = resolve(import.meta.dirname, "..", "src");
const tokenFile = "verge-theme.css";
const failures = [];

function cssFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return cssFiles(path);
    return entry.isFile() && extname(path) === ".css" ? [path] : [];
  });
}

function report(path, source, index, message) {
  const line = source.slice(0, index).split("\n").length;
  failures.push(`${path.slice(sourceRoot.length + 1)}:${line} ${message}`);
}

for (const path of cssFiles(sourceRoot)) {
  const source = readFileSync(path, "utf8");
  const isTokenFile = basename(path) === tokenFile;

  if (!isTokenFile) {
    for (const match of source.matchAll(/#[0-9a-f]{3,8}\b|rgba?\(/gi)) {
      report(path, source, match.index, "raw color; add a semantic token in verge-theme.css");
    }
  }

  for (const match of source.matchAll(/(?<!-)\bfont-size\s*:\s*([^;}]+)/gi)) {
    const value = match[1].trim();
    if (value !== "0" && /\d(?:\.\d+)?px\b/i.test(value)) {
      report(path, source, match.index, "raw font size; use a --type-* token");
    }
  }

  for (const match of source.matchAll(/\bfont\s*:\s*([^;}]+)/gi)) {
    if (/\d(?:\.\d+)?px\b/i.test(match[1])) {
      report(path, source, match.index, "raw font shorthand size; use a --type-* token");
    }
  }

  for (const match of source.matchAll(/\bfont-weight\s*:\s*(\d{3})\b/gi)) {
    report(path, source, match.index, "raw font weight; use a --weight-* token");
  }

  for (const match of source.matchAll(/\bfont-family\s*:\s*([^;}]+)/gi)) {
    if (!["inherit", "var(--font-ui)", "var(--mono)", "var(--library-display)", "var(--faq-display)"].includes(match[1].trim())) {
      report(path, source, match.index, "component font stack; use the global UI or mono font token");
    }
  }

  for (const match of source.matchAll(/\bborder-radius\s*:\s*([^;}]+)/gi)) {
    const value = match[1].trim();
    if (value !== "0" && value !== "1px" && /(?:\d+(?:\.\d+)?px|\d+%)/i.test(value)) {
      report(path, source, match.index, "raw radius; use a --radius-* token");
    }
  }
}

if (failures.length > 0) {
  console.error("Design token check failed:\n" + failures.map((failure) => `- ${failure}`).join("\n"));
  process.exitCode = 1;
} else {
  console.log("Design token check passed.");
}
