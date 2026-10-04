# Online Behavior and Privacy

Demo analysis, conversion, validation and playback run locally. DemoTracer
does not upload demos, manifests, replays, voice sidecars, local paths or logs.

| Trigger | Destination | Data sent |
| --- | --- | --- |
| Analysis/conversion completes with **Anonymous aggregate statistics** enabled (default) | `telemetry.detr.site/v1/aggregate` | Versions, result/coarse error category, duration/round-count buckets, locally classified demo source; no identifier |
| **Active-user estimates** explicitly enabled | `telemetry.detr.site/v1/presence` | Versions and a daily rotating random identifier |
| Startup, selected CS2 folder changes, or update check | `releases.detr.site/channels/stable/latest.json` | Versions, updater target/architecture and request metadata |
| A roster is visible | `steamcommunity.com/profiles/<steamid>` (XML and profile page) | SteamID64 and request metadata |
| About/credits opened | `avatars.githubusercontent.com` | Public avatar identifier and request metadata |
| Cosmetic image or optional 3D preview opened | `cdn.cstrike.app`, `3d.cstrike.app` | Image key or cosmetic render parameters and request metadata |
| **Add selected batch** confirmed | `inventory.cstrike.app/api/action/resync` and `/api/action/sync` | Selected catalog IDs and supported appearance fields |
| External link opened | System browser | Destination's normal browser requests |

## Profiles and Inventory Simulator

Steam profile enhancement is automatic, cached for 24 hours and optional to
successful local processing. XML/profile pages supply names, static or animated
avatars and frames. Failed requests do not block analysis or conversion.

**Add selected batch** opens Inventory Simulator in its own WebView2 panel.
Steam sign-in and site data stay there; DemoTracer does not extract session
cookies, user IDs or API keys. The official same-origin `resync` response stays
inside that WebView and is used to reject duplicate items before one batch
`sync` request. A version conflict allows one resync and retry.

Items are replicas: catalog IDs, supported wear/seed/name, stickers, keychains
and patches may be sent. Owner/account IDs, original item IDs, exact StatTrak
counters and unsupported sticker scale are omitted.

## Updates

The app displays versions and localized notes before the user chooses to install.
The Tauri updater verifies the GUI package signature before NSIS installation.
Playback downloads are restricted to the release origin and checked by SHA-256,
minisign, receipt and per-file hashes. Local ZIP installation uses the receipt
and file checks. CS2 must be closed. Failed update checks do not block local work.

## Telemetry

Both channels can be disabled in Settings. Aggregate results become hourly
counters without retained individual events. Only optional presence creates a
local seed; it sends a UTC-day-derived identifier, never the seed. Disabling
presence deletes the seed.

Neither payload contains filenames, paths, raw server names/addresses, SteamIDs,
account data, logs, voice, user agents or persistent device identifiers.
Cloudflare processes ordinary HTTPS metadata; the Worker does not read or store
IP addresses or user-agent headers. See [Telemetry](TELEMETRY.md) for the
contract, retention and operations.
