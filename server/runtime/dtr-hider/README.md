# dtr-hider

Bot identity and presentation provider for CS2 DemoTracer:

| Part | Purpose |
| --- | --- |
| `src/` → `dtr-hider.dll` | Native fake-client adoption, persona state and ping |
| `csharp/DtrHider/` → `DtrHider.dll` | CounterStrikeSharp presentation publisher |
| `csharp/DtrHiderApi/` → `DtrHiderApi.dll` | Shared capability `dtr-hider:api:v3` |

Install all three from the matched Playback bundle and restart the server.
Do not run another BotHider provider alongside them. Native late loading is
unsupported; native unload is refused while managed bots remain connected.
Managed provider reload releases leases and can reconnect to the native runtime.

## Build

With .NET 10, PowerShell 7, CMake and a C++20 compiler, run from this directory:

```powershell
./tools/check.ps1
./tools/check.ps1 -NativeBuild
```

The default command builds the managed provider and runs Release native tests.
`-NativeBuild` also needs the [server SDK environment](../../README.md#shared-hook-runtime)
and stages native files in `.build/native/package/`.
Use `-Dotnet <path>` to select a .NET executable.

## Presentation API

The provider publishes name, SteamID64, clan tag/group ID, ping, scoreboard
flair and crosshair. It does not assign teams or respawn bots.

Query `TryGetManagedSlot` for the current incarnation, then acquire or replace
`BotHiderPresentationOverride` entries:

- Acquisition validates the entire batch, applies requested native/controller
  fields and verifies readback before succeeding. This confirms server state, not client delivery.
- Each slot has one owner. Replace/release requires the exact opaque lease token;
  failed replacement preserves the previous lease.
- Supply a cancellable owner lifetime; cancel it on the server thread at unload.
  All API operations run on that thread. There is no renewal heartbeat.
- Map/provider teardown revokes leases. Disconnect or slot reuse removes only
  that participant; surviving slots keep their lease.
- Release removes ownership without restoring presentation. Omitted fields keep
  their values; explicit empty clan/crosshair values clear them.
- Explicit SteamID 0 and conflicting nonzero SteamIDs reject the batch.
- Subscribe to `ProviderChanged` for provider replacement and unsubscribe at unload.

Native/C# communication uses synchronous ABI 3. Session, incarnation and full
controller handles identify writes. Native notifications queue server-thread
reconciliation; hooks do not write controller fields during adoption.
Spawn, death, round and presentation events reconcile active leases.
Crosshair changes require readback and native network notification.

## Runtime commands and personas

| Command | Purpose |
| --- | --- |
| `dtr_hider_status` | Provider, hooks, managed slots, incarnations and leases |
| `dtr_hider_disguise <0|1>` | Toggle native disguise; may rebuild bots |
| `dtr_hider_namesource <0|1>` | Use engine names or configured persona names for new adoptions |

The disguise toggle preserves `bot_quota` and `bot_quota_mode`, including zero.
It is separate from the Bot Improver Panel's Profiles toggle.

The bundle ships `bot_info.example.json` and preserves server-local `bot_info.json`.
Configured personas are exclusive to one bot each. Without an available persona,
bots keep their engine name and SteamID 0; other presentation fields still work.
A lease can supply an exact nonzero SteamID, which remains after release.

## Credits and license

AGPL-3.0-only. Original attribution, imported baseline and maintenance boundaries
are preserved in [UPSTREAM.md](UPSTREAM.md) and [LICENSE](LICENSE).
