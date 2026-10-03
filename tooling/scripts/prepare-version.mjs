// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export function prepareVersion(root, { gui, playback, converter, notesZh, notesEn }) {
  const edits = new Map();
  const read = path => edits.get(path) ?? readFileSync(resolve(root, path), 'utf8');
  const replace = (path, pattern, previous, next) => {
    const source = read(path);
    const matches = [...source.matchAll(new RegExp(pattern, 'gm'))];
    if (matches.length !== 1 || matches[0][2] !== previous) {
      throw new Error(`Version mismatch in ${path}`);
    }
    edits.set(path, source.replace(new RegExp(pattern, 'm'), (_, prefix) => `${prefix}${next}`));
  };
  const jsonVersion = key => `("${key}"\\s*:\\s*")([^"\\r\\n]+)(?=")`;
  const manifestVersion = '(\\[package\\]\\s*\\r?\\nname = "[^"]+"\\s*\\r?\\nversion = ")([^"\\r\\n]+)(?=")';
  const lockVersion = name => `(\\[\\[package\\]\\]\\s*\\r?\\nname = "${name}"\\s*\\r?\\nversion = ")([^"\\r\\n]+)(?=")`;
  const pluginPath = 'server/plugins/DemoTracer/src/DemoTracer/Lifecycle/DemoTracerPlugin.cs';
  const pluginVersion = '(ModuleVersion\\s*=>\\s*")([^"\\r\\n]+)(?=")';
  const currentGui = JSON.parse(read('desktop/gui/package.json')).version;
  const currentPlayback = read(pluginPath).match(new RegExp(pluginVersion))?.[2];
  const currentConverter = read('desktop/converter/Cargo.toml').match(new RegExp(manifestVersion))?.[2];
  const versionParts = value => {
    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value ?? '')) {
      throw new Error(`Expected a stable x.y.z version: ${value}`);
    }
    return value.split('.').map(BigInt);
  };
  const validate = (next, current) => {
    const oldParts = versionParts(current), newParts = versionParts(next);
    const difference = newParts.map((n, i) => n - oldParts[i]).find(n => n !== 0n);
    if (difference < 0n) throw new Error(`Version cannot decrease: ${current} -> ${next}`);
  };
  gui ||= currentGui;
  playback ||= currentPlayback;
  converter ||= currentConverter;
  validate(gui, currentGui);
  validate(playback, currentPlayback);
  validate(converter, currentConverter);
  if (gui === currentGui && playback === currentPlayback && converter === currentConverter) {
    throw new Error('At least one product version must increase');
  }

  // Validate all mirrors before writing any file.
  for (const path of ['desktop/gui/package.json', 'desktop/gui/src-tauri/tauri.conf.json']) {
    replace(path, jsonVersion('version'), currentGui, gui);
  }
  replace('shared/contracts/telemetry-contract.v1.json', jsonVersion('productVersion'), currentGui, gui);
  replace('desktop/gui/src-tauri/Cargo.toml', manifestVersion, currentGui, gui);
  replace('desktop/gui/src-tauri/Cargo.lock', lockVersion('cs2-demotracer-gui'), currentGui, gui);
  replace('desktop/converter/Cargo.toml', manifestVersion, currentConverter, converter);
  for (const path of ['desktop/converter/Cargo.lock', 'desktop/gui/src-tauri/Cargo.lock']) {
    replace(path, lockVersion('cs2-demotracer'), currentConverter, converter);
  }
  replace(pluginPath, pluginVersion, currentPlayback, playback);

  if (gui !== currentGui) {
    if (!notesZh?.trim() || !notesEn?.trim()) throw new Error('GUI versions require Chinese and English release notes');
    const json = `tooling/release/release-notes.v${gui}.json`;
    const markdown = `tooling/release/github-release.v${gui}.md`;
    for (const path of [json, markdown]) {
      if (existsSync(resolve(root, path))) throw new Error(`Release notes already exist: ${path}`);
    }
    edits.set(json, `${JSON.stringify({ zh: notesZh.trim(), en: notesEn.trim() }, null, 2)}\n`);
    edits.set(markdown, `# CS2 DemoTracer v${gui}\n\n${notesEn.trim()}\n\n---\n\n${notesZh.trim()}\n`);
  }
  const changed = [];
  for (const [path, content] of edits) {
    if (existsSync(resolve(root, path)) && readFileSync(resolve(root, path), 'utf8') === content) continue;
    writeFileSync(resolve(root, path), content);
    changed.push(path);
  }
  return changed;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const changed = prepareVersion(fileURLToPath(new URL('../..', import.meta.url)), {
    gui: process.env.GUI_VERSION,
    playback: process.env.PLAYBACK_VERSION,
    converter: process.env.CONVERTER_VERSION,
    notesZh: process.env.RELEASE_NOTES_ZH,
    notesEn: process.env.RELEASE_NOTES_EN,
  });
  console.log(changed.join('\n'));
}
