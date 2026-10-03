# dtr-hider

[`unicbm/demotracer`](https://github.com/unicbm/demotracer/tree/main/server/runtime/dtr-hider) maintains the
runtime shipped with CS2 DemoTracer. It combines the `dtr-hider` Metamod plugin
with its matched `DtrHider.dll` CounterStrikeSharp presentation provider and
`DtrHiderApi` shared assembly. Native exports use `DtrHider_` and the capability
is `dtr-hider:api:v3`.
The bundle contains all three parts. No separately installed upstream BotHider
is required. See [UPSTREAM.md](UPSTREAM.md) for attribution and maintained differences.

Function and virtual hooks use Metamod's shared KHook engine. Build and run
against the source pins in `server/runtime/common/contracts/hook-runtime.v1.json`.

## Standalone checks

Build from the DemoTracer working tree. Shared infrastructure is consumed
directly from `server/runtime/common`.

With PowerShell 7, CMake, a C++20 compiler, and .NET 10 installed:

```powershell
./tools/check.ps1
./tools/check.ps1 -Dotnet /path/to/dotnet -NativeBuild
```

The default check builds the provider and runs Release native tests without a
CS2 SDK. `-NativeBuild` additionally builds the plugin with the SDK environment
described in [TECH.md](TECH.md). For a local shared-code checkout, use
`-CommonDirectory /path/to/common`; direct CMake builds accept
`-DDTR_COMMON_DIR=...`.
The check script stages native installation files in `.build/native/package/`.

The native layer owns fake-client adoption, synthetic persona state, ping, and
a synchronous, main-thread C ABI (native ABI 3). The C# layer is the only publisher for visible
name, SteamID64, clan tag/group ID, ping, scoreboard flair, and server-replicated crosshair state.
It never assigns teams or respawns bots. Ordinary bots follow the engine's
round lifecycle; DemoTracer prepares and respawns only its own replay roster.

Consumers use `dtr-hider:api:v3` for presentation overrides.

## Presentation leases

Temporary DTR presentation uses an all-or-none ownership lease. Success
requires native userinfo and the requested controller fields to be applied
and read back before returning; it does not acknowledge delivery to every
client or promise simultaneous rendering across slots. Failed requests
restore the previous lease or current base presentation.

Lease rules:

- each request carries the provider-issued slot incarnation;
- one lease owns a slot at a time;
- replacement and release require the exact opaque lease token;
- acquisition requires a cancellable owner lifetime; the consumer cancels it
  on unload, synchronously releasing ownership without a heartbeat or timeout;
- all API operations, including owner cancellation, run on the server thread;
- provider reload and map change revoke leases;
- disconnect, loss of managed state, and slot reuse remove only the affected
  slot; surviving slots retain their identity and the existing lease token;
- release restores the current persona base, not a stale saved copy;
- an active lease is reconciled against both native client state and controller
  fields after spawn/death, round events, and native presentation changes;
- exact SteamID conflicts fail the whole batch instead of selecting another
  persona.

See [TECH.md](TECH.md) for native publication and restoration details.

## Runtime commands

- `dtr_hider_status`: provider, hook, managed-slot, incarnation, and lease status.
- `dtr_hider_disguise <0|1>`: global native disguise toggle.
- `dtr_hider_namesource <0|1>`: choose engine bot names or `bot_info.json` names for
  newly adopted personas.

Each configured persona can be assigned to one bot at a time. When the roster is
exhausted, additional bots keep their engine name and unconfigured identity.

The bundle ships `bot_info.example.json` and never overwrites a server-local
`bot_info.json`. Copy and customize the example only when explicit persona base
data is wanted. Without it, the base keeps the engine bot name and SteamID 0;
name, clan, crosshair, and other presentation fields remain usable. A lease may
temporarily supply an exact nonzero SteamID and release back to that zero base.
Explicit lease requests for SteamID 0 are rejected. Configured persona SteamID
collisions are resolved before adoption, so the base and initial published
identity agree and release does not undo that choice.

`dtr_hider_disguise` may rebuild bots. It preserves the existing `bot_quota` value and
`bot_quota_mode`, including a quota of zero; it does not infer a fill-mode quota
from the number of humans and bots.

## Co-installation

Install or update the native runtime with a full server restart. Native late
loading is rejected, and native unload is rejected while managed bots remain
connected, before any hook is removed. Disguised bots depend on those hooks;
clearing the plugin's slot table cannot safely undo their engine state. Managed
provider reload still releases its leases and can reconnect to the loaded
native runtime.

The managed provider installs as `DtrHider/DtrHider.dll`. Replace the
previous `DemoTracerBotHider` directory during migration. Do not run an upstream
provider beside this matched native/C# provider. Multiple publishers can
overwrite the same controller presentation fields.

The Panel Profiles toggle is not mapped to `dtr_hider_disguise`: this fork's native
disguise switch may rebuild bots. Keep Profiles enabled when using both components.

## Upstream and license

See [UPSTREAM.md](UPSTREAM.md) for the imported baseline and update policy.
Original attribution and AGPL-3.0-only license files are preserved here.
