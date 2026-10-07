// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { execFileSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
const files = execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8' }).split('\0').filter(Boolean);
const forbidden = files.filter(p => /^(desktop|cloudflare|third_party)\/|(^|\/)(target|bin|obj|tmp|output)\/|\.(dem|dtr|cs2rec|exe|dll|pdb|map|log|pem|key|pfx|p12)$|(^|\/)\.env($|\.)/i.test(p));
if (forbidden.length) throw new Error(`Private/build files in public source: ${forbidden.join(', ')}`);
for (const path of files) {
  const data = readFileSync(path);
  if (data.includes(0)) continue;
  const text = data.toString('utf8');
  if (/C:[\\/]+Users[\\/]+|-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----|github_pat_[A-Za-z0-9_]{40,}|gh[pousr]_[A-Za-z0-9]{30,}/.test(text)) {
    throw new Error(`Local path or credential in public source: ${path}`);
  }
}
if (existsSync('desktop') || existsSync('.gitmodules')) throw new Error('Public checkout must not contain desktop sources or parser submodules.');
const { components } = JSON.parse(readFileSync('components.json', 'utf8'));
const expected = new Set(['common', 'controller', 'hider', 'randomizer', 'css']);
for (const c of components) {
  if (!expected.delete(c.id) || !c.path.startsWith('server/') || !existsSync(c.path) || c.repository !== 'unicbm/demotracer' || c.source !== 'product') throw new Error(`Unexpected component: ${c.id}`);
}
if (expected.size) throw new Error('Incomplete playback sources.');
const contract = JSON.parse(readFileSync('shared/contracts/playback-contract.v1.json', 'utf8'));
if ([contract.dtr_writer, contract.dtr_reader.min, contract.dtr_reader.max].some(v => v !== 12)) throw new Error('Stable DTR contract must remain v12.');
console.log(`Public source boundary verified (${files.length} files, five playback modules, DTR v12).`);
