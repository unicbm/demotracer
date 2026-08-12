# Online Behavior and Privacy

Demo selection, parsing, conversion, validation, and playback are local. The
application does not upload demos, manifests, replay files, voice sidecars,
local paths, or logs.

| Trigger | Request | Data sent |
| --- | --- | --- |
| An analysis or conversion finishes while default-on **Anonymous aggregate statistics** remain enabled | `telemetry.detr.site/v1/aggregate` | App/playback versions, success/failure and a fixed coarse error category, duration/round-count buckets, and a locally classified Demo source; no event, daily, or installation identifier is created or sent |
| The user explicitly enables **Active-user estimates**, then while the app is open | `telemetry.detr.site/v1/presence` | App/playback versions and a daily rotating random identifier; ordinary HTTPS connection metadata is handled by Cloudflare but is not read or stored by the Worker |
| The desktop app starts, the CS2 folder changes, or the user clicks **Check for updates** | `releases.detr.site/channels/stable/latest.json` | Current app and installed playback versions, Windows updater target/architecture, and normal request metadata |
| A roster is visible | `steamcommunity.com/profiles/<steamid>?xml=1` and the public profile page | SteamID64 and normal request metadata |
| **About & credits** is opened | `avatars.githubusercontent.com` | Public GitHub avatar identifier and normal request metadata |
| A cosmetic image is opened | `cdn.cstrike.app` | Catalog image key and normal request metadata |
| The optional 3D preview is opened | `3d.cstrike.app` | Cosmetic render parameters and normal request metadata |
| The user confirms **Add selected batch** | `inventory.cstrike.app/api/action/resync` and `/api/action/sync` in a collapsible WebView2 side panel | The selected cosmetics' catalog IDs and supported demo-backed wear, seed, name, sticker, and keychain fields |
| An external link is opened | System browser | Normal browser request metadata under the destination's policy |

Steam profile enhancement is automatic, best-effort, and cached for 24 hours.
The XML response supplies identity and a full-size static avatar fallback; the
public profile page may supply an official animated avatar and a separate
profile frame. Failure is silent and never blocks parsing, conversion, or
validation. Cosmetic requests occur only
for separately enabled/exported evidence and user-opened previews.

GitHub avatars are requested only while the user-visible credits board is open.
They identify the already public GitHub accounts named on that page. An offline
or failed request falls back to a local initial and does not affect the app.

The Inventory Simulator integration is user-initiated. Clicking **Add selected
batch** is the confirmation that starts the operation. DemoTracer opens a
resizable side panel on the official site and shows a compact progress indicator.
If needed, Steam sign-in happens only inside that window and the batch resumes
afterward. The window keeps its own normal WebView2 site data so the official
session can be reused, but DemoTracer does not read, export, or store the
session cookie, user ID, or an API key.

Immediately before submission, the official same-origin `resync` route supplies
the current inventory version and inventory document. The document remains
inside the official-origin WebView and is used only to reject duplicates. The
comparison includes item ID, seed, wear, StatTrak state, custom name, stickers,
keychains, and patches while intentionally ignoring inventory-only state such
as equip flags and timestamps. It also detects duplicates within the selected
batch and inside storage units.

New `add` actions are submitted in one request and processed in order by the
official sync route. A concurrent version conflict triggers one fresh resync,
another duplicate check, and one retry. On success the window loads the
refreshed official inventory; its compact completion indicator disappears
automatically. This avoids racing several stale browser tabs.

Each added entry is a replica. DemoTracer omits owner/account identifiers, the
original item ID, exact StatTrak counters, and unsupported sticker-scale evidence.
Compatible weapon and knife custom names are preserved; other names are omitted
without rejecting the item.

The desktop app checks the signed stable GUI release manifest when it starts.
The check is best-effort, does not upload local paths or application data, and
does not interrupt local work when the release service is offline. When a newer
version exists, the app shows the current version, latest version, and localized
release notes before the user chooses whether to download and install it. The
Tauri updater verifies the package signature before the passive NSIS install.

The app also checks the playback entry in that signed stable manifest after a
CS2 installation is selected. A newer CSS bundle is shown before the user
chooses whether to download and install it. The download URL is restricted to
the immutable release origin, and the package is verified with SHA-256 and a
minisign signature before the existing receipt and per-file validation runs.
CS2 must be closed before installation. A manually selected local CSS ZIP
remains supported and goes through the same receipt and per-file validation.

DemoTracer 1.1.2 adds two separate telemetry channels. Anonymous aggregate task
statistics are enabled by default and can be disabled in Settings. Their strict
payload contains no event, daily, installation, or account identifier, and the
Worker writes each result directly into an hourly counter instead of retaining
an individual event. Demo sources such as 5E, Perfect World, FACEIT, and
official matchmaking are classified locally; unrecognized values become
`other` without the original text.

Active-user estimates remain off until explicitly enabled from a lightweight
notice or Settings. Only this channel creates a local random seed and sends a
derived identifier. The identifier changes every UTC day, the seed is never
uploaded, and disabling the channel deletes the local seed. Neither channel
accepts Demo or replay content, file names, paths, raw server names or
addresses, SteamIDs, account data, logs, voice, user agents, or persistent
installation identifiers. See [Anonymous Telemetry](TELEMETRY.md) for the exact
contracts, retention, and administrator queries.

There is still no cloud conversion, replay upload, account system, or remote
player catalog.
