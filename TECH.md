# DemoTracer BotHider technical notes

## Projects

- `src/`: Metamod native fake-client and persona runtime.
- `csharp/BotHiderApi/`: dependency-free `.NET 10` capability contract.
- `csharp/BotHiderImpl/`: CounterStrikeSharp `.NET 10` provider and sole
  presentation publisher.
- `configs/addons/BotHider/`: sanitized runtime defaults.

CounterStrikeSharp projects compile against `CounterStrikeSharp.API` 1.0.371.
The private native/C# shared-memory contract is version 2; it keeps immutable
persona base name/SteamID fields separate from the effective values published
by lease-controlled native commands.

## Capability

The provider registers `demotracer:bot-hider:v1` and exposes
`DemoTracerBotHiderApi.IBotHiderApi`.

The API intentionally separates native persona base state from temporary
consumer overrides. Consumers first query `TryGetManagedSlot` to obtain the
current `Incarnation`, then acquire or replace an array of
`BotHiderPresentationOverride` entries.

The provider validates the complete array before changing ownership. A failed
entry changes nothing. Slot reuse changes the incarnation and revokes the full
lease containing that slot. SteamID values are exact: duplicate or unresolved
live-slot conflicts reject the batch, and native publication never substitutes
another persona identity.

## Ownership and restore

The shared memory mapping `CS2BotHider_Slots` is private transport between the
native and C# halves. It is not a public DemoTracer integration ABI.

The effective presentation for each field is:

```text
active exact lease override ?? current native persona base
```

Because release recomputes from current base state, a persona refresh that
happens while DTR is active is not overwritten by stale saved values.
The publisher also compares effective lease values with live controller fields
on every pass and schedules immediate reconciliation after spawn/death. This
prevents engine lifecycle writes from exposing the persona base while a lease
is active.

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
