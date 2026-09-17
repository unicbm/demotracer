# DemoTracer BotHider runtime

This directory contains the BotHider runtime maintained and shipped as part of
CS2 DemoTracer. It combines a Metamod plugin with a CounterStrikeSharp
presentation provider.

The native layer owns fake-client adoption, synthetic persona state, ping, and
a synchronous, main-thread C ABI (native ABI 3). The C# layer is the only publisher for visible
name, SteamID64, ping, scoreboard flair, and server-replicated crosshair state.
It never assigns teams or respawns bots. Ordinary bots follow the engine's
round lifecycle; DemoTracer prepares and respawns only its own replay roster.

DemoTracer consumes the versioned `demotracer:bot-hider:v2` capability. It does
not read shared-memory offsets, invoke `bh_setname`/`bh_setsid`, or write these
presentation fields directly.

## Presentation leases

Temporary DTR presentation uses an all-or-none ownership lease. Success
requires native userinfo and the requested controller fields to be applied
and read back before returning; it does not acknowledge delivery to every
client or promise simultaneous rendering across slots. Failed requests
restore the previous lease or current base presentation.

The native API checks its own live session and slot incarnation at each
identity write. There is no shared-memory queue or cross-process mapping.
Native reload revokes leases and an unloaded provider reports disconnected.
Name and SteamID changes share one userinfo publication; unchanged identities
do not force another publication. Native adoption/release and managed
spawn/death/round events coalesce into one next-frame reconcile. Ping changes
queue only the affected slots and publish only their ping through a guarded
native field write; they never trigger identity or crosshair reconciliation.
There is no periodic presentation sweep. Provider lifecycle notifications
invalidate consumers' cached references without availability polling.
Crosshair changes use the native controller field notification, guarded by
session, slot incarnation, and the complete controller entity handle. Failed
notifications remain pending for retry even when the string already matches;
an explicit crosshair lease cannot succeed while its notification is pending.
The managed provider and native ABI 3 runtime must be installed together.

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

DemoTracer retains the most recent successfully loaded DTR presentation batch
independently of native replay buffers. Playback handoff, replay finish,
sequence completion, later server rounds, and match end release control only.
A later successful DTR batch atomically replaces it; a failed partial load keeps
the previous complete batch. Explicit slot unload/kick, disconnect, map change,
slot reuse, plugin unload, or provider loss end the affected presentation.

Crosshair publication writes and verifies
`CCSPlayerController.m_szCrosshairCodes`, then submits its native controller
network-state notification using the live field offset. This does not depend
on the generic managed schema networked-field flag. A requested override
requires both readback and successful notification submission. Publication
occurs once for a new slot incarnation
or presentation lease and again only when the engine actually changes the
stored value. The path is server-only and requires no client-side injection or
fragile `client.dll` signature hook.

## Runtime commands

- `bh_status`: provider, hook, managed-slot, incarnation, and lease status.
- `bh_disguise <0|1>`: global native disguise toggle.
- `bh_namesource <0|1>`: choose engine bot names or `bot_info.json` names for
  newly adopted personas.

The bundle ships `bot_info.example.json` and never overwrites a server-local
`bot_info.json`. Copy and customize the example only when explicit persona base
data is wanted; otherwise the native fallback remains available.

Raw per-slot mutation commands are intentionally not exposed. DTR overrides
must use the presentation lease API.

## Co-installation

The maintained provider installs as `BotHiderImpl/BotHiderImpl.dll` for Panel
file detection; its capability is `demotracer:bot-hider:v2`. Replace the
previous `DemoTracerBotHider` directory during migration. Do not run an upstream
provider beside this matched native/C# provider. Multiple publishers can
overwrite the same controller presentation fields.

The Panel Profiles toggle is not mapped to `bh_disguise`: this fork's native
disguise switch may rebuild bots. Keep Profiles enabled during combined testing.

## Upstream and license

See [UPSTREAM.md](UPSTREAM.md) for the imported baseline and update policy.
Original attribution and AGPL-3.0-only license files are preserved here.
