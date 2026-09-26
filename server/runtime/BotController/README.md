# CS2-Bot-Controller

**Bot replay runtime for CS2 DemoTracer**

## Your stars⭐ are my motivation to keep updating

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

- `BotController_SetUsercmdMovementIntent`
- `BotController_ClearUsercmdMovementIntent`
- `BotController_SetLeftHandIntent`
- `BotController_ClearLeftHandIntent`
- `BotController_GetMovementIntentContractVersion`

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

- `BotController.dll` → `csgo/addons/BotController/bin/win64/`
- `gamedata.json` → `csgo/addons/BotController/`
- `BotController.vdf`  → `csgo/addons/metamod/`

------------------------------------------------------------------------

## Build

Env: `HL2SDKCS2`, `MMSOURCE_DEV`, `CSGO_PROTO`, `protoc` (3.21.x) on PATH.
`MMSOURCE_DEV` must include Metamod's KHook API and initialized KHook submodule;
use the [matched source baseline](../../README.md#shared-hook-runtime).
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
bc_lock <all|aim|weapon> <slot> [slot1..slot5]
bc_unlock <all|aim|weapon> <slot>
bc_unlock_all <all|aim|weapon>
bc_perf [0|1|reset]
bc_status
```

`weapon` mode requires the weapon slot as the third argument.

```
bc_lock aim 1                # freeze bot 1's view, AI still runs
bc_lock all 1                # explicit full native-AI freeze
bc_lock weapon 1 slot3       # force bot 1 to knife
bc_unlock_all weapon         # clear every weapon lock
bc_perf 1                    # enable and print replay perf counters
bc_status                    # print hook status + every per-slot lock
```

Record / replay is driven through the C-ABI below, not console commands.

Replay provides simulation-local angles and the final post-angle getter. The
engine updates and networks `m_angEyeAngles` at its normal command boundary.
There is no spectator mask or per-tick absolute POV correction. The obsolete
`bc_replay_view`, `bc_replay_cmd_view`, and POV publishing controls are removed;
command angles use recorded command data, falling back to the tick pre view.

------------------------------------------------------------------------

## CounterStrikeSharp API

Drop `scripts/BotController.NativeApi.cs` into your project when you are
building a low-level BotController integration. This file is a typed C#
P/Invoke binding over the native C ABI; it is not the public DemoTracer
companion-plugin API.

Companion plugins for DemoTracer should use the managed `demotracer:api`
capability from `server/plugins/DemoTracerApi/IDemoTracerApi.cs` instead of depending on
BotController native exports or replay buffer structs.

ABI 21 keeps the upstream 228-byte replay tick layout. Its 36-byte event tail is
reserved: every field must be zero, and loads reject unsupported native drop
payloads. Public motion recording and JSON replay remain available, but do not
capture or replay weapon drops. DTR gameplay events use their managed executor.

```csharp
using BotControllerApi;

if (!BotController.IsCompatible()) return;   // requires ABI 21
BotController.TryGetAbiInfo(out var abiInfo);
var capabilities = BotController.Capabilities();
var buildId = BotController.BuildId();
```

DemoTracer intentionally skips native ABI 17 because current upstream
BotController uses that number for an incompatible export surface. Reusing it
would let upstream managed bridges falsely accept the DemoTracer runtime.

Low-level movement integrations can probe
`BotController.CapabilityUsercmdMovementIntent` and then call
`SetUsercmdMovementIntent` / `ClearUsercmdMovementIntent`. The `SetLeftHandIntent`
helpers are present only for compatibility with existing left-hand movement
callers.

ABI minor 32 adds the optional
`BotController.CapabilityButtonOnlyMovementIntent` capability. When present,
callers may pass `BotController.MovementIntentPreserveMoveAxes` in `flags` to
apply `buttonsSet`/`buttonsClear` at `PlayerRunCommand` and `ProcessMovement`
without replacing the engine-authored forward, left, or up movement axes. The
movement-button mask includes `IN_SPEED`, allowing a caller to remove Walk
while leaving native navigation in control.

ABI minor 33 adds `BotController.CapabilityHandoffBestWeapon` and
`RequestEquipBestWeapon`. The request is tied to the currently observed
`CCSBot` incarnation and consumed once after its next native `Update`. It is
discarded if replay control or a weapon lock resumes before execution.

ABI minor 34 adds `BotController.CapabilityReplayInputHistory` and
`LoadReplayWithInputHistory`. The export remains ABI-compatible, but the
Windows runtime does not currently advertise the capability: mutating
engine-owned `CSGOUserCmdPB` history entries across the module ABI boundary can
corrupt the command ring. Matched callers fall back to `LoadReplayExtended`,
preserving command, movement, and subtick playback while leaving native input
history untouched.

The input-history and movement-extra ABI parameters still receive validation
for compatibility with older integrations. Their unsupported playback data is
not retained in native replay buffers. Current replay state restoration uses
the source-state timeline.

Replay handoff integrations can probe `CapabilityNativePerception`, then read
`TryGetNativePerceptionState`. During replay, the native vision detours disable
only the `CCSBot::IsVisible` FOV test; LOS/smoke logic and native enemy/reaction
state continue normally. `SetReplayNativeFovOverride` controls this replay-only
behavior.

### Locks

```csharp
BotController.Lock(slot, LockKind.Aim);
BotController.Lock(slot, LockKind.All);
BotController.Lock(slot, LockTarget.Slot3);   // weapon lock
BotController.Unlock(slot, LockKind.Aim);
BotController.UnlockAll(LockKind.Weapon);
BotController.IsLocked(slot, LockKind.Aim);
BotController.GetWeaponLock(slot);            // -> LockTarget
```

### Record & Replay

```csharp
// Record a slot's motion
BotController.StartRecord(srcSlot);
// ... player moves ...
BotController.StopRecord(srcSlot);

// Replay it on a bot
BotController.TransferRecordingToReplay(srcSlot, botSlot);
// Do not Lock(All): replay owns output while native AI shadow-runs for handoff.
BotController.StartReplay(botSlot, loop: false);

// Or pull the buffers out, persist them, and load later
var (ticks, subs) = BotController.GetRecordedMotion(srcSlot);
BotController.LoadReplay(botSlot, ticks, subs);

// Drive weapon/fire from the tick being replayed
if (BotController.TryGetReplayTick(botSlot, out var tick))
    BotController.SwitchBotWeapon(botSlot, tick.WeaponDefIndex);

BotController.ReplayCursor(botSlot);          // current tick, <0 if idle
BotController.ReplayTotal(botSlot);           // loaded tick count
BotController.StopReplay(botSlot);            // retain buffers for a warm restart

// When this slot will not be reused soon, capability-probe and release its
// native tick/subtick/command buffer capacity as well as stopping replay.
if ((BotController.Capabilities() & BotController.CapabilityReleaseReplayBuffer) != 0)
    BotController.ReleaseReplayBuffer(botSlot);
```

`ReplayTick` / `SubtickMove` mirror the C++ struct layout byte-for-byte, so the
buffers can be serialized and reloaded across rounds. Main thread only.

Replay weapon selection caches the resolved inventory cell while its recorded
definition, `WeaponServices` binding, and exact inventory entry remain
unchanged, then compares the active handle directly. Load/start/stop, loop
restart, explicit native weapon switch,
give/drop/replacement detection, and global reset invalidate or refresh the
cache; no missing-weapon result is cached.

------------------------------------------------------------------------

## Demo-backed avatar publication

DemoTracer's native avatar publisher requires ABI 21.41 and capability bit 18.
`BotController_PublishAvatarOverride(steamId, png, length)` synchronously writes
and verifies a PNG of at most 16 KiB. A nonnegative result confirms server
publication, not display on a remote client. `BotController_ClearAvatarOverride`
and `BotController_ClearAvatarOverrides` restore preceding data, preserving a
later writer's replacement. Call these functions on the server game thread.

For a Windows listen server, the optional local client bridge attaches to
`ServerAvatarOverrides` data changes and queues Valve's targeted
`ReloadAvatarImage` event after the matching bytes arrive. Identical content
does not repeatedly invalidate the HUD. Map changes rebind the callback;
unload restores the prior callback and owned local images. The bridge requires
validated engine, client, and Panorama signatures. A failed validation leaves
server publication available and reports that local HUD refresh is unavailable.
It does not install code on remote clients.

`bc_avatar_status` reports bridge availability and refresh-event counts.
`bc_avatar_override_probe <steamid64> <png_path>` and
`bc_avatar_override_clear <steamid64>` exercise the same publisher for local
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
