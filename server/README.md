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

## Shared hook runtime

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
