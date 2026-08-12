// ---------------------------------------------------------------------------------------------
// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
// ---------------------------------------------------------------------------------------------

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import worker, {
  AGGREGATE_FIELDS,
  PRESENCE_FIELDS,
  TELEMETRY_SCHEMA_VERSION,
  validateAggregateEvent,
  validatePresenceEvent,
} from "../src/index.js";

const contract = JSON.parse(
  await readFile(new URL("../../../shared/contracts/telemetry-contract.v1.json", import.meta.url), "utf8"),
);

function validAggregate(patch = {}) {
  return {
    schemaVersion: 1,
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

function validPresence(patch = {}) {
  return {
    schemaVersion: 1,
    dailyId: "a".repeat(64),
    appVersion: "1.1.2",
    playbackVersion: "1.1.2",
    ...patch,
  };
}

function rateLimiter(success = true) {
  return { limit: async () => ({ success }) };
}

function recordingDb() {
  const writes = [];
  return {
    writes,
    prepare(sql) {
      return {
        bind(...params) {
          return {
            run: async () => {
              writes.push({ sql, params });
              return { meta: { changes: 1 } };
            },
          };
        },
      };
    },
    async batch(statements) {
      for (const statement of statements) await statement.run();
    },
  };
}

function ingestEnv({ globalAllowed = true, installationAllowed = true, db = recordingDb() } = {}) {
  return {
    DB: db,
    GLOBAL_RATE_LIMITER: rateLimiter(globalAllowed),
    INSTALLATION_RATE_LIMITER: rateLimiter(installationAllowed),
  };
}

function post(path, body) {
  return new Request(`https://telemetry.detr.site${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

test("implementation matches the public split-channel contract", () => {
  assert.equal(TELEMETRY_SCHEMA_VERSION, contract.schemaVersion);
  assert.deepEqual([...AGGREGATE_FIELDS].sort(), Object.keys(contract.channels.aggregate.fields).sort());
  assert.deepEqual([...PRESENCE_FIELDS].sort(), Object.keys(contract.channels.presence.fields).sort());
  assert.equal(contract.channels.aggregate.default, "enabled");
  assert.equal(contract.channels.presence.default, "disabled");
});

test("aggregate events contain no request, daily, or installation identifier", () => {
  assert.equal(validateAggregateEvent(validAggregate()).ok, true);
  for (const field of ["eventId", "dailyId", "installationId"]) {
    assert.deepEqual(validateAggregateEvent(validAggregate({ [field]: "private" })), {
      ok: false,
      error: "unexpected_field",
    });
  }
});

test("aggregate events accept only fixed task categories", () => {
  assert.equal(validateAggregateEvent(validAggregate({ kind: "analysis" })).ok, true);
  assert.equal(validateAggregateEvent(validAggregate({ kind: "session" })).error, "invalid_kind");
  assert.equal(validateAggregateEvent(validAggregate({ outcome: "failure" })).error, "missing_failure_error_code");
  assert.equal(validateAggregateEvent(validAggregate({ outcome: "failure", errorCode: "parse_failed" })).ok, true);
  assert.equal(validateAggregateEvent(validAggregate({ errorCode: "C:private:demo.dem" })).error, "invalid_error_code");
  assert.equal(validateAggregateEvent(validAggregate({ demoSource: "Private Server 127.0.0.1" })).error, "invalid_demo_source");
});

test("presence events accept only the rotating daily identifier and versions", () => {
  assert.equal(validatePresenceEvent(validPresence()).ok, true);
  assert.equal(validatePresenceEvent(validPresence({ dailyId: "short" })).error, "invalid_daily_id");
  assert.deepEqual(validatePresenceEvent(validPresence({ demoSource: "5e" })), {
    ok: false,
    error: "unexpected_field",
  });
});

test("health endpoint is public and contains no operational data", async () => {
  const response = await worker.fetch(new Request("https://telemetry.detr.site/healthz"), {});
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { status: "ok", schemaVersion: 1 });
  assert.equal(response.headers.get("cache-control"), "no-store");
});

test("aggregate ingestion writes only an hourly counter", async () => {
  const db = recordingDb();
  const response = await worker.fetch(post("/v1/aggregate", validAggregate()), ingestEnv({ db }));
  assert.equal(response.status, 202);
  assert.deepEqual(await response.json(), { accepted: true });
  assert.equal(db.writes.length, 1);
  assert.match(db.writes[0].sql, /INSERT INTO hourly_metrics/);
  assert.equal(db.writes[0].params.includes("a".repeat(64)), false);
});

test("presence ingestion updates only active and daily leases", async () => {
  const db = recordingDb();
  const response = await worker.fetch(post("/v1/presence", validPresence()), ingestEnv({ db }));
  assert.equal(response.status, 202);
  assert.deepEqual(await response.json(), { accepted: true });
  assert.equal(db.writes.length, 2);
  assert.match(db.writes[0].sql, /INSERT INTO active_installations/);
  assert.match(db.writes[1].sql, /INSERT INTO daily_installations/);
});

test("rejects globally rate-limited ingestion before reading D1", async () => {
  const response = await worker.fetch(
    post("/v1/aggregate", validAggregate()),
    ingestEnv({ globalAllowed: false }),
  );
  assert.equal(response.status, 429);
  assert.deepEqual(await response.json(), { error: "rate_limited" });
});

test("rejects a presence identifier that exceeds its own rate", async () => {
  const response = await worker.fetch(
    post("/v1/presence", validPresence()),
    ingestEnv({ installationAllowed: false }),
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
    new Request("https://telemetry.detr.site/v1/aggregate", {
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
