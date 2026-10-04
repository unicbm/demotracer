# dot-hider architecture

## Projects

- `src/`: Metamod native fake-client and persona runtime.
- `csharp/DtrHiderApi/`: dependency-free `.NET 10` capability contract.
- `csharp/DtrHider/`: CounterStrikeSharp `.NET 10` provider and sole
  presentation publisher.
- `configs/addons/dot-hider/`: sanitized runtime defaults.

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

The provider registers `dtr-hider:api:v3` and exposes
`DtrHiderApi.IDtrHiderApi`.

Consumers first query `TryGetManagedSlot` to obtain the
current `Incarnation`, then acquire or replace an array of
`BotHiderPresentationOverride` entries.

The provider validates the complete array before changing ownership. A failed
entry changes nothing. Slot reuse changes the incarnation and removes that
slot from its lease, preserving surviving participants. SteamID values are exact: duplicate or unresolved
live-slot conflicts reject the batch, and native publication never substitutes
another persona identity.

## Presentation ownership

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
Ping notifications coalesce separately and update only non-networked `m_iPing`,
after checking the session, incarnation, controller handle, and NetChannel.
Crosshair publication requires field readback and a successful native network
notification; failed notifications remain pending even if the field already matches.

Consumers supply a `CancellationToken` on acquisition and cancel it on the
server thread when unloading. Replacement preserves that owner registration;
explicit release, the last participant leaving, and map/provider teardown
unregister it.
Consumers subscribe to `ProviderChanged` in the shared API assembly and
unsubscribe on unload to handle provider replacement without periodic probes.

Release and owner cancellation remove the lease without writing to controllers.
Names and SteamIDs remain in native published state. Fields absent from the next
request keep their current values; an explicit empty crosshair or clan clears it.
Clan publication retains only notification status, not an original-field snapshot.
Disconnect, controller replacement, map and native-session changes discard
slot state without accessing the expired controller.
Spawn, death, and presentation changes reconcile live fields with active leases.

Native loading after server startup is unsupported. Unload refuses while the
active-slot set is nonempty, before removing hooks or clearing presentation
state. Deploy native updates with a full server restart. The remaining empty
runtime unload path drains callbacks and clears schema state.

Kick commands temporarily change server-side identity without broadcasting it;
survivors need no userinfo publication when that temporary change is reversed.
Other Windows userinfo updates use the gamedata virtual slot, not the SDK's
compiled position, which can drift into the adjacent `FillServerInfo` method.

Entity packing gathers and deduplicates all managed pawn handles into one
fixed 64-entry buffer before writing any flag. It then compacts the modified
entries for scope restoration, retaining the complete-handle checks and
restoring only `FL_BOT` without publishing the transient server-side bit.

## Build

Windows native prerequisites are `HL2SDKCS2`, `MMSOURCE_DEV`, protoc 3.21.x,
CMake, and Visual Studio Build Tools. `CSGO_PROTO` is optional when
`HL2SDKCS2/common/network_connection.proto` exists.

```powershell
git submodule update --init --recursive
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release --target dot-hider
dotnet build csharp/DtrHider/DtrHider.csproj -c Release
```

The native package is staged under `build/package/addons`; managed assemblies
are under `csharp/DtrHider/bin/Release/net10.0`. Install the matched native
runtime and provider together. The product packager assembles the playback bundle.
