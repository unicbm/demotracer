# CS2 CSS DemoTrace

The CounterStrikeSharp playback plugin maintained for CS2 DemoTracer. This
repository owns the managed playback source and its regression tests. The
repository name does not change the runtime identity: the assembly and plugin
directory remain `DemoTracer`, and the companion capability remains
`demotracer:api` (API 7).

## Dependencies

Clone with `git clone --recurse-submodules` or run
`git submodule update --init --recursive` after checkout. Builds use .NET 10 and
CounterStrikeSharp.API 1.0.371. The exact native host pins are supplied by
`.deps/common/contracts/hook-runtime.v1.json`.

| Dependency | Used for |
| --- | --- |
| `.deps/common` | `DemoTracerApi`, econ data, and shared host contract |
| `.deps/hider` | BotHider API and provider regression tests |
| `.deps/randomizer` | BotRandomizer API |
| `.deps/controller` | BotController public/provider API regression tests |

The plugin project excludes all dependency and test source trees from default
compilation; it references their projects explicitly. Generated econ data has
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
select its pinned checkouts. Standalone defaults remain under `.deps/`.
The check script supplies the same common checkout to the provider test graph.

## Packaging

`tools/package.ps1` creates `dist/DemoTracer-CSS-v1.3.0.zip`, its SHA-256 file,
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

AGPL-3.0-only. See [UPSTREAM.md](UPSTREAM.md), [LICENSE](LICENSE), and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Public source never includes
server-local configuration, demo files, replay archives, logs, or credentials.
