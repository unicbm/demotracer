/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import { fileName, formatBytes, formatDuration } from "./displayFormat.ts";

test("file labels support Windows paths, slash paths, and a trailing separator", () => {
  assert.equal(fileName("C:\\demos\\match.dem.zst"), "match.dem.zst");
  assert.equal(fileName("/demos/match.dem"), "match.dem");
  assert.equal(fileName("C:\\replays\\match\\"), "match");
  assert.equal(fileName(""), "");
});

test("file sizes use one bounded format for small bytes and large archives", () => {
  assert.equal(formatBytes(0), "0 B");
  assert.equal(formatBytes(0.5), "1 B");
  assert.equal(formatBytes("1536"), "1.50 KB");
  assert.equal(formatBytes(10 * 1024 ** 2), "10.0 MB");
  assert.equal(formatBytes(1024 ** 4), "1.00 TB");
  for (const value of [-1, NaN, Infinity, "invalid"]) assert.equal(formatBytes(value), "—");
});

test("durations round across minute and hour boundaries without inventing missing values", () => {
  assert.equal(formatDuration(0), "0:00");
  assert.equal(formatDuration(-5), "0:00");
  assert.equal(formatDuration(59.6), "1:00");
  assert.equal(formatDuration(3599.6), "1:00:00");
  assert.equal(formatDuration(3661), "1:01:01");
  for (const value of [null, undefined, NaN, Infinity]) assert.equal(formatDuration(value), null);
});
