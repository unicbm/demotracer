# Playback Command Reference

Run `dtr_*` commands from the server console or the local listen-server host.
Remote players cannot use them. Prefer the high-level `dtr_go` commands; manual
slot commands are for development and diagnostics.

## Quick Start

```text
css_plugins reload DemoTracer
dtr_runtime
dtr_config_status
dtr_preset 0x15; dtr_go seq "<replay.dtr>" 0
```

`dtr_go` validates and arms the plan, then runs `mp_restartgame 1`. `dtr_arm`
uses the same plan without restarting.

## Playback Plans

| Command | Purpose |
| --- | --- |
| `dtr_go seq <replay.dtr> [from_source_round]` | Replay the manifest from a source round onward. |
| `dtr_go round <replay.dtr> <source_round>` | Replay exactly one source round. |
| `dtr_arm seq|round ...` | Arm the same plan without restarting the server round. |
| `dtr_playoff <true|false>` | Continue an exhausted sequence with SteamID-matched rifle/sniper openings. |
| `dtr_retain <t-order-code> <ct-order-code>` | Set per-side bot retention priority for the next manifest plan; use `clear` to reset. |
| `dtr_stop sequence|replay|slot <slot>|all` | Stop the selected scheduler or replay state. |

`from_source_round` is a demo round index. Mixed playoff rounds do not replay
chat or voice metadata because they can draw the two sides
from different source rounds. Recorded teammate colors remain available.

Playoff continuation draws from live-start inventories where every recorded
player on the selected side has a rifle/sniper rifle. Both teams need at least
two eligible rounds per side. Cash and economy settings do not affect selection.

## Demo Souls

Experimental server commands; stability is not yet verified. The GUI does not
expose this mode.

```text
dtr_soul "<replay.dtr>" <source_round> [t|ct]
dtr_soul_stats
dtr_soul_stop
```

`dtr_soul` challenges one source round repeatedly. The team defaults to the
local caller's T/CT side; the server console must specify `t` or `ct`. At least
one human must be on that team. Team defeat automatically restarts the same
round through the normal replay preparation path. Human death alone does not
restart it. Team victory completes the challenge; there is no next level.

A five-second opening banner shows the source round, team and attempt number.
The statistics command reports combat time and human kills/deaths/damage.
An optional server-local `sounds/demo-souls-death.dtv` in the plugin directory
plays on a human participant's death. It uses the existing server voice stream
and client voice volume, requires no client download, and does not interrupt
demo voice playback. Simultaneous deaths share one sound. The sound asset is
installed separately and is not included in the product bundle.
Victory prints totals and per-attempt results to the server console;
`dtr_soul_stats` also retrieves the current/last challenge. Statistics are
session-local, and interrupted attempts are reported separately from defeats.
`dtr_soul_stop`, replacing the replay plan, map changes and plugin unload cancel
the challenge. No engine team scores or match statistics are overwritten.

## Desktop Preset

```text
dtr_preset [status|0x00..0x3F]
```

| Bit | Hex | Behavior |
| ---: | ---: | --- |
| 0 | `0x01` | Weapon/loadout alignment |
| 1 | `0x02` | Full cosmetic alignment |
| 2 | `0x04` | Demo name and SteamID64 |
| 3 | `0x08` | Manifest avatar override |
| 4 | `0x10` | Automatic voice playback |
| 5 | `0x20` | Playoff continuation |

The normal GUI preset is `0x15`: weapons, Steam identity, and voice. Avatar
requires Steam identity; cosmetics require weapon alignment. The mask does not
change projectiles, handoff, crosshair, partial replay, or
chat settings.

## Replay Behavior

### Fidelity

```text
dtr_align [status|default|full|handoff_safe|off]
dtr_align <weapons|projectiles|crosshair|left_hand|balance> <on|off>
```

`default` enables weapons, projectiles, crosshair, and left-hand desired.
`full` additionally enables the default-off round-start balance write.
`handoff_safe` disables left-hand desired and balance writes. Weapon alignment
applies demo loadouts and active weapon switching; projectile alignment consumes
demo-backed throw evidence. Balance alignment writes only the demo-backed
`m_iAccount` value once when the corresponding DTR round starts; missing
evidence is left untouched.

Projectile alignment applies recorded birth position and velocity once before
the first physics step. CS2 owns subsequent flight, collision and detonation;
effect positions are diagnostics only. `dtr_runtime` reports hook availability.

| Command | Purpose |
| --- | --- |
| `dtr_projectile_align_log [clear|all|molotov|fire]` | Print or clear alignment diagnostics. |

### Identity and Presentation

| Command | Purpose |
| --- | --- |
| `dtr_replay_identity <off|name|steam|avatar>` | Select the bot identity lease. `full` aliases `avatar`. |
| `dtr_partial <0|1>` | Allow fewer replay bots than manifest players. |

Use identity `name` or `off` when the original demo player is also connected to
the local server. `steam` is the normal mode. `avatar` additionally applies a
valid manifest PNG when available.

`name`, `steam` and `avatar` also apply recorded clan tags/group IDs. Missing evidence leaves
presentation unchanged; an explicit empty tag clears it. Identity `off` or
lease release leaves the applied presentation in place.

Recorded teammate colors remain available as demo metadata. Live teammate
colors are assigned by CS2; playback does not overwrite them.

