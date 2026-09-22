/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import { createCatalogResource, createJsonCatalogResource } from "./catalogResource.ts";

test("catalogs load only on demand and share pending and completed reads", async () => {
  let calls = 0;
  let complete!: (data: { handle: string }) => void;
  const data = { handle: "donk" };
  const resource = createCatalogResource(() => {
    calls++;
    return new Promise<typeof data>((resolve) => { complete = resolve; });
  });
  assert.equal(calls, 0);
  const first = resource.load();
  const second = resource.load();
  assert.strictEqual(first, second);
  await Promise.resolve();
  assert.equal(calls, 1);
  complete(data);
  await first;
  assert.strictEqual(resource.getSnapshot().data, data);
  await resource.load();
  assert.equal(calls, 1);
});

test("failed catalog reads notify consumers and can be retried without remounting", async (context) => {
  let attempt = 0;
  context.mock.method(globalThis, "fetch", async () => {
    attempt++;
    if (attempt === 1) return new Response("missing", { status: 404 });
    if (attempt === 2) return new Response("invalid JSON");
    return Response.json({ handle: "NiKo" });
  });
  const resource = createJsonCatalogResource<{ handle: string }>("/assets/players.json");
  let notified = false;
  const unsubscribe = resource.subscribe(() => { notified = true; });
  await resource.load();
  assert.equal(notified, true);
  assert.equal(resource.getSnapshot().error, true);
  assert.equal(resource.getSnapshot().data, null);
  await resource.load();
  assert.equal(resource.getSnapshot().error, true);
  await resource.load();
  assert.deepEqual(resource.getSnapshot().data, { handle: "NiKo" });
  assert.equal(resource.getSnapshot().error, false);
  unsubscribe();
});
