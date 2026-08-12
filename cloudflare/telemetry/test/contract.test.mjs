// ---------------------------------------------------------------------------------------------
// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
// ---------------------------------------------------------------------------------------------

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import worker, { TELEMETRY_FIELDS, TELEMETRY_SCHEMA_VERSION, validateTelemetryEvent } from "../src/index.js";

const contract = JSON.parse(
  await readFile(new URL("../../../shared/contracts/telemetry-contract.v1.json", import.meta.url), "utf8"),
);

function validEvent(patch = {}) {
  return {
    schemaVersion: 1,
    eventId: "18bc1c0c-5fca-4b11-bac2-9b6765d172ad",
    dailyId: "a".repeat(64),
    appVersion: "1.1.2",
    playbackVersion: "1.1.2",
    kind: "conversion",
    outcome: "success",
    demoSource: "5e",
    errorCode: "-",
    roundsBucket: "13-24",
    durationBucket: "10-29s",
    ...patch,
  };
}

function rateLimiter(success = true) {
  return { limit: async () => ({ success }) };
}

function ingestEnv(globalAllowed = true, installationAllowed = true) {
  return {
    GLOBAL_RATE_LIMITER: rateLimiter(globalAllowed),
    INSTALLATION_RATE_LIMITER: rateLimiter(installationAllowed),
  };
}

test("implementation matches the public v1 contract", () => {
  assert.equal(TELEMETRY_SCHEMA_VERSION, contract.schemaVersion);
  assert.deepEqual([...TELEMETRY_FIELDS].sort(), Object.keys(contract.fields).sort());
  assert.equal(contract.consent.default, "disabled");
});

test("accepts the bounded event schema", () => {
  assert.equal(validateTelemetryEvent(validEvent()).ok, true);
  assert.equal(
    validateTelemetryEvent(validEvent({ kind: "session", outcome: "ping", demoSource: "unknown", roundsBucket: "unknown", durationBucket: "unknown" })).ok,
    true,
  );
});

test("rejects unknown and potentially identifying fields", () => {
  const event = validEvent({ demoName: "private.dem" });
  assert.deepEqual(validateTelemetryEvent(event), { ok: false, error: "unexpected_field" });
});

test("rejects inconsistent outcomes", () => {
  assert.equal(validateTelemetryEvent(validEvent({ outcome: "failure" })).error, "missing_failure_error_code");
  assert.equal(validateTelemetryEvent(validEvent({ kind: "session", outcome: "success" })).error, "invalid_session_event");
});

test("accepts only fixed coarse failure categories", () => {
  assert.equal(validateTelemetryEvent(validEvent({ outcome: "failure", errorCode: "parse_failed" })).ok, true);
  assert.equal(validateTelemetryEvent(validEvent({ outcome: "failure", errorCode: "demo_parse_failed" })).error, "invalid_error_code");
  assert.equal(validateTelemetryEvent(validEvent({ outcome: "failure", errorCode: "C:private:demo.dem" })).error, "invalid_error_code");
});

test("accepts only local source categories and never source text", () => {
  assert.equal(validateTelemetryEvent(validEvent({ demoSource: "perfect-world" })).ok, true);
  assert.equal(validateTelemetryEvent(validEvent({ demoSource: "Private Server 127.0.0.1" })).error, "invalid_demo_source");
});

test("health endpoint is public and contains no operational data", async () => {
  const response = await worker.fetch(new Request("https://telemetry.detr.site/healthz"), {});
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { status: "ok", schemaVersion: 1 });
  assert.equal(response.headers.get("cache-control"), "no-store");
});

test("rejects globally rate-limited ingestion before reading D1", async () => {
  const response = await worker.fetch(
    new Request("https://telemetry.detr.site/v1/events", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(validEvent()),
    }),
    ingestEnv(false, true),
  );
  assert.equal(response.status, 429);
  assert.deepEqual(await response.json(), { error: "rate_limited" });
});

test("rejects a daily identifier that exceeds its own rate", async () => {
  const response = await worker.fetch(
    new Request("https://telemetry.detr.site/v1/events", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(validEvent()),
    }),
    ingestEnv(true, false),
  );
  assert.equal(response.status, 429);
  assert.deepEqual(await response.json(), { error: "rate_limited" });
});

test("streams request bodies through the 4 KiB limit", async () => {
  const oversized = new TextEncoder().encode("x".repeat(4097));
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(oversized.subarray(0, 2048));
      controller.enqueue(oversized.subarray(2048));
      controller.close();
    },
  });
  const response = await worker.fetch(
    new Request("https://telemetry.detr.site/v1/events", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body,
      duplex: "half",
    }),
    ingestEnv(),
  );
  assert.equal(response.status, 413);
  assert.deepEqual(await response.json(), { error: "payload_too_large" });
});
