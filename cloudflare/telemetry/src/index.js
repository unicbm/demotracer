// ---------------------------------------------------------------------------------------------
// Copyright (c) 2026 unicbm. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0 only.
// See LICENSE in the project root for license information.
// ---------------------------------------------------------------------------------------------

export const TELEMETRY_SCHEMA_VERSION = 1;
export const AGGREGATE_FIELDS = Object.freeze([
  "schemaVersion",
  "appVersion",
  "playbackVersion",
  "kind",
  "outcome",
  "demoSource",
  "errorCode",
  "roundsBucket",
  "durationBucket",
]);
export const PRESENCE_FIELDS = Object.freeze([
  "schemaVersion",
  "dailyId",
  "appVersion",
  "playbackVersion",
]);

const EVENT_KINDS = new Set(["analysis", "conversion"]);
const OUTCOMES = new Set(["success", "failure"]);
const DEMO_SOURCES = new Set([
  "5e",
  "perfect-world",
  "faceit",
  "valve-premier",
  "matchmaking",
  "pracc",
  "popflash",
  "esportal",
  "gamers-club",
  "fastcup",
  "renown",
  "cevo",
  "challengermode",
  "esea",
  "starladder",
  "flashpoint",
  "blast",
  "pgl",
  "esl",
  "matchzy",
  "ebot",
  "get5",
  "other",
  "unknown",
]);
const ROUND_BUCKETS = new Set(["0", "1-4", "5-12", "13-24", "25+", "unknown"]);
const DURATION_BUCKETS = new Set(["<10s", "10-29s", "30-59s", "1-2m", "3-9m", "10m+", "unknown"]);
const ERROR_CODES = new Set([
  "cancelled",
  "input_unavailable",
  "parse_failed",
  "validation_failed",
  "output_conflict",
  "output_failed",
  "batch_failed",
  "playback_failed",
  "environment_failed",
  "network_failed",
  "internal_error",
  "unknown",
  "-",
]);
const DAILY_ID_PATTERN = /^[0-9a-f]{64}$/;
const VERSION_PATTERN = /^(?:-|[0-9A-Za-z][0-9A-Za-z.+-]{0,31})$/;
const MAX_PAYLOAD_BYTES = 4 * 1024;
const JSON_HEADERS = Object.freeze({
  "cache-control": "no-store",
  "content-type": "application/json; charset=utf-8",
  "x-content-type-options": "nosniff",
});

function jsonResponse(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: JSON_HEADERS });
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function hasExactFields(value, fields) {
  const keys = Object.keys(value);
  return keys.length === fields.length && keys.every((key) => fields.includes(key));
}

function hasValidVersions(value) {
  return typeof value.appVersion === "string"
    && VERSION_PATTERN.test(value.appVersion)
    && typeof value.playbackVersion === "string"
    && VERSION_PATTERN.test(value.playbackVersion);
}

export function validateAggregateEvent(value) {
  if (!isPlainObject(value)) return { ok: false, error: "invalid_payload" };
  if (!hasExactFields(value, AGGREGATE_FIELDS)) return { ok: false, error: "unexpected_field" };
  if (value.schemaVersion !== TELEMETRY_SCHEMA_VERSION) return { ok: false, error: "unsupported_schema" };
  if (!hasValidVersions(value)) return { ok: false, error: "invalid_version" };
  if (typeof value.kind !== "string" || !EVENT_KINDS.has(value.kind)) return { ok: false, error: "invalid_kind" };
  if (typeof value.outcome !== "string" || !OUTCOMES.has(value.outcome)) return { ok: false, error: "invalid_outcome" };
  if (typeof value.demoSource !== "string" || !DEMO_SOURCES.has(value.demoSource)) return { ok: false, error: "invalid_demo_source" };
  if (typeof value.errorCode !== "string" || !ERROR_CODES.has(value.errorCode)) return { ok: false, error: "invalid_error_code" };
  if (typeof value.roundsBucket !== "string" || !ROUND_BUCKETS.has(value.roundsBucket)) return { ok: false, error: "invalid_rounds_bucket" };
  if (typeof value.durationBucket !== "string" || !DURATION_BUCKETS.has(value.durationBucket)) return { ok: false, error: "invalid_duration_bucket" };
  if (value.outcome === "success" && value.errorCode !== "-") return { ok: false, error: "invalid_success_error_code" };
  if (value.outcome === "failure" && value.errorCode === "-") return { ok: false, error: "missing_failure_error_code" };
  return { ok: true, event: value };
}

