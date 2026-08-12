# Anonymous Telemetry

DemoTracer 1.1.2 separates telemetry into two independent channels with
different defaults and data contracts. Neither channel changes local features.
Both can be changed at any time in Settings.

The canonical machine-readable contract is
[`shared/contracts/telemetry-contract.v1.json`](../shared/contracts/telemetry-contract.v1.json).
The desktop sends only its fixed fields to `telemetry.detr.site`; the Worker
rejects the entire request if it contains an unknown field.

## Anonymous aggregate statistics

This channel is enabled by default and can be turned off in Settings. It sends
one bounded task result after an analysis or conversion finishes:

- App and installed playback versions.
- Success or failure with a fixed coarse error category.
- Coarse duration and round-count buckets.
- A locally classified Demo source such as 5E, Perfect World, FACEIT, Valve
  Premier, Matchmaking, or a known tournament/platform category. Unknown
  labels become `other`; no original server or filename text is sent.

The aggregate payload has no event ID, daily ID, local seed, installation ID,
account ID, or other field that can join two submissions. The Worker writes the
result directly into an hourly aggregate counter and does not store the
request as an individual event.

## Optional active-user estimates

This channel is disabled until the user explicitly enables it through the
lightweight in-app notice or Settings. While enabled, the app sends a heartbeat
every five minutes. The admin report treats a heartbeat seen within ten minutes
as approximately online and uses distinct daily identifiers for a directional
UTC-day active estimate.

The desktop creates a local random seed only after this channel is enabled. It
hashes that seed with the UTC day and sends only the result. The result changes
daily and cannot be linked across days by the service. The seed itself never
leaves the computer and is deleted when the channel is disabled.

## Excluded data and Cloudflare boundary

Neither payload contains Demo/replay content, a file name, a file or Steam path,
a raw server name or address, SteamID, account data, log text, user agent, voice
data, or a persistent device identifier. Cloudflare necessarily processes
ordinary network metadata to terminate HTTPS and protect the service, but this
Worker does not read or persist IP addresses or user-agent headers and Worker
observability is disabled.

## Storage and access

The aggregate channel stores only hourly counters. The optional presence
channel stores short active leases and UTC daily installation rows. Inactive
lease rows expire after 24 hours, daily installation rows after 14 days, hourly
aggregates after 90 days, and daily rollups after 365 days. No raw task-event
table or receipt identifier is retained.

Both ingest routes enforce a streaming 4 KiB body limit and a per-location
global request limit. The presence route also limits each daily identifier.
These limits do not read or store an IP address. Because a public desktop client
cannot hold an unextractable service credential, the figures are directional
product metrics rather than an authenticated billing or accounting source.

Cloudflare account administrators can query the D1 database. The maintained
report returns aggregates and never prints daily identifiers:

```powershell
.\tooling\scripts\telemetry-report.ps1
.\tooling\scripts\telemetry-report.ps1 -Days 30
```

The report labels optional-presence estimates separately from default aggregate
task data. It includes approximately-online installations, UTC-today active
installations, version distribution, locally classified Demo-source share,
task volume, and top coarse failure categories.

## Cloudflare operations

The Worker, D1 migrations, retention Cron, and tests live under
`cloudflare/telemetry`. From that directory:

```powershell
pnpm install --frozen-lockfile
pnpm test
pnpm exec wrangler d1 migrations apply DB --remote
pnpm exec wrangler deploy
```

The public health check exposes only service state and the schema version:
`https://telemetry.detr.site/healthz`.
