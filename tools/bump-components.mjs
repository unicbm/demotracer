// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { readComponentRegistry } from './component-registry.mjs';

const run = (exe, args, cwd = process.cwd()) => execFileSync(exe, args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] }).trim();
const components = readComponentRegistry(process.cwd());
const changes = [];
for (const component of components) {
  const cwd = resolve(component.path);
  const releases = JSON.parse(run('gh', ['api', `repos/${component.repository}/releases?per_page=100`]));
  const prefix = component.tagPrefix ?? 'v';
  const versions = releases.filter(r => !r.draft && !r.prerelease && r.tag_name.startsWith(prefix))
    .map(r => ({ ...r, version: r.tag_name.slice(prefix.length) }))
    .filter(r => /^\d+\.\d+\.\d+$/.test(r.version))
    .sort((a, b) => {
      const av = a.version.split('.').map(Number), bv = b.version.split('.').map(Number);
      return bv[0] - av[0] || bv[1] - av[1] || bv[2] - av[2];
    });
  const release = versions[0];
  if (!release) throw new Error(`No maintained release: ${component.repository}`);
  const before = run('git', ['rev-parse', 'HEAD'], cwd);
  run('git', ['fetch', '--no-tags', `https://github.com/${component.repository}.git`, `refs/tags/${release.tag_name}`], cwd);
  const after = run('git', ['rev-parse', 'FETCH_HEAD^{commit}'], cwd);
  if (before === after) continue;
  // A pinned unreleased fix or newer branch must never be silently rolled back.
  try { run('git', ['merge-base', '--is-ancestor', before, after], cwd); }
  catch { throw new Error(`Refusing rollback/divergence: ${component.repository} ${before} -> ${after}`); }
  run('git', ['checkout', '--detach', after], cwd);
  run('git', ['submodule', 'update', '--init', '--recursive'], cwd);
  run('git', ['add', '--', component.path]);
  changes.push(`- ${component.repository}: ${before.slice(0, 12)} → ${after.slice(0, 12)} ([${release.tag_name}](${release.html_url}))`);
}
const body = ['Update maintained DemoTracer infrastructure releases.', '', ...changes, '',
  'The gitlinks pin exact source commits. Original upstream repositories are not update sources.',
  'Review the integration CI before merging; ABI and product versions are not automatically changed.', ''].join('\n');
writeFileSync(process.env.RUNNER_TEMP ? resolve(process.env.RUNNER_TEMP, 'component-update.md') : '.component-update.md', body);
console.log(changes.length ? body : 'All components already match their maintained releases.');