### Cosmetics

```text
dtr_cosmetics [status|off|weapons|basic|full]
dtr_cosmetics <weapons|knives|gloves|names|agents|stickers|charms> <on|off>
```

| Preset | Enabled cosmetics |
| --- | --- |
| `off` | None |
| `weapons` | Ordinary weapon paints and custom names |
| `basic` | Weapons plus knives, gloves and agents |
| `full` | Basic plus stickers and charms |

Cosmetics default off and require demo evidence. The bundled BotRandomizer
API v3 provider is the sole entity writer; DemoTracer submits plans. Missing
agent evidence preserves the engine model. See [BotRandomizer](../server/runtime/BotRandomizer/README.md)
for randomization commands, configuration and host requirements. Changes apply
during spawn/item creation; existing items are not rebuilt.

### Handoff

```text
dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [slot|all]
dtr_handoff_360 [0|1]
```

The default is `death_contact_c4 slot`: release an individual slot on death or
contact; C4 planted releases all active slots. Contact uses fresh native bot
perception. The 360 option disables only the native field-of-view restriction
during replay; native visibility checks remain active.

### Chat and Voice

| Command | Purpose |
| --- | --- |
| `dtr_chat_auto [status|on|off]` | Toggle timed manifest chat replay; default on. |
| `dtr_mute_dead_comms [status|on|off]` | Default on: suppress automatic voice/player chat when the actual pawn is dead while the recorded replay still has alive frames. |
| `dtr_chat_test <loaded|any|slot> [all|team] <message>` | Send one diagnostic bot chat line. |
| `dtr_voice_auto [status|on|off]` | Toggle automatic `.dtv` playback. |
| `dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]` | Test one sidecar with a fixed sender. |
| `dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]` | Test multi-speaker mapping. |
| `dtr_voice_stop` | Stop voice test playback. |

Voice exports use `voice/roundXX.dtv` relative to the manifest, with at least
two round-number digits. In v13 these resources are stored inside the container. Speakers map by
XUID to replay bots. Observers hear all replay voice; human T/CT players hear
their own team. Bots and HLTV are not recipients. Missing usable voice evidence
produces no sidecar.

`mute_diverged_dead_comms` persists the dead-pawn communication setting in
`demotracer.config.json`. Recorded communication beyond the exported alive-frame
interval is preserved, as are server chat and manual communication tests.

## Manual Replay Control

These commands bypass some sequence lifecycle handling and are intended for
debugging.

| Command | Purpose |
| --- | --- |
| `dtr_load round <replay.dtr> <source_round>` | Load one round onto safe bot slots. |
| `dtr_load slot <slot> <path.dtr>` | Load one raw replay without manifest-only metadata. |
| `dtr_play loaded [loop:0|1]` | Start every loaded slot immediately. |
| `dtr_play slot <slot> [loop:0|1]` | Start one loaded slot. |
| `dtr_unload <slot>` | Unload one slot and clear its metadata. |
| `dtr_kick <exact-name>|slot <slot>|sid <steamid64>` | Release and kick a replay bot safely. |

Loops restart the still-controlled slots together from the live-play index,
refreshing cursors and loadouts. Stopped/handed-off slots stay out.
A complete round loop restarts chat and voice; individual slot loops do not.

## Configuration

Optional defaults live in `demotracer.config.json` next to `DemoTracer.dll`.
Start from the packaged `demotracer.config.example.json`, using `fidelity` and
`cosmetics` sections. JSON comments and trailing commas are accepted.

Playback does not synchronize K/D/A, MVPs, individual scores, team scores or
team names. CS2 owns match statistics and round settlement. Archive statistics
remain available in the GUI for analysis.

| Setting | Default |
| --- | --- |
| Identity | `steam` |
| Fidelity | weapons, projectiles, crosshair, and left-hand on |
| Round-start balance | off |
| Cosmetics | off |
| Partial replay | on |
| Handoff | `death_contact_c4 slot` |
| Chat replay | on |
| Playoff continuation | off |

Console changes are temporary. Reload the file with `dtr_config_reload` or the
plugin with `css_plugins reload DemoTracer`.

## Diagnostics

| Command | Purpose |
| --- | --- |
| `dtr_config_status` | Print config path, parse state, and effective settings. |
| `dtr_config_reload` | Reload server-local defaults. |
| `dtr_runtime` | Print plugin/runtime ABI and capability state. |
| `dtr_doctor [replay.dtr]` | Check dependencies and optional archive compatibility. |
| `dtr_bots` | List candidate bots and replay ownership. |
| `dtr_status [slot <slot>|<slot>]` | Print replay state. |
| `dtr_hider_status` | Print BotHider provider and managed-slot state. |
| `dtr_controller_status` | Print native hooks and per-slot locks. |
| `dtr_controller_perf [0|1|reset]` | Print, toggle, or reset native performance counters. |

## Known Boundaries

- Playback needs the source map and enough safe bot slots on a local Windows x64 server.
- Archived evidence does not reconstruct every CS2 physics interaction.
  Boosts, handoff transitions and grenade effects can differ.
- Other plugins writing bot movement, inventory or presentation may conflict.
- Cosmetic alignment defaults off. Missing/incompatible cosmetic
  providers disable cosmetic writes while playback continues.
- Voice requires usable netmessages; some demos contain team/default avatars.
