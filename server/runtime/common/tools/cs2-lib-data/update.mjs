// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: {
  version: { type: 'string', default: 'latest' },
  'check-only': { type: 'boolean', default: false },
  report: { type: 'string' },
} });
if (!/^(latest|\d+\.\d+\.\d+)$/.test(values.version)) throw new Error('Expected latest or an exact stable version');
const directory = dirname(fileURLToPath(import.meta.url));
const sourcePath = resolve(directory, 'source.json');
const source = JSON.parse(readFileSync(sourcePath, 'utf8'));
const npm = (args, capture = false) => execFileSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', args, {
  cwd: directory, encoding: 'utf8', stdio: capture ? 'pipe' : 'inherit', shell: process.platform === 'win32',
});
const metadata = JSON.parse(npm(['view', `@ianlucas/cs2-lib@${values.version}`, 'version', 'gitHead', '--json'], true));
if (!/^\d+\.\d+\.\d+$/.test(metadata.version) || !/^[a-f0-9]{40}$/.test(metadata.gitHead) ||
    (values.version !== 'latest' && metadata.version !== values.version)) {
  throw new Error('Package metadata must contain the requested stable version and source commit');
}
const report = JSON.stringify({
  package: source.package,
  previousVersion: source.version,
  candidateVersion: metadata.version,
  candidateCommit: metadata.gitHead,
  dataChanged: source.version !== metadata.version || source.commit !== metadata.gitHead,
  reviewedRandomizerCommit: source.randomizerCommit,
}, null, 2);
console.log(report);
if (values.report) {
  const path = resolve(values.report);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${report}\n`);
}
if (!values['check-only']) {
  npm(['install', '--save-exact', '--ignore-scripts', `@ianlucas/cs2-lib@${metadata.version}`]);
  source.version = metadata.version;
  source.commit = metadata.gitHead;
  writeFileSync(sourcePath, `${JSON.stringify(source, null, 2)}\n`);
  for (const task of ['generate', 'check', 'test']) npm(['run', task]);
}
