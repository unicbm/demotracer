# Anonymous Telemetry

DemoTracer 1.1.2 contains a small, optional telemetry channel for product
health and compatibility planning. It is disabled by default. The first-run
dialog offers equally available allow and decline actions, and the choice can
be changed at any time in Settings. Declining never disables local features.

The canonical machine-readable contract is
[`shared/contracts/telemetry-contract.v1.json`](../shared/contracts/telemetry-contract.v1.json).
The desktop sends only its fixed fields to `telemetry.detr.site`; the Worker
rejects the entire request if it contains an unknown field.

## What is measured

- An approximate active-installation lease while an opted-in app is open. A
  heartbeat is sent every five minutes and the admin report uses a ten-minute
  active window.
- Analysis and conversion success or failure, with a fixed coarse error category
  and coarse duration and round-count buckets.
- A locally classified Demo source such as 5E, Perfect World, FACEIT, Valve
  Premier, Matchmaking, or a known tournament/platform category. Unknown
  labels become `other`; no original server or filename text is sent.
- App and installed playback versions.

No Demo/replay content, file name, file or Steam path, raw server name or IP,
SteamID, account data, log text, user agent, voice data, or persistent device
identifier is in the payload or D1 schema. Cloudflare necessarily processes
ordinary network metadata to terminate HTTPS and protect the service, but this
Worker does not read or persist IP addresses or user-agent headers and Worker
observability is disabled.

The desktop creates a local random seed only after telemetry is enabled. It
hashes that seed with the UTC day and sends only the result. The result changes
daily and cannot be linked across days by the service. The seed itself never
leaves the computer.

## Storage and access

The Worker stores short active leases and UTC daily installation rows, plus
hourly aggregate counters. It does not store raw event payloads. Random event
receipts expire after 48 hours, inactive lease rows after 24 hours, daily
installation rows after 14 days, hourly aggregates after 90 days, and daily
rollups after 365 days.

The ingest route enforces a streaming 4 KiB body limit plus per-location global
and per-daily-ID request limits before writing D1. These limits do not use or
store an IP address. Because a public anonymous desktop client cannot hold an
unextractable service credential, these figures are directional product
metrics rather than an authenticated billing or accounting source.

Cloudflare account administrators can query the D1 database. The maintained
report intentionally returns only aggregates and never prints daily IDs or
event receipts:

```powershell
.\tooling\scripts\telemetry-report.ps1
.\tooling\scripts\telemetry-report.ps1 -Days 30
```

The report includes approximately-online installations, UTC-today active
installations, version distribution, locally classified Demo-source share,
task volume, and top coarse failure categories.

## Cloudflare operations

The Worker, D1 migration, retention Cron, and tests live under
`cloudflare/telemetry`. From that directory:

```powershell
pnpm install --frozen-lockfile
pnpm test
pnpm exec wrangler d1 migrations apply DB --remote
pnpm exec wrangler deploy
```

The public health check exposes only service state and the schema version:
`https://telemetry.detr.site/healthz`.
