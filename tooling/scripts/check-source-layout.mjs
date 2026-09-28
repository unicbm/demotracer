// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
const { components } = JSON.parse(readFileSync('components.json', 'utf8'));
const submodules = new Map([
  ['third_party/demoparser', { id: 'parser', repository: 'unicbm/demoparser' }],
  ['third_party/BotHider', { id: 'hider', repository: 'XBribo/CS2-Bot-Hider' }],
]);
const modules = git('config', '--file', '.gitmodules', '--get-regexp', '^submodule\..*\.path$');
if (modules.split('\n').length !== submodules.size || modules.split('\n').some(line => !submodules.has(line.split(' ')[1]))) {
  throw new Error('Only the parser and external BotHider may be source submodules.');
}
const entries = git('ls-files', '--stage').split('\n');
const links = entries.filter(line => line.startsWith('160000 '));
if (links.length !== submodules.size || links.some(line => !submodules.has(line.split('\t')[1]))) {
  throw new Error('Expected pinned parser and BotHider gitlinks.');
}
const ids = new Set();
for (const c of components) {
  if (ids.has(c.id)) throw new Error(`Duplicate component: ${c.id}`);
  ids.add(c.id);
  if (c.source === 'product') {
    if (c.repository !== 'unicbm/demotracer' || !existsSync(c.path) ||
        existsSync(resolve(c.path, '.git')) || existsSync(resolve(c.path, '.gitmodules')) || existsSync(resolve(c.path, '.deps'))) {
      throw new Error(`Product module must use the single working tree: ${c.path}`);
    }
  } else if (c.source !== 'submodule' || c.id !== submodules.get(c.path)?.id || c.repository !== submodules.get(c.path)?.repository) {
    throw new Error(`Unsupported source entry: ${c.id}`);
  }
}
if (ids.size !== 7 || !ids.has('parser')) throw new Error('Incomplete component source inventory.');
for (const [path, { repository }] of submodules) {
  if (git('config', '--file', '.gitmodules', '--get', `submodule.${path}.url`) !== `https://github.com/${repository}.git`) {
    throw new Error(`Unexpected submodule repository: ${path}`);
  }
  if (process.argv.includes('--require-checkout')) {
    const expected = links.find(line => line.endsWith(`\t${path}`)).split(' ')[1];
    if (!existsSync(`${path}/.git`) || git('-C', path, 'rev-parse', 'HEAD') !== expected) {
      throw new Error(`Initialize ${path} at its committed gitlink before building.`);
    }
  }
}
console.log('Source layout verified: five product modules and two pinned dependencies.');
