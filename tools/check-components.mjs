// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { readComponentRegistry } from './component-registry.mjs';

const root = process.cwd();
const git = (cwd, ...args) => execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim();
const registry = readComponentRegistry(root);
const expected = new Map(registry.map(c => [c.id, { repository: c.repository, sha: git(resolve(root, c.path), 'rev-parse', 'HEAD') }]));
const status = git(root, 'submodule', 'status', '--recursive');
if (status.split('\n').some(line => /^[-+U]/.test(line))) throw new Error('Submodules must be initialized at their committed gitlinks');
function visit(dir) {
  const config = resolve(dir, 'components.json');
  if (!existsSync(config)) return;
  const components = readComponentRegistry(dir);
  if (!components.length) return;
  const entries = git(dir, 'config', '--file', '.gitmodules', '--null', '--get-regexp', '^submodule[.].*[.]path$');
  const modules = new Map();
  for (const entry of entries.split('\0').filter(Boolean)) {
    const [key, path] = entry.split('\n');
    if (!path || modules.has(path.toLowerCase())) throw new Error(`Duplicate or invalid .gitmodules path in ${dir}`);
    modules.set(path.toLowerCase(), key.slice(0, -'path'.length) + 'url');
  }
  for (const component of components) {
    const urlKey = modules.get(component.path.toLowerCase());
    if (!urlKey || git(dir, 'config', '--file', '.gitmodules', '--get-all', urlKey) !== `https://github.com/${component.repository}.git`)
      throw new Error(`Component repository does not match .gitmodules: ${dir}/${component.path}`);
    if (!/^160000 [0-9a-f]{40} 0\t/.test(git(dir, 'ls-files', '--stage', '--', component.path)))
      throw new Error(`Component is not a tracked gitlink: ${dir}/${component.path}`);
    const child = resolve(dir, component.path);
    const actual = git(child, 'rev-parse', 'HEAD');
    const baseline = expected.get(component.id);
    if (baseline && (baseline.repository !== component.repository || baseline.sha !== actual))
      throw new Error(`Mixed ${component.id} pins at ${component.path}: ${actual}, expected ${baseline.repository}@${baseline.sha}. Release matching consumers before integration.`);
    expected.set(component.id, { repository: component.repository, sha: actual });
    visit(child);
  }
}
visit(root);
console.log(`Verified ${expected.size} maintained component pins and their recursive dependencies.`);
