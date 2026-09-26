// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

export function readComponentRegistry(directory) {
  const file = resolve(directory, 'components.json');
  const registry = JSON.parse(readFileSync(file, 'utf8'));
  if (registry.schemaVersion !== 1 || !Array.isArray(registry.components))
    throw new Error(`Invalid component registry: ${file}`);
  const ids = new Set(), paths = new Set();
  for (const component of registry.components) {
    if (!/^[a-z][a-z0-9-]*$/.test(component.id ?? '')
        || !/^unicbm\/(cs2-dtr-[a-z-]+|cs2-css-demotracer|demoparser)$/.test(component.repository ?? '')
        || !/^[a-zA-Z0-9._/-]+$/.test(component.path ?? '')
        || component.path.split('/').some(part => !part || part === '.' || part === '..')
        || !/^[a-zA-Z0-9._/-]+$/.test(component.branch ?? '')
        || !/^[a-zA-Z0-9-]+$/.test(component.tagPrefix ?? 'v'))
      throw new Error(`Invalid maintained component in ${file}`);
    const pathKey = component.path.toLowerCase();
    if (ids.has(component.id) || paths.has(pathKey))
      throw new Error(`Duplicate component id or path in ${file}: ${component.id}, ${component.path}`);
    ids.add(component.id);
    paths.add(pathKey);
  }
  return registry.components;
}