export function validatePresenceEvent(value) {
  if (!isPlainObject(value)) return { ok: false, error: "invalid_payload" };
  if (!hasExactFields(value, PRESENCE_FIELDS)) return { ok: false, error: "unexpected_field" };
  if (value.schemaVersion !== TELEMETRY_SCHEMA_VERSION) return { ok: false, error: "unsupported_schema" };
  if (typeof value.dailyId !== "string" || !DAILY_ID_PATTERN.test(value.dailyId)) return { ok: false, error: "invalid_daily_id" };
  if (!hasValidVersions(value)) return { ok: false, error: "invalid_version" };
  return { ok: true, event: value };
}

async function readBoundedText(request, maxBytes) {
  if (request.body == null) return { ok: true, text: "" };

  const reader = request.body.getReader();
  const decoder = new TextDecoder();
  let bytesRead = 0;
  let text = "";
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      bytesRead += value.byteLength;
      if (bytesRead > maxBytes) {
        await reader.cancel();
        return { ok: false };
      }
      text += decoder.decode(value, { stream: true });
    }
    text += decoder.decode();
    return { ok: true, text };
  } finally {
    reader.releaseLock();
  }
}

async function checkRateLimit(limiter, key) {
  if (limiter == null || typeof limiter.limit !== "function") return null;
  const result = await limiter.limit({ key });
  return result?.success === true;
}

async function readJsonRequest(request) {
  const contentType = request.headers.get("content-type") ?? "";
  if (!contentType.toLowerCase().startsWith("application/json")) {
    return { response: jsonResponse({ error: "content_type_required" }, 415) };
  }

  const declaredLength = Number(request.headers.get("content-length") ?? "0");
  if (Number.isFinite(declaredLength) && declaredLength > MAX_PAYLOAD_BYTES) {
    return { response: jsonResponse({ error: "payload_too_large" }, 413) };
  }

  const boundedBody = await readBoundedText(request, MAX_PAYLOAD_BYTES);
  if (!boundedBody.ok) return { response: jsonResponse({ error: "payload_too_large" }, 413) };

  try {
    return { body: JSON.parse(boundedBody.text) };
  } catch {
    return { response: jsonResponse({ error: "invalid_json" }, 400) };
  }
}

async function ingestAggregate(request, env) {
  const globalRateAllowed = await checkRateLimit(env.GLOBAL_RATE_LIMITER, "v1/aggregate");
  if (globalRateAllowed == null) return jsonResponse({ error: "service_unavailable" }, 503);
  if (!globalRateAllowed) return jsonResponse({ error: "rate_limited" }, 429);

  const requestBody = await readJsonRequest(request);
  if (requestBody.response) return requestBody.response;
  const validation = validateAggregateEvent(requestBody.body);
  if (!validation.ok) return jsonResponse({ error: validation.error }, 400);

  const event = validation.event;
  const hour = new Date(Math.floor(Date.now() / 1000) * 1000).toISOString().slice(0, 13);
  await env.DB.prepare(
    "INSERT INTO hourly_metrics (hour, event_kind, outcome, app_version, playback_version, demo_source, error_code, rounds_bucket, duration_bucket, event_count) " +
      "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1) " +
      "ON CONFLICT(hour, event_kind, outcome, app_version, playback_version, demo_source, error_code, rounds_bucket, duration_bucket) " +
      "DO UPDATE SET event_count = event_count + 1",
  ).bind(
    hour,
    event.kind,
    event.outcome,
    event.appVersion,
    event.playbackVersion,
    event.demoSource,
    event.errorCode,
    event.roundsBucket,
    event.durationBucket,
  ).run();

  return jsonResponse({ accepted: true }, 202);
}

