# CSS component development

The CounterStrikeSharp playback plugin maintained for CS2 DemoTracer. This
repository owns the managed playback source and its regression tests. The
assembly and plugin directory are `DemoTracer`; the companion capability is
`demotracer:api` (API 7).

## Source responsibilities

The project is `src/DemoTracer/DemoTracer.csproj`. Its source directories follow
the responsibilities of the existing classes and partial plugin files:

| Directory under `src/DemoTracer` | Responsibility |
| --- | --- |
| `Lifecycle` | Plugin entry, API registration, game events, session state, and deferred work |
| `Commands` | Command authorization and control, playback, preset, cosmetic, and alignment commands |
| `Configuration` | Runtime configuration loading and application |
| `Playback` | Round loading, preparation, scheduling, slot ownership, timing, retention, and handoff |
| `Playback/Inventory` | Replay inventory timelines, equipment alignment, grants, and weapon replacement |
| `Native` | BotController binding, ABI types, native mapping, module fingerprints, and health checks |
| `Presentation` | BotHider bridge, avatars, scoreboard, view state, round banners, and chat |
| `Cosmetics` | BotRandomizer bridge, cosmetic leases, evidence validation, and alignment |
| `Projectiles` | Projectile births, physics hooks, trace, and alignment |
| `Voice` | Voice models, codec, loading, and playback |
| `Data` | DTR section readers, validation, manifests, and shared plugin models |

Configuration inputs live in `config/`; the project links them into its build output.
Tests stay in `tests/DemoTracer.Tests/`. License and source provenance stay at the
repository root.

`ReplaySessionState` owns the reset rules for managed execution and loaded
replay state. Native control release and presentation restoration remain at
the plugin lifecycle boundary. Warm buffers and pending round plans have
separate lifetimes and do not grant bot write ownership. Cosmetic application
belongs to BotRandomizer; DemoTracer tracks accepted plans, not entity writes.

## Dependencies

Build from the DemoTracer working tree. Builds use .NET 10 and
CounterStrikeSharp.API 1.0.371. The exact native host pins are supplied by
`server/runtime/common/contracts/hook-runtime.v1.json`.

| Dependency | Used for |
| --- | --- |
| `server/runtime/common` | `DemoTracerApi`, econ data, and shared host contract |
| `server/runtime/dtr-hider` | BotHider API and provider regression tests |
| `server/runtime/BotRandomizer` | BotRandomizer API |
| `server/runtime/dtr-controller` | BotController public/provider API regression tests |

The plugin project contains only its source tree and references dependency
projects explicitly. Generated econ data has
one owner in common. Do not copy or independently update its JSON here.

## Build and check

```powershell
pwsh -NoProfile -File tools/check.ps1
pwsh -NoProfile -File tools/package.ps1
```

Both scripts accept `-DotnetPath` when the .NET 10 executable is not the default
on `PATH`. `tools/check.ps1` builds Release and runs the complete migrated
`tests/DemoTracer.Tests` suite, including the public/provider API tests; it
does not require a live CS2 server. Actual engine hooks, playback, takeover,
and live cosmetic publication still need matched-server smoke tests.

Product integration can pass absolute `DtrCommonRoot`, `DtrHiderRoot`,
`DtrRandomizerRoot`, and (for tests) `DtrControllerRoot` MSBuild properties to
select alternative source directories. Defaults use the product's runtime modules.
The check script supplies the same common checkout to the provider test graph.
Release plugin output is under `src/DemoTracer/bin/Release/net10.0/`.

## Packaging

`tools/package.ps1` creates `dist/DemoTracer-CSS-v1.5.0.zip`, its SHA-256 file,
and a file-hash manifest. The archive contains:

```text
addons/counterstrikesharp/plugins/DemoTracer/DemoTracer.dll
addons/counterstrikesharp/plugins/DemoTracer/ZstdSharp.dll
addons/counterstrikesharp/plugins/DemoTracer/{config examples, econ data, notices}
addons/counterstrikesharp/shared/DemoTracerApi/DemoTracerApi.dll
```

This component package does not include the CS2 server, Metamod,
CounterStrikeSharp host, BotController, BotHider, or BotRandomizer runtimes.
Install the matched product playback bundle to deploy all components. Keep
existing server configuration and install shared APIs in their standard
`shared` directories, never private copies in plugin directories.

Native ABI and `.dtr` compatibility remain governed by the CS2 DemoTracer
product release contract. A managed-only build is not a complete playback
bundle or proof that a different native runtime is compatible.

## License and origin

AGPL-3.0-only. See [UPSTREAM.md](../UPSTREAM.md), [LICENSE](../LICENSE), and
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md). Public source never includes
server-local configuration, demo files, replay archives, logs, or credentials.
