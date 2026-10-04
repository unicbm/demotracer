# Playback server

Server modules for `.dtr` playback:

| Path | Responsibility |
| --- | --- |
| [`plugins/DemoTracer/`](plugins/DemoTracer/) | Product playback module; orchestration and commands in `src/DemoTracer`, configuration in `config`, regression suite in `tests/DemoTracer.Tests` |
| [`runtime/`](runtime/) | Product bot runtime modules and their public APIs |
| [`runtime/common/`](runtime/common/) | Shared native utilities, companion API, contracts and econ data |

Install the matched playback bundle. See the
[plugin development guide](plugins/DemoTracer/docs/DEVELOPMENT.md) for source
layout and build commands.

## Projectile hook compatibility profile

The managed plugin ships `config/demotracer-native.json` from its source
repository beside `DemoTracer.dll` as `demotracer-native.json`.
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

BotRandomizer serves ordinary bot servers and DemoTracer through the same
provider and API. `package-server.ps1` consumes its standalone package, or an
explicit `-BotRandomizerPackage` ZIP, checking version, API, host pins and hashes.
Source and fixes belong in [`runtime/BotRandomizer`](runtime/BotRandomizer/README.md).

dtr-controller and dtr-hider use the single [KHook](https://github.com/Kenzzer/KHook)
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

BotRandomizer hooks `GiveNamedItem` through CounterStrikeSharp's KHook backend.
KHook coordinates detours; it does not coordinate cosmetic writes. Use one
BotRandomizer provider, with [API v3](runtime/BotRandomizer/API.md) ownership plans.

The shared hook integration tests build the pinned upstream engine only for
testing. After initializing its recursive submodules, run from the repo root:

```powershell
cmake -S server/runtime/common/native/tests -B server/runtime/common/.build/native-tests -A x64
cmake --build server/runtime/common/.build/native-tests --config Release
ctest --test-dir server/runtime/common/.build/native-tests -C Release --output-on-failure
```

The current playback contract requires dtr-controller ABI 22.
The GUI rejects install receipts from the older hook runtime. The managed
heartbeat reports older dtr-controller binaries as incompatible.

Presentation and cosmetic plans are owned by the consumer's lifetime, not a
periodically renewed timeout. dtr-hider API v3 and BotRandomizer API v3 require
a cancellation token at acquisition; consumers cancel it on the server thread
on unload. Replacement keeps the same owner, and map changes or provider unload
revoke the plans. Provider lifecycle notifications trigger reconnection, so idle
playback does not poll providers or rebuild empty cosmetic plans each tick.
The GUI's runtime health file remains periodic because it reports liveness
across processes.

The native runtime is built on the foundational work in
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) and
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider). See the root
[credits](../README.md#credits-and-license) and the runtime-specific
upstream notes for the exact maintenance boundary.
