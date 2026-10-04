# Playback server

Install the matched Playback bundle from GUI **Settings → CS2** with CS2 closed.
It contains [DemoTracer](plugins/DemoTracer/README.md),
[dtr-controller](runtime/dtr-controller/README.md),
[dtr-hider](runtime/dtr-hider/README.md) and
[BotRandomizer](runtime/BotRandomizer/README.md). Keep their DLLs and shared APIs together.
Metamod and CounterStrikeSharp are installed separately. Restart the server after native updates.

See [playback commands](../docs/COMMANDS.md) and the
[configuration example](plugins/DemoTracer/config/demotracer.config.example.json).

## Shared hook runtime

The supported target is Windows x64 with Metamod 2.0 build 1469+ (plugin API 18)
and a KHook-enabled CounterStrikeSharp host. The managed API minimum is 1.0.371;
the NuGet version alone does not establish native hook compatibility.
Use the source pins in [playback-contract.v1.json](../shared/contracts/playback-contract.v1.json),
including the matched CounterStrikeSharp build. Public host v1.0.374 predates the KHook migration.

All three providers use Metamod's shared KHook engine. KHook coordinates hooks;
the provider APIs coordinate presentation and cosmetic ownership. Run one
BotRandomizer provider and use its [API v3](runtime/BotRandomizer/API.md) for external plans.
Consumers cancel ownership on the server thread when unloading and reconnect
through provider lifecycle notifications.

Native builds require CMake, a C++20 compiler, protoc 3.21.x on `PATH`,
`HL2SDKCS2` and `MMSOURCE_DEV`. Initialize the pinned Metamod checkout's KHook
submodule recursively. `CSGO_PROTO` is optional when the HL2SDK supplies
`common/network_connection.proto`. Shared hook tests are covered by
[common's check script](runtime/common/README.md).

## Projectile hook profile

Keep `demotracer-native.json` beside `DemoTracer.dll`. Its source is
[`config/demotracer-native.json`](plugins/DemoTracer/config/demotracer-native.json).
Missing or incompatible profiles disable projectile birth alignment and report
a diagnostic. Updating a profile requires reviewing the PE image fingerprint,
complete function body, signatures and projectile vtables, then restarting the server.
See [signature maintenance](../docs/SIGNATURES.md).

`PeImageFingerprint.cs` is shared with CS2-Bot-Improver-light under its retained
GPL-3.0-or-later notice. Keep its algorithm and mutation tests synchronized.

The runtime derives from
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) and
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider).
Their `UPSTREAM.md` files record attribution and maintenance boundaries.
