# Playback server

This area contains everything installed on a local CS2 server for `.dtr`
playback. It is deliberately separate from the desktop converter.

| Path | Responsibility |
| --- | --- |
| [`plugins/`](plugins/) | CounterStrikeSharp orchestration, commands, tests, and companion API |
| [`runtime/`](runtime/) | Native Metamod replay and bot-presentation runtimes |

The maintained release combines these projects as one versioned playback
bundle. Do not mix binaries from different builds: the manifest, native ABI,
BotHider API, and CounterStrikeSharp reader must remain compatible.

## Projectile hook compatibility profile

The managed plugin ships `demotracer-native.json` beside `DemoTracer.dll`.
The project and playback packaging script both include this file. Keep the
profile with its matching plugin; missing or incompatible profiles disable
projectile birth alignment with a diagnostic instead of using embedded offsets.

Initialization validates a `pe-image-v1` fingerprint, resolves a unique signature
in executable sections to the reviewed entry, checks the complete loaded function
body and projectile vtable pointers, then registers through CounterStrikeSharp.
The fingerprint includes PE headers, section layout, code, data, relocations,
unwind information and PDB GUID. It normalizes build timestamps, checksum,
certificate location and CodeView age, and excludes the DOS stub and file overlay.
Only metadata-equivalent rebuilds are accepted automatically. Code, ABI or layout
changes still require binary review and an updated profile; a matching prologue
alone is insufficient. Restart after installing a reviewed profile.

`PeImageFingerprint.cs` is shared with CS2-Bot-Improver-light under its retained
GPL-3.0-or-later notice. Keep the algorithm and mutation tests synchronized when
changing either implementation. No additional native hook engine is introduced.

## Shared hook runtime

BotRandomizer is distributed as one common provider for ordinary bot servers,
Bot Improver Panel and DemoTracer. Build the public package with
`server/runtime/BotRandomizer/tools/package.ps1`; `package-server.ps1` consumes
that package, or an explicit `-BotRandomizerPackage` ZIP with matching version,
API, KHook pins and file hashes. It never creates a second replay implementation.
For upstream review, `tooling/scripts/export-bot-randomizer.ps1 -Destination
tmp/randomizer-standalone` exports complete independently buildable source,
including the shared API, tests and CI. See the provider's
[README](runtime/BotRandomizer/README.md) and [API](runtime/BotRandomizer/API.md).

BotController and BotHider use the single [KHook](https://github.com/Kenzzer/KHook)
engine exported by Metamod for both function and virtual hooks. The playback
server requires Metamod 2.0 build 1469 or newer (plugin API 18) and a
KHook-enabled CounterStrikeSharp host. CounterStrikeSharp's managed API minimum
remains 1.0.371; that version number alone does not prove its native hook backend.

The maintained source baseline is recorded in
[`playback-contract.v1.json`](../shared/contracts/playback-contract.v1.json):
Metamod `fa6f80e4662e5b96cc2e97722d812f374581dfd8`, its KHook submodule
`40d233d160b5bf60cc3e732939142b222fbd8ece`, and CounterStrikeSharp
`9cbdee0ce7871671f38ff25101052bd5c2f0a858` from
[the KHook migration](https://github.com/roflmuffin/CounterStrikeSharp/pull/1418).
Use that CounterStrikeSharp source build with the matched playback bundle.
The public v1.0.374 host predates this backend migration. Metamod and
CounterStrikeSharp are external prerequisites and are not included in the bundle.

For native development, set `MMSOURCE_DEV` to the pinned Metamod checkout with
`third_party/khook` initialized, alongside the existing `HL2SDKCS2` and protobuf
toolchain. Both native CMake projects reject SDKs without the KHook interface.
Product DLLs consume Metamod's engine; they do not embed a separate copy.
Signature scanning also uses KHook so signatures still match original engine
bytes when another consumer has already installed a detour.

BotRandomizer participates in this same engine through CounterStrikeSharp:
`GiveNamedItemFunc.Hook/Unhook` delegates to the host's dynamic hooks, which
register and remove hooks with Metamod's KHook interface in the pinned CSS
build. Its attribute writer and item-view constructor are native function
calls, not separate detours; CSS resolves their signatures through KHook.
Do not add a private hook engine or a second native Randomizer hook layer.
The NuGet API package alone does not select the host backend.

Shared KHook coordinates hook chaining and original function execution. Cosmetic
ownership remains the responsibility of the single BotRandomizer provider and
its API v3 plans. Loading two cosmetic providers can still overwrite inventories
even when both use KHook. A future common upstream Randomizer must preserve both
this host requirement and the single-writer plan lifecycle.

The shared hook integration tests build the pinned upstream engine only for
testing. After initializing its recursive submodules, run from the repo root:

```powershell
cmake -S server/runtime/common/tests -B server/runtime/common/tests/build -A x64
cmake --build server/runtime/common/tests/build --config Release
ctest --test-dir server/runtime/common/tests/build -C Release --output-on-failure
```

BotController ABI minor 43 marks this migration. The replay format and public
control API are unchanged. The GUI rejects install receipts from the older
hook runtime and the managed heartbeat reports older BotController binaries
as incompatible.

Presentation and cosmetic plans are owned by the consumer's lifetime, not a
periodically renewed timeout. BotHider API v2 and BotRandomizer API v3 require
a cancellation token at acquisition; consumers cancel it on the server thread
on unload. Replacement keeps the same owner, and map changes or provider unload
revoke the plans. Provider lifecycle notifications trigger reconnection, so idle
playback does not poll providers or rebuild empty cosmetic plans each tick.
The GUI's runtime health file remains periodic because it reports liveness
across processes.

The native runtime is built on the foundational work in
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) and
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider). See the root
[credits](../README.md#credits-and-foundations) and the runtime-specific
upstream notes for the exact maintenance boundary.
