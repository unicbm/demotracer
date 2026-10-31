# DemoTracer playback plugin

CounterStrikeSharp playback component for DemoTracer.
Assembly: `DemoTracer`; companion capability: `demotracer:api` (API 7).
Install the matched product Playback bundle for the native providers and shared APIs.

## Source

Production code is in `src/DemoTracer/`, configuration in `config/`, and
regression tests in `tests/DemoTracer.Tests/`.

| Source directory | Responsibility |
| --- | --- |
| `Lifecycle`, `Commands`, `Configuration` | Plugin lifetime, console commands and settings |
| `Playback`, `Playback/Inventory` | Round plans, ownership, scheduling, handoff and equipment |
| `Native`, `Projectiles` | Native bindings, health checks and projectile birth alignment |
| `Presentation`, `Cosmetics` | dtr-hider and BotRandomizer consumers |
| `Data`, `Voice` | DTR/manifest readers and voice playback |

`ReplaySlotRegistry` owns slot state and epochs; `ReplaySessionState` owns
session resets. Warm buffers and pending plans do not grant bot write ownership.
BotRandomizer applies cosmetics; DemoTracer tracks accepted plans.

Console command responses and runtime status messages use English.

External utility providers can set a projectile's entity name to
`dtr_external_projectile_v1` before its first physics step. Playback skips both
alignment and recorded-throw consumption for that projectile. The marker is
published in `shared/contracts/playback-contract.v1.json`; NadeSystem uses it for
fire-protection smoke without releasing replay ownership.

## Build and package

With .NET 10, from `server/plugins/DemoTracer`:

```powershell
pwsh -NoProfile -File tools/check.ps1
pwsh -NoProfile -File tools/package.ps1
```

Both scripts accept `-DotnetPath`. Checks build Release and run the managed
suite without a live CS2 server. Engine hooks still need server testing.
Dependencies come from `server/runtime/common`, `dtr-hider` and `BotRandomizer`.

The component ZIP contains the plugin, dependency metadata, Zstd decoder, native
profile, econ data and shared `DemoTracerApi.dll`. Full product packaging adds
the native providers; CS2, Metamod and CounterStrikeSharp remain separate. See
the [development guide](../../../docs/DEVELOPMENT.md#packaging).

AGPL-3.0-only. See [LICENSE](LICENSE), [UPSTREAM.md](UPSTREAM.md) and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
