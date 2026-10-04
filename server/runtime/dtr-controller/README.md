# dtr-controller

Windows x64 Metamod runtime for DemoTracer movement and input replay.
The native library is `dtr-controller.dll`; exports use `DtrController_`.
ABI requirements are in the [playback contract](../../../shared/contracts/playback-contract.v1.json).
ABI 22.1 adds `DtrController_FindMetamodPlugin(library)`: on the game thread,
query a library basename without its extension and receive its running/paused
Metamod plugin ID, or zero if absent. Hider switching uses this registration
state instead of DLL residency.

## Build and install

From this directory, with PowerShell 7, CMake and a C++20 compiler:

```powershell
./tools/check.ps1
./tools/check.ps1 -NativeBuild
```

The default command runs Release native tests without the CS2 SDK.
`-NativeBuild` also builds the plugin using the [server SDK environment](../../README.md#shared-hook-runtime)
and stages `.build/native/package/addons/`:

- `dtr-controller/bin/win64/dtr-controller.dll`
- `dtr-controller/gamedata.json`
- `metamod/dtr-controller.vdf`

Use the matched product Playback bundle for installation and restart the server.
Linux runtime packages are unsupported.

## Replay and locks

After `SetupMove`, replay supplies demo pre-command position and velocity in
`CMoveData`; native movement and `FinishMove` produce the pawn state.
Pose and duck/ladder initialization occur only at start, seek or loop boundaries.
Missing command axes are neutral.

Native AI continues perception during replay so it is ready for handoff.
Replay blocks conflicting weapon selection. DemoTracer uses `Lock(All)` only
during freeze-time pre-roll and releases it at `round_freeze_end`.

| Lock | Effect |
| --- | --- |
| `weapon` | Pin a weapon slot and block AI switching |
| `aim` | Freeze `CCSBot::Upkeep`; AI decisions and movement continue |
| `all` | Freeze both `CCSBot::Update` and `Upkeep` |

```text
dtr_controller_lock <all|aim|weapon> <slot> [slot1..slot5]
dtr_controller_unlock <all|aim|weapon> <slot>
dtr_controller_unlock_all <all|aim|weapon>
dtr_controller_perf [0|1|reset]
dtr_controller_status
```

Weapon slots 1–5 are primary, pistol, knife/Zeus, grenades and C4.
Replay is controlled through the native API and [DemoTracer commands](../../../docs/COMMANDS.md).

## Movement intent API

`DtrController_SetUsercmdMovementIntent` and `ClearUsercmdMovementIntent`
apply short-lived input to autonomous bot pawns. `GetMovementIntentContractVersion`
returns contract 1: positive forward is W; positive left is A.
`SetLeftHandIntent` / `ClearLeftHandIntent` are aliases.

Supported buttons are WASD, duck, jump, walk and primary attack. Button-only
modifiers preserve native axes; transitions preserve single-command presses.
The API does not select targets, aim, switch weapons or teleport.
Pawn replacement or human takeover invalidates control. DTR replay owns its
slot and clears other intent on load, start, stop, finish and clear.

## Avatar publication

On the game thread, `DtrController_PublishAvatarOverride(steamId, png, length)`
publishes and verifies a PNG of at most 16 KiB. A nonnegative return confirms
server publication; client caching may delay display.
`ClearAvatarOverride` and `ClearAvatarOverrides` restore preceding entries
without overwriting a later writer. Map shutdown drops ownership; unload restores owned entries.

```text
dtr_controller_avatar_status
dtr_controller_avatar_override_probe <steamid64> <png_path>
dtr_controller_avatar_override_clear <steamid64>
```

DemoTracer uses manifest-backed PNGs only when avatar playback is enabled.
Avatar publication does not change dtr-hider identity leases.

## Credits and license

AGPL-3.0-only. Maintained derivative of
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller);
see [UPSTREAM.md](UPSTREAM.md). Authors: XBribo and DemoTracer contributors.
Thanks to [cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod)
for the replay framework groundwork.
