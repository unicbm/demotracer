/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import {
  storedTelemetryConsent,
  telemetryDemoSource,
  telemetryDurationBucket,
  telemetryRoundsBucket,
} from "./telemetry.ts";

test("consent defaults to unknown and preserves explicit choices", () => {
  assert.equal(storedTelemetryConsent({ getItem: () => null }), "unknown");
  assert.equal(storedTelemetryConsent({ getItem: () => "enabled" }), "enabled");
  assert.equal(storedTelemetryConsent({ getItem: () => "disabled" }), "disabled");
});

test("demo source telemetry is categorical and never forwards source text", () => {
  assert.equal(telemetryDemoSource({ name: "5E", evidence: "serverName" }), "5e");
  assert.equal(telemetryDemoSource({ name: "Perfect World", evidence: "serverName" }), "perfect-world");
  assert.equal(telemetryDemoSource({ name: "Private Server 192.0.2.1", evidence: "serverName" }), "other");
  assert.equal(telemetryDemoSource(null), "unknown");
});

test("round and duration values are reduced to bounded buckets", () => {
  assert.equal(telemetryRoundsBucket(24), "13-24");
  assert.equal(telemetryRoundsBucket(25), "25+");
  assert.equal(telemetryRoundsBucket(Number.NaN), "unknown");
  assert.equal(telemetryDurationBucket(29_999), "10-29s");
  assert.equal(telemetryDurationBucket(10 * 60_000), "10m+");
});
