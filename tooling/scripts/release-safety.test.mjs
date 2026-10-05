// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { checkBuildOutput } from '../../desktop/gui/scripts/check-build-output.mjs';

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'demotracer-release-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const write = (name, value = '') => {
    mkdirSync(dirname(join(root, name)), { recursive: true });
    writeFileSync(join(root, name), value);
  };
  write('index.html', '<div id="root"></div>');
  return { root, write };
}

test('accepts bundled UI assets and the two product catalogs', t => {
  const { root, write } = fixture(t);
  for (const file of ['index-Abcd1234.js', 'index-Abcd1234.css', 'ancient-Abcd1234.webp', 'professionals-Abcd1234.json', 'cosmetics-Abcd1234.json']) write(`assets/${file}`);
  assert.equal(checkBuildOutput(root), 6);
});

test('rejects local files, sourcemaps and unexpected catalogs in build output', t => {
  for (const file of ['.env.local', 'recording.dem', 'private/session.json', 'assets/index-Abcd1234.js.map', 'assets/session-Abcd1234.json']) {
    const { root, write } = fixture(t);
    write(file);
    assert.throws(() => checkBuildOutput(root), /Unexpected/);
  }
});

test('rejects private values even inside an allowed asset name', t => {
  for (const value of ['C:' + '\\\\Users\\\\someone', '-----BEGIN ' + 'PRIVATE KEY-----', 'github_' + 'pat_' + 'x'.repeat(40)]) {
    const { root, write } = fixture(t);
    write('assets/index-Abcd1234.js', value);
    assert.throws(() => checkBuildOutput(root), /Private data/);
  }
});
