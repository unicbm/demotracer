# Telemetry operations

User-facing defaults and disclosures are in [Online behavior](ONLINE_SERVICES.md).
The fixed payload fields are defined in
[telemetry-contract.v1.json](../shared/contracts/telemetry-contract.v1.json);
the Worker rejects unknown fields.

| Channel | Default | Storage |
| --- | --- | --- |
| `/v1/aggregate` | On; one result after analysis/conversion | Hourly counters, no individual events or identifiers |
| `/v1/presence` | Opt-in; heartbeat every five minutes | Short active leases and UTC-day identifiers |

Presence seen within ten minutes counts as approximately online; distinct daily
identifiers estimate UTC-day activity. The client derives them from a local
random seed and never sends that seed. Reports are directional product metrics,
not authenticated billing or accounting data.

## Retention and access

| Data | Retention |
| --- | --- |
| Inactive presence leases | 24 hours |
| Daily presence rows | 14 days |
| Hourly aggregates | 90 days |
| Daily rollups | 365 days |

Both routes enforce a streaming 4 KiB body limit and per-location global rate
limit. Presence also limits each daily identifier. Limits do not use stored IPs;
the Worker does not read or persist IP/user-agent headers and observability is disabled.

Cloudflare account administrators can query D1. The maintained report prints
aggregates, never daily identifiers:

```powershell
./tooling/scripts/telemetry-report.ps1 -Days 30
```

It separates optional presence estimates from task counts and reports versions,
demo-source share, task volume and coarse errors.

## Deploy

Worker source, migrations, retention Cron and tests are in `cloudflare/telemetry`.
From that directory:

```powershell
pnpm install --frozen-lockfile
pnpm test
pnpm exec wrangler d1 migrations apply DB --remote
pnpm exec wrangler deploy
```

The [health endpoint](https://telemetry.detr.site/healthz) exposes service state
and schema version only.
