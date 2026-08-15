/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readdirSync, readFileSync } from "node:fs";
import { extname, join, relative, resolve } from "node:path";

const sourceRoot = resolve(import.meta.dirname, "..", "src");
const failures = [];

function sourceFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    return entry.isFile() && extname(path) === ".tsx" ? [path] : [];
  });
}

for (const path of sourceFiles(sourceRoot)) {
  const lines = readFileSync(path, "utf8").split(/\r?\n/);
  lines.forEach((line, index) => {
    if (!line.includes('language === "zh"')) return;
    if (line.includes('"zh-CN"')) return;
    failures.push(`${relative(sourceRoot, path)}:${index + 1} branch user-facing copy through TEXT[language] instead of a language ternary`);
  });
}

if (failures.length > 0) {
  console.error(`UI copy check failed:\n${failures.map((failure) => `- ${failure}`).join("\n")}`);
  process.exitCode = 1;
} else {
  console.log("UI copy check passed.");
}
