// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import assert from 'node:assert/strict';
import test from 'node:test';
import { scopeFor } from './ci-scope.mjs';

test('docs changes do not compile products', () => {
  assert.ok(Object.values(scopeFor(['README.md', 'docs/FORMAT.md', 'server/runtime/BotController/README.md', 'desktop/converter/PROVENANCE.md'])).every(v => !v));
});
test('frontend avoids native and Rust work', () => {
  assert.deepEqual(scopeFor(['desktop/gui/src/App.tsx']), { parser:false, converter:false, desktop:false, frontend:true, playback:false, nativeCommon:false, fuzz:false });
});
test('parser changes validate converter and GUI consumers', () => {
  const s = scopeFor(['third_party/demoparser']);
  assert.ok(s.parser && s.converter && s.desktop);
  assert.equal(s.playback, false);
});
test('provider changes exercise matched playback packaging', () => {
  assert.ok(scopeFor(['third_party/BotHider']).playback);
  assert.ok(scopeFor(['server/runtime/BotHider/src/plugin.cpp']).playback);
  assert.ok(scopeFor(['server/runtime/BotController/src/plugin.cpp']).fuzz);
});
test('shared contracts, infrastructure and release validation run everything', () => {
  for (const paths of [['shared/contracts/playback-contract.v1.json'], ['server/runtime/common/econ/randomizer-catalog.json'], ['.github/workflows/ci.yml'], ['tooling/scripts/package-server.ps1']]) {
    assert.ok(Object.values(scopeFor(paths)).every(Boolean));
  }
  assert.ok(Object.values(scopeFor([], true)).every(Boolean));
});
