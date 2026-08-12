/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import assert from "node:assert/strict";
import test from "node:test";
import {
  storedAggregateTelemetryEnabled,
  storedPresenceTelemetryConsent,
  telemetryDemoSource,
  telemetryDurationBucket,
  telemetryRoundsBucket,
} from "./telemetry.ts";

function storage(values: Record<string, string>) {
  return { getItem: (key: string) => values[key] ?? null };
}

test("aggregate statistics default on and preserve an explicit opt-out", () => {
  assert.equal(storedAggregateTelemetryEnabled(storage({})), true);
  assert.equal(storedAggregateTelemetryEnabled(storage({ "demotracer.aggregate-telemetry.v1": "disabled" })), false);
  assert.equal(storedAggregateTelemetryEnabled(storage({ "demotracer.telemetry-consent.v1": "disabled" })), false);
});

test("presence statistics default to no consent and migrate an earlier choice", () => {
  assert.equal(storedPresenceTelemetryConsent(storage({})), "unknown");
  assert.equal(storedPresenceTelemetryConsent(storage({ "demotracer.presence-telemetry-consent.v1": "enabled" })), "enabled");
  assert.equal(storedPresenceTelemetryConsent(storage({ "demotracer.telemetry-consent.v1": "disabled" })), "disabled");
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