async function ingestPresence(request, env) {
  const globalRateAllowed = await checkRateLimit(env.GLOBAL_RATE_LIMITER, "v1/presence");
  if (globalRateAllowed == null) return jsonResponse({ error: "service_unavailable" }, 503);
  if (!globalRateAllowed) return jsonResponse({ error: "rate_limited" }, 429);

  const requestBody = await readJsonRequest(request);
  if (requestBody.response) return requestBody.response;
  const validation = validatePresenceEvent(requestBody.body);
  if (!validation.ok) return jsonResponse({ error: validation.error }, 400);

  const event = validation.event;
  const installationRateAllowed = await checkRateLimit(env.INSTALLATION_RATE_LIMITER, event.dailyId);
  if (installationRateAllowed == null) return jsonResponse({ error: "service_unavailable" }, 503);
  if (!installationRateAllowed) return jsonResponse({ error: "rate_limited" }, 429);

  const now = Math.floor(Date.now() / 1000);
  const day = new Date(now * 1000).toISOString().slice(0, 10);
  await env.DB.batch([
    env.DB.prepare(
      "INSERT INTO active_installations (daily_id, app_version, playback_version, last_seen) VALUES (?, ?, ?, ?) " +
        "ON CONFLICT(daily_id) DO UPDATE SET app_version = excluded.app_version, playback_version = excluded.playback_version, last_seen = excluded.last_seen",
    ).bind(event.dailyId, event.appVersion, event.playbackVersion, now),
    env.DB.prepare(
      "INSERT INTO daily_installations (day, daily_id, app_version, playback_version, first_seen) VALUES (?, ?, ?, ?, ?) " +
        "ON CONFLICT(day, daily_id) DO NOTHING",
    ).bind(day, event.dailyId, event.appVersion, event.playbackVersion, now),
  ]);

  return jsonResponse({ accepted: true }, 202);
}

async function runRetention(env) {
  const now = Math.floor(Date.now() / 1000);
  const currentDay = new Date(now * 1000).toISOString().slice(0, 10);
  const dailyCutoff = new Date((now - 14 * 24 * 60 * 60) * 1000).toISOString().slice(0, 10);
  const hourlyCutoff = new Date((now - 90 * 24 * 60 * 60) * 1000).toISOString().slice(0, 13);
  const rollupCutoff = new Date((now - 365 * 24 * 60 * 60) * 1000).toISOString().slice(0, 10);

  await env.DB.batch([
    env.DB.prepare(
      "INSERT INTO daily_rollups (day, active_installations, analysis_success, analysis_failure, conversion_success, conversion_failure, updated_at) " +
        "SELECT days.day, COALESCE(d.active_installations, 0), " +
        "COALESCE(h.analysis_success, 0), COALESCE(h.analysis_failure, 0), " +
        "COALESCE(h.conversion_success, 0), COALESCE(h.conversion_failure, 0), ? " +
        "FROM (SELECT day FROM daily_installations WHERE day < ? UNION SELECT substr(hour, 1, 10) AS day FROM hourly_metrics WHERE substr(hour, 1, 10) < ?) days " +
        "LEFT JOIN (SELECT day, COUNT(*) AS active_installations FROM daily_installations GROUP BY day) d ON d.day = days.day " +
        "LEFT JOIN (SELECT substr(hour, 1, 10) AS day, " +
        "SUM(CASE WHEN event_kind = 'analysis' AND outcome = 'success' THEN event_count ELSE 0 END) AS analysis_success, " +
        "SUM(CASE WHEN event_kind = 'analysis' AND outcome = 'failure' THEN event_count ELSE 0 END) AS analysis_failure, " +
        "SUM(CASE WHEN event_kind = 'conversion' AND outcome = 'success' THEN event_count ELSE 0 END) AS conversion_success, " +
        "SUM(CASE WHEN event_kind = 'conversion' AND outcome = 'failure' THEN event_count ELSE 0 END) AS conversion_failure " +
        "FROM hourly_metrics GROUP BY substr(hour, 1, 10)) h ON h.day = days.day " +
        "ON CONFLICT(day) DO UPDATE SET active_installations = excluded.active_installations, analysis_success = excluded.analysis_success, " +
        "analysis_failure = excluded.analysis_failure, conversion_success = excluded.conversion_success, conversion_failure = excluded.conversion_failure, updated_at = excluded.updated_at",
    ).bind(now, currentDay, currentDay),
    env.DB.prepare("DELETE FROM active_installations WHERE last_seen < ?").bind(now - 24 * 60 * 60),
    env.DB.prepare("DELETE FROM daily_installations WHERE day < ?").bind(dailyCutoff),
    env.DB.prepare("DELETE FROM hourly_metrics WHERE hour < ?").bind(hourlyCutoff),
    env.DB.prepare("DELETE FROM daily_rollups WHERE day < ?").bind(rollupCutoff),
  ]);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/healthz") {
      return jsonResponse({ status: "ok", schemaVersion: TELEMETRY_SCHEMA_VERSION });
    }
    if (request.method === "POST" && url.pathname === "/v1/aggregate") {
      return ingestAggregate(request, env);
    }
    if (request.method === "POST" && url.pathname === "/v1/presence") {
      return ingestPresence(request, env);
    }
    return jsonResponse({ error: "not_found" }, 404);
  },

  async scheduled(_controller, env, context) {
    context.waitUntil(runRetention(env));
  },
};
