# dtr-controller

The maintained source repository is
[`unicbm/demotracer`](https://github.com/unicbm/demotracer/tree/main/server/runtime/dtr-controller).
It contains the native `dtr-controller.dll` runtime used directly by DemoTracer,
with `DtrController_` exports. The separate managed recording provider, public
SDK, and single-file binding are retired. Native ABI and replay layouts are
unchanged. See [UPSTREAM.md](UPSTREAM.md) for attribution and maintained differences.

## Standalone checks

Build from the DemoTracer working tree. Shared infrastructure is consumed
directly from `server/runtime/common`.

With PowerShell 7, CMake, and a C++20 compiler installed:

```powershell
./tools/check.ps1
./tools/check.ps1 -NativeBuild
```

The default check runs Release native tests without a
CS2 SDK. `-NativeBuild` additionally builds the plugin and requires the SDK
environment described below. For a local shared-code checkout, use
`-CommonDirectory /path/to/common`; direct builds accept
`-DDTR_COMMON_DIR=...`.
The check script stages native installation files in `.build/native/package/`.

## Runtime

CS2-Bot-Controller is a Metamod:Source plugin for Counter-Strike 2 that takes
control of a bot's behaviour at the engine level. It can pin a bot's weapon,
freeze its aim, or hand its movement over to external code —
and it can **record** a human player's per-tick movement and **replay** it back
through any bot.

It exposes both in-game console commands and a C-ABI surface for
CounterStrikeSharp, so a plugin can record, transfer, and replay motion with a
few P/Invoke calls. The maintained DemoTracer runtime target is Win64. Linux
build scaffolding exists, but the bundled gamedata still has unresolved Linux
signatures/offsets, so Linux runtime packages are not supported yet.

------------------------------------------------------------------------

## Locks

- **Weapon** — pin a bot to one weapon slot; AI switches are blocked.
- **Aim** — freeze `CCSBot::Upkeep`; view holds still, AI keeps deciding/moving.
- **All** — freeze both `CCSBot::Update` and `CCSBot::Upkeep` for callers that
  explicitly need a full native-AI freeze.

------------------------------------------------------------------------

## Record & Replay

Capture a slot's movement tick by tick — origin, velocity, view angles, button
states, duck/ladder state, active weapon and all subtick input steps — then load
it onto another slot and play it back. Replay is driven through the engine's own
movement path, so it reproduces the original motion subtick-accurate.

Typical flow: lock the source slot if needed → `StartRecord` → move → `StopRecord`
→ `TransferRecordingToReplay` into a bot slot → `StartReplay`. While replay
owns the bot's injected command, movement, and view output, native AI update and
upkeep continue in the background so perception and decision state are ready for
handoff. Replay ownership still blocks native `EquipBestWeapon`, `EquipPistol`,
and conflicting `SelectItem` actions; only the weapon requested by the active
replay tick may pass through the hooked selection path. DemoTracer applies a
scoped `Lock(All)` only during freeze-time pre-roll, where contact cannot occur,
then releases it on `round_freeze_end`. Do not otherwise apply `Lock(All)` to a
replay bot when that continuity is wanted.
See the CounterStrikeSharp API section below.

------------------------------------------------------------------------

## Movement Intent

BotController exposes optional low-level movement intent exports for callers
that already own policy and target selection:

- `DtrController_SetUsercmdMovementIntent`
- `DtrController_ClearUsercmdMovementIntent`
- `DtrController_SetLeftHandIntent`
- `DtrController_ClearLeftHandIntent`
- `DtrController_GetMovementIntentContractVersion`

ABI minor 44 preserves movement input contract 1: positive forward is W and
positive left is A in both usercmd and CMoveData. Consumers can query the version
before acquiring control. Owned button transitions are encoded against the
previous engine-held state, preserving single-tick jump presses and unrelated
native input; button-only modifiers retain native movement axes. This includes
the input-boundary fix previously validated by the Bot Improver consumer.

The `LeftHandIntent` names are compatibility aliases. The native primitive
writes short-lived button and analog movement intent into the usercmd/movedata
path only. Its supported button mask is WASD, duck, jump, walk, and primary
attack (`IN_ATTACK`); callers own the decision to set or clear those buttons.
It does not choose targets, aim, switch weapons, teleport, or write absolute
velocity. Other button bits are ignored. Intent and replay execution require
the current autonomous bot pawn; human players can still be recorded. A pawn
replacement or human takeover invalidates existing control. Active DTR replay owns its replay slot,
and replay load/start/stop/finish/clear paths clear any movement intent on that
slot.

------------------------------------------------------------------------

## Slots

| Target  | Engine | Weapon                  |
| ------- | ------ | ----------------------- |
| `Slot1` | 0      | Primary                 |
| `Slot2` | 1      | Pistol                  |
| `Slot3` | 2      | Knife / Zeus            |
| `Slot4` | 3      | Grenades                |
| `Slot5` | 4      | C4                      |

------------------------------------------------------------------------

## Install

The build stages a ready-to-copy `addons/` tree under `build/package/`.

- `dtr-controller.dll` → `csgo/addons/dtr-controller/bin/win64/`
- `gamedata.json` → `csgo/addons/dtr-controller/`
- `dtr-controller.vdf`  → `csgo/addons/metamod/`

------------------------------------------------------------------------

## Build

Env: `HL2SDKCS2`, `MMSOURCE_DEV`, `CSGO_PROTO`, `protoc` (3.21.x) on PATH.
`MMSOURCE_DEV` must include Metamod's KHook API and initialized KHook submodule;
use the matched source pins in `server/runtime/common/contracts/hook-runtime.v1.json`.
ABI minor 43 uses Metamod's shared KHook engine for function hooks. No private
detour engine is linked into the runtime.

```
cmake -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

Config sources (vdf + gamedata) live under `configs/addons/`; the build copies
them into the package tree automatically.

------------------------------------------------------------------------

## Commands

```
dtr_controller_lock <all|aim|weapon> <slot> [slot1..slot5]
dtr_controller_unlock <all|aim|weapon> <slot>
dtr_controller_unlock_all <all|aim|weapon>
dtr_controller_perf [0|1|reset]
dtr_controller_status
```

`weapon` mode requires the weapon slot as the third argument.

```
dtr_controller_lock aim 1                # freeze bot 1's view, AI still runs
dtr_controller_lock all 1                # explicit full native-AI freeze
dtr_controller_lock weapon 1 slot3       # force bot 1 to knife
dtr_controller_unlock_all weapon         # clear every weapon lock
dtr_controller_perf 1                    # enable and print replay perf counters
dtr_controller_status                    # print hook status + every per-slot lock
```

Record / replay is driven through the C-ABI below, not console commands.

Replay provides simulation-local angles and the final post-angle getter. The
engine updates and networks `m_angEyeAngles` at its normal command boundary.
Command angles use recorded command data, or the tick pre view when absent.

------------------------------------------------------------------------

## Demo-backed avatar publication

DemoTracer's native avatar publisher requires ABI 21.41 and capability bit 18.
`DtrController_PublishAvatarOverride(steamId, png, length)` synchronously writes
and verifies a PNG of at most 16 KiB. A nonnegative result confirms server
publication, not display on a remote client. `DtrController_ClearAvatarOverride`
and `DtrController_ClearAvatarOverrides` restore preceding data, preserving a
later writer's replacement. Call these functions on the server game thread.

Avatar publication updates the server's `ServerAvatarOverrides` table. Client
caching can delay visible changes. Map shutdown discards publication ownership;
plugin unload restores owned server entries while preserving later writers.

`dtr_controller_avatar_status` reports server publication availability and active ownership.
`dtr_controller_avatar_override_probe <steamid64> <png_path>` and
`dtr_controller_avatar_override_clear <steamid64>` exercise the same publisher for local
diagnostics. Avatar refresh does not republish userinfo or replace BotHider's
identity lease. PNG selection remains opt-in and manifest-backed in DemoTracer.

## Special thanks

- [cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod) for helping determine the replay framework.

------------------------------------------------------------------------

## License

AGPL-3.0-only. This DemoTracer runtime is a maintained derivative of
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller); see
[UPSTREAM.md](UPSTREAM.md) for the maintenance boundary.

------------------------------------------------------------------------

## Author

**XBribo and DemoTracer contributors**
