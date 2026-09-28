// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { execFileSync } from 'node:child_process';
import { appendFileSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function scopeFor(files, full = false) {
  files = files.filter(f => !f.endsWith('.md'));
  const any = pattern => files.some(f => pattern.test(f));
  const infrastructure = any(/^(\.github\/|tooling\/|components\.json$|\.gitmodules$|NuGet\.Config$|global\.json$)/);
  const shared = any(/^(shared\/contracts\/|server\/runtime\/common\/)/);
  const all = full || infrastructure || shared;
  const parser = all || any(/^third_party\/demoparser(?:\/|$)/);
  const converter = parser || any(/^desktop\/converter\//);
  const desktop = converter || any(/^desktop\/gui\/src-tauri\//);
  const frontend = all || any(/^desktop\/gui\/(?!src-tauri\/)/);
  const playback = all || any(/^server\/(plugins\/DemoTracer|runtime\/(BotController|BotHider|BotRandomizer))\//) || any(/^third_party\/BotHider(?:\/|$)/);
  const fuzz = all || any(/^server\/runtime\/BotController\//);
  return { parser, converter, desktop, frontend, playback, nativeCommon: all, fuzz };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  const full = ['workflow_dispatch', 'workflow_call'].includes(process.env.GITHUB_EVENT_NAME) || process.env.CI_FULL === 'true';
  const head = event.pull_request?.head.sha ?? process.env.GITHUB_SHA;
  const base = event.pull_request?.base.sha ?? event.before;
  let files = [], fallback = !base || /^0+$/.test(base);
  if (!full && !fallback) {
    try {
      const range = event.pull_request ? `${base}...${head}` : `${base}..${head}`;
      files = execFileSync('git', ['diff', '--name-only', '--no-renames', '-z', range], { encoding: 'utf8' }).split('\0').filter(Boolean);
    } catch { fallback = true; }
  }
  const scope = scopeFor(files, full || fallback);
  for (const [key, value] of Object.entries(scope)) appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${value}\n`);
  console.log(JSON.stringify({ full: full || fallback, changedFiles: files.length, scope }, null, 2));
}
