# DemoTracer BotHider runtime

This directory contains the BotHider runtime maintained and shipped as part of
CS2 DemoTracer. It combines a Metamod plugin with a CounterStrikeSharp
presentation provider.

The native layer owns fake-client adoption, synthetic persona state, ping, and
the shared native/C# transport. The C# layer is the only publisher for visible
name, SteamID64, ping, scoreboard flair, and server-replicated crosshair state.

DemoTracer consumes the versioned `demotracer:bot-hider:v1` capability. It does
not read shared-memory offsets, invoke `bh_setname`/`bh_setsid`, or write these
presentation fields directly.

## Presentation leases

Temporary DTR presentation is applied as an all-or-none batch lease:

- each request carries the provider-issued slot incarnation;
- one lease owns a slot at a time;
- replacement and release require the exact opaque lease token;
- leases expire when their heartbeat is absent for four seconds;
- provider reload, map change, disconnect, or slot reuse revokes stale leases;
- release restores the current persona base, not a stale saved copy;
- an active lease is reconciled against both native client state and controller
  fields after spawn/death and during periodic publication;
- exact SteamID conflicts fail the whole batch instead of selecting another
  persona.

DemoTracer retains the most recent successfully loaded DTR presentation batch
independently of native replay buffers. Playback handoff, replay finish,
sequence completion, later server rounds, and match end release control only.
A later successful DTR batch atomically replaces it; a failed partial load keeps
the previous complete batch. Explicit slot unload/kick, disconnect, map change,
slot reuse, plugin unload, or provider loss end the affected presentation.

Crosshair publication uses
`CCSPlayerController.m_szCrosshairCodes` plus CounterStrikeSharp state-change
replication. The path is server-only and requires no client-side injection.

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

Do not run a separately installed public `BotHiderImpl` CounterStrikeSharp
plugin beside `DemoTracerBotHider`. Two presentation publishers can overwrite
each other even when they share the same native BotHider mapping.

## Upstream and license

See [UPSTREAM.md](UPSTREAM.md) for the imported baseline and update policy.
Original attribution and AGPL-3.0-only license files are preserved here.
