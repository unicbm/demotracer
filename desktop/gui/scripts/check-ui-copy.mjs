/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { readdirSync, readFileSync } from "node:fs";
import { extname, join, relative, resolve } from "node:path";
import ts from "typescript";

const sourceRoot = resolve(import.meta.dirname, "..", "src");
const failures = [];

function sourceFiles(directory, extensions) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return sourceFiles(path, extensions);
    return entry.isFile() && extensions.has(extname(path)) ? [path] : [];
  });
}

const jsxSourceFiles = sourceFiles(sourceRoot, new Set([".tsx"]));
const appSourceFiles = sourceFiles(sourceRoot, new Set([".ts", ".tsx"]));

for (const path of jsxSourceFiles) {
  const lines = readFileSync(path, "utf8").split(/\r?\n/);
  lines.forEach((line, index) => {
    const hardCodedChineseJsx = />[^<>{]*\p{Script=Han}[^<>{]*</u.test(line)
      || /(?:aria-label|title|placeholder)="[^"]*\p{Script=Han}[^"]*"/u.test(line);
    if (hardCodedChineseJsx) {
      failures.push(`${relative(sourceRoot, path)}:${index + 1} hard-coded Chinese JSX copy belongs in a localized dictionary`);
    }
    if (!line.includes('language === "zh"')) return;
    if (line.includes('"zh-CN"')) return;
    failures.push(`${relative(sourceRoot, path)}:${index + 1} branch user-facing copy through TEXT[language] instead of a language ternary`);
  });
}

const dictionaryPath = join(sourceRoot, "i18n.ts");
const dictionarySource = readFileSync(dictionaryPath, "utf8");
const zhDictionarySource = dictionarySource
  .slice(dictionarySource.indexOf("const zh = {"), dictionarySource.indexOf("const en: typeof zh = {"));
const localizedKeys = [...zhDictionarySource.matchAll(/^  ([A-Za-z][A-Za-z0-9]*):/gm)]
  .map((match) => match[1]);
const configPath = resolve(sourceRoot, "..", "tsconfig.json");
const configFile = ts.readConfigFile(configPath, ts.sys.readFile);
const config = ts.parseJsonConfigFileContent(configFile.config, ts.sys, resolve(sourceRoot, ".."));
const program = ts.createProgram(config.fileNames, config.options);
const checker = program.getTypeChecker();
const usedKeys = new Set();

function markDictionaryProperty(symbol) {
  for (const declaration of symbol?.declarations ?? []) {
    if (resolve(declaration.getSourceFile().fileName) === dictionaryPath) usedKeys.add(symbol.name);
  }
}

for (const path of appSourceFiles) {
  if (path === dictionaryPath) continue;
  const source = program.getSourceFile(path);
  if (!source) continue;
  function visit(node) {
    if (ts.isPropertyAccessExpression(node)) markDictionaryProperty(checker.getSymbolAtLocation(node.name));
    if (ts.isElementAccessExpression(node)) {
      const keyType = checker.getTypeAtLocation(node.argumentExpression);
      const keys = keyType.isUnion() ? keyType.types : [keyType];
      const owner = checker.getTypeAtLocation(node.expression);
      for (const key of keys) {
        if (key.isStringLiteral()) markDictionaryProperty(owner.getProperty(key.value));
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(source);
}

for (const key of localizedKeys) {
  if (usedKeys.has(key)) continue;
  failures.push(`i18n.ts unused localized copy key: ${key}`);
}

if (failures.length > 0) {
  console.error(`UI copy check failed:\n${failures.map((failure) => `- ${failure}`).join("\n")}`);
  process.exitCode = 1;
} else {
  console.log("UI copy check passed.");
}
