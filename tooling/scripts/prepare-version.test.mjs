// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { prepareVersion } from './prepare-version.mjs';

const plugin = 'server/plugins/DemoTracer/src/DemoTracer/Lifecycle/DemoTracerPlugin.cs';
const initial = {
  'desktop/gui/package.json': '{"version":"1.5.0","dependencies":{"example":"2.0.0"}}\n',
  'desktop/gui/src-tauri/tauri.conf.json': '{"version":"1.5.0"}\n',
  'shared/contracts/telemetry-contract.v1.json': '{"productVersion":"1.5.0","schemaVersion":1}\n',
  'desktop/gui/src-tauri/Cargo.toml': '[package]\nname = "cs2-demotracer-gui"\nversion = "1.5.0"\n',
  'desktop/converter/Cargo.toml': '[package]\nname = "cs2-demotracer"\nversion = "1.4.0"\n',
  'desktop/converter/Cargo.lock': '[[package]]\nname = "cs2-demotracer"\nversion = "1.4.0"\n',
  'desktop/gui/src-tauri/Cargo.lock': '[[package]]\nname = "cs2-demotracer"\nversion = "1.4.0"\n\n[[package]]\nname = "cs2-demotracer-gui"\nversion = "1.5.0"\n\n[[package]]\nname = "example"\nversion = "1.5.0"\n',
  [plugin]: 'public override string ModuleVersion => "1.3.0";\n',
};
function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'demotracer-version-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  for (const [path, content] of Object.entries(initial)) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), content);
  }
  mkdirSync(join(root, 'tooling/release'), { recursive: true });
  return { root, read: path => readFileSync(join(root, path), 'utf8') };
}
const notes = { notesZh: '修复回放。', notesEn: 'Fix playback.' };

test('GUI hotfix preserves independent converter, Playback and dependency versions', t => {
  const { root, read } = fixture(t);
  const changed = prepareVersion(root, { gui: '1.5.1', ...notes });
  assert.equal(changed.length, 7);
  assert.equal(read(plugin), initial[plugin]);
  assert.equal(read('desktop/converter/Cargo.lock'), initial['desktop/converter/Cargo.lock']);
  const lock = read('desktop/gui/src-tauri/Cargo.lock');
  assert.match(lock, /name = "cs2-demotracer-gui"\nversion = "1.5.1"/);
  assert.match(lock, /name = "example"\nversion = "1.5.0"/);
  assert.equal(JSON.parse(read('tooling/release/release-notes.v1.5.1.json')).zh, notes.notesZh);
  assert.equal(read('tooling/release/github-release.v1.5.1.md'), '# CS2 DemoTracer v1.5.1\n\nFix playback.\n\n---\n\n修复回放。\n');
});

test('Playback and converter can advance without changing GUI or requiring GUI notes', t => {
  const { root, read } = fixture(t);
  const changed = prepareVersion(root, { playback: '1.3.1', converter: '1.4.1' });
  assert.equal(changed.length, 4);
  assert.equal(read('desktop/gui/package.json'), initial['desktop/gui/package.json']);
  assert.match(read(plugin), /"1.3.1"/);
  for (const path of ['desktop/converter/Cargo.lock', 'desktop/gui/src-tauri/Cargo.lock']) {
    assert.match(read(path), /name = "cs2-demotracer"\nversion = "1.4.1"/);
  }
});

test('invalid input, downgrades, missing notes and no-op requests leave every source untouched', t => {
  const { root, read } = fixture(t);
  for (const options of [{}, { gui: '1.4.9' }, { gui: 'v1.5.1' }, { gui: '1.5.1-rc.1' }, { gui: '1.5.1' }, { playback: '../../1' }]) {
    assert.throws(() => prepareVersion(root, options));
    for (const [path, content] of Object.entries(initial)) assert.equal(read(path), content);
  }
});

test('inconsistent mirrors are rejected before any version is written', t => {
  const { root, read } = fixture(t);
  writeFileSync(join(root, 'desktop/gui/src-tauri/tauri.conf.json'), '{"version":"1.4.0"}');
  assert.throws(() => prepareVersion(root, { gui: '1.5.1', ...notes }), /Version mismatch/);
  assert.equal(read('desktop/gui/package.json'), initial['desktop/gui/package.json']);
});

test('existing release notes cannot be replaced by the preparation workflow', t => {
  const { root, read } = fixture(t);
  writeFileSync(join(root, 'tooling/release/github-release.v1.5.1.md'), 'Reviewed notes');
  assert.throws(() => prepareVersion(root, { gui: '1.5.1', ...notes }), /already exist/);
  assert.equal(read('desktop/gui/package.json'), initial['desktop/gui/package.json']);
  assert.equal(read('tooling/release/github-release.v1.5.1.md'), 'Reviewed notes');
});
