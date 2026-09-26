# DemoTracer BotHider technical notes

## Projects

- `src/`: Metamod native fake-client and persona runtime.
- `csharp/BotHiderApi/`: dependency-free `.NET 10` capability contract.
- `csharp/BotHiderImpl/`: CounterStrikeSharp `.NET 10` provider and sole
  presentation publisher.
- `configs/addons/BotHider/`: sanitized runtime defaults.

CounterStrikeSharp projects compile against `CounterStrikeSharp.API` 1.0.371.
The private native/C# C ABI is version 3; it keeps immutable
persona base name/SteamID fields separate from the effective values published
by lease-controlled publication calls.

`SlotPublisher` owns the native identity snapshot. `FakeClientManager` keeps
only ping simulation and a mutex-protected active-slot set, because entity
packing can query that set from a worker thread. Base SteamID selection runs
once before adoption. An absent roster preserves SteamID 0; native publication
accepts zero only for a zero-base incarnation, while the managed API rejects
explicitly requested zero SteamIDs.

## Capability

The provider registers `demotracer:bot-hider:v3` and exposes
`DemoTracerBotHiderApi.IBotHiderApi`.

The API intentionally separates native persona base state from temporary
consumer overrides. Consumers first query `TryGetManagedSlot` to obtain the
current `Incarnation`, then acquire or replace an array of
`BotHiderPresentationOverride` entries.

The provider validates the complete array before changing ownership. A failed
entry changes nothing. Slot reuse changes the incarnation and removes that
slot from its lease, preserving surviving participants. SteamID values are exact: duplicate or unresolved
live-slot conflicts reject the batch, and native publication never substitutes
another persona identity.

## Ownership and restore

The native and managed halves communicate synchronously through a private C
ABI. Session, slot incarnation, and complete controller handles fence writes.
The managed provider registers one native change callback and unregisters it
on unload; native shutdown clears it. Notifications queue reconciliation on the
server thread and do not perform entity writes inside native adoption hooks.
Slot notifications and player lifecycle events use a 64-bit dirty-slot mask;
map, round, and native-session boundaries still reconcile all managed slots.
Takeover events queue both controllers and retain their complete handles so a
subsequent human death can still reconcile the original bot after the engine
clears its relationship field. Map/round teardown clears those associations.
Ping notifications carry the changed slot and coalesce separately. Their writer
uses the same live session, incarnation, controller handle, and NetChannel
checks as crosshair publication, then updates only non-networked `m_iPing`.
They do not rebuild presentation objects, submit userinfo, or notify crosshair.

Consumers supply a `CancellationToken` on acquisition and cancel it on the
server thread when unloading. Replacement preserves that owner registration;
explicit release, the last participant leaving, and map/provider teardown
unregister it. Neither lease expiry scans nor keepalive calls are needed.
Consumers subscribe to `ProviderChanged` in the shared API assembly and
unsubscribe on unload to handle provider replacement without periodic probes.

The effective presentation for each field is:

```text
active exact lease override ?? current native persona base
```

Because release recomputes from current base state, a persona refresh that
happens while DTR is active is not overwritten by stale saved values.
Clan is the exception: native personas do not own a clan, so the managed
provider captures its controller base as one tag/group-ID pair, per controller
incarnation. The nullable API v3 pair restores that base on release; an empty
pair explicitly clears it. Failed writes or notifications retain the saved
base and pending status for rollback/retry. Teardown restores only the exact
previous controller handle and user ID, never a replacement player.
The publisher also compares effective lease values with live controller fields
at actual lifecycle/change events and schedules reconciliation after spawn/death. This
prevents engine lifecycle writes from exposing the persona base while a lease
is active.

Native loading after server startup is unsupported. Unload refuses while the
active-slot set is nonempty, before removing hooks or clearing presentation
state. Deploy native updates with a full server restart. The remaining empty
runtime unload path drains callbacks and clears schema state.

Entity packing gathers and deduplicates all managed pawn handles into one
fixed 64-entry buffer before writing any flag. It then compacts the modified
entries for scope restoration, retaining the complete-handle checks and
restoring only `FL_BOT` without publishing the transient server-side bit.

## Build

Windows native prerequisites are `HL2SDKCS2`, `MMSOURCE_DEV`, protoc 3.21.x,
CMake, and Visual Studio Build Tools. `CSGO_PROTO` is optional when
`HL2SDKCS2/common/network_connection.proto` exists.

```powershell
cmake -S server\runtime\BotHider -B server\runtime\BotHider\build -G "Visual Studio 18 2026" -A x64
cmake --build server\runtime\BotHider\build --config Release --target BotHider
dotnet build server\runtime\BotHider\csharp\BotHiderImpl\BotHiderImpl.csproj -c Release
```

The server package script consumes the native package under
`server/runtime/BotHider/build/package` and the `.NET 10` C# outputs.
Use the repository's `tooling/scripts/package-server.ps1` for the matched
bundle. The obsolete standalone `build.ps1` distribution layout is removed.
