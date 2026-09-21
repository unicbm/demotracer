# Playback Command Reference

Run `dtr_*` commands from the server console or the local listen-server host.
Remote players cannot use them. Prefer the high-level `dtr_go` commands; manual
slot commands are for development and diagnostics.

## Quick Start

```text
css_plugins reload DemoTracer
dtr_runtime
dtr_config_status
dtr_preset 0x15; dtr_go seq "<manifest.json>" 0
```

`dtr_go` validates and arms the plan, then runs `mp_restartgame 1`. `dtr_arm`
uses the same plan without restarting.

## Playback Plans

| Command | Purpose |
| --- | --- |
| `dtr_go seq <manifest.json> [from_source_round]` | Replay the manifest from a source round onward. |
| `dtr_go round <manifest.json> <source_round>` | Replay exactly one source round. |
| `dtr_arm seq|round ...` | Arm the same plan without restarting the server round. |
| `dtr_playoff <true|false>` | Continue an exhausted sequence with SteamID-matched rifle/sniper openings. |
| `dtr_retain <t-order-code> <ct-order-code>` | Set per-side bot retention priority for the next manifest plan; use `clear` to reset. |
| `dtr_stop sequence|replay|slot <slot>|all` | Stop the selected scheduler or replay state. |

`from_source_round` is a demo round index. Mixed playoff rounds do not replay
match statistics, chat, or voice metadata because they can draw the two sides
from different source rounds. Recorded teammate colors remain available.

Continuation, including overtime, selects the long-gun pool from recorded
live-start inventories: every recorded player on the selected side must have a
rifle or sniper rifle. Pistol rounds, SMG/shotgun/machine-gun or mixed pools, and
missing loadout evidence are excluded. Cash balances, economy labels, and the
balance-alignment setting do not affect selection. Both teams need at least two
eligible source rounds on each side; both side assignments are prefetched so an
overtime team swap can use the matching sources immediately.

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
change projectiles, handoff, crosshair, match presentation, partial replay, or
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

Advanced projectile diagnostics:

Projectile alignment is uniform for every grenade kind: DemoTracer applies the
recorded initial position and velocity once through CS2's entity teleport path
before the projectile's first native physics step. The spawn listener records
the entity; the synchronous physics hook applies the birth state without
waiting another frame or shifting any timer. `dtr_runtime` reports the hook's
actual availability. Flight, collision, detonation, and effect propagation remain
owned by CS2. Effect positions are diagnostic evidence only; playback never teleports a
projectile to an effect point or forces detonation.

| Command | Purpose |
| --- | --- |
| `dtr_projectile_align_log [clear|all|molotov|fire]` | Print or clear alignment diagnostics. |

### Identity and Presentation

| Command | Purpose |
| --- | --- |
| `dtr_replay_identity <off|name|steam|avatar>` | Select the bot identity lease. `full` aliases `avatar`. |
| `dtr_match <status|off|scoreboard|full>` | Control default-off local scoreboard/team presentation. |
| `dtr_match scoreboard <on|off>` | Toggle scoreboard presentation only. |
| `dtr_partial <0|1>` | Allow fewer replay bots than manifest players. |

Use identity `name` or `off` when the original demo player is also connected to
the local server. `steam` is the normal mode. `avatar` additionally applies a
valid manifest PNG when available.

Replacement DTR bots with an unassigned TAB/HUD teammate color are repaired
after spawning, independently of match-statistics synchronization. A recorded
color is preferred; if it is occupied, the bot takes a free team color. Existing
colors and human players are left unchanged. Missing recorded color evidence
does not invent an override.

### Cosmetics

```text
dtr_cosmetics [status|off|weapons|basic|full]
dtr_cosmetics <weapons|knives|gloves|names|agents|stickers|charms|preserve_native> <on|off>
```

`weapons` enables demo-backed paints and custom names for ordinary weapons;
`full` additionally enables stickers and charms. Those optional fields remain
default-off and are claimed only when the demo contains positive evidence for
that weapon definition.

The playback bundle includes the matched BotRandomizer v2 replay-plan provider.
DemoTracer submits normalized demo evidence as parameters only; BotRandomizer
is the sole cosmetic entity writer. Weapons and knives are prepared before
`GiveNamedItem` constructs them, while Agent, gloves, and music kits are applied
from BotRandomizer's next-spawn lifecycle. Missing Agent evidence preserves the
engine-selected model. DemoTracer never rebuilds or hot-repairs cosmetic
entities. Review the GSLT warning in the root README before enabling cosmetics.

The bundled BotRandomizer also accepts the Bot Improver 1.4.4 Panel command
from the server console or a server `.cfg`:

```text
bot_randomizer github.com/ed0ard/CS2-Bot-Randomizer <weapons|knives|gloves|agents|music|stickers|charms> <0|1>
```

All seven randomization switches default to enabled. They accept `on/off`,
`yes/no`, and `true/false` as well. Positive DTR evidence takes precedence over
these randomization defaults; missing Agent evidence retains DTR's existing
preserve-engine-default policy. Without a DTR claim, a disabled category adds
no randomized cosmetics. Disabling agents selects the normal team model, and
disabling music clears the music kit at the next spawn or MVP event.
The agent option also applies to round-start team intros; an explicit DTR agent
keeps its recorded item ID, while DTR's preserve-engine-default policy leaves
the intro agent untouched.

Settings are read at natural spawn, item construction, and the existing
wearable/pickup callbacks. Commands do not rebuild live inventory, reroll
cached selections, revoke DTR plans, or unload a plugin. Existing weapons keep
their applied cosmetics until replaced. Settings survive map changes; after a
plugin reload, execute the server configuration again to restore its switches.
Install one `BotRandomizer` provider in the normal plugin directory.

### Handoff

```text
dtr_handoff <off|death|contact|death_or_contact|death_contact_c4> [slot|all]
dtr_handoff_360 [0|1] [range] [los|nolos]
```

The default is `death_contact_c4 slot`: release an individual slot on death or
contact; C4 planted releases all active slots. The 360 option extends contact
detection around the replay bot and can use an optional RayTrace provider for
line-of-sight filtering.

### Chat and Voice

| Command | Purpose |
| --- | --- |
| `dtr_chat_auto [status|on|off]` | Toggle timed manifest chat replay; default on. |
| `dtr_chat_test <loaded|any|slot> [all|team] <message>` | Send one diagnostic bot chat line. |
| `dtr_voice_auto [status|on|off]` | Toggle automatic `.dtv` playback. |
| `dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]` | Test one sidecar with a fixed sender. |
| `dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]` | Test multi-speaker mapping. |
| `dtr_voice_stop` | Stop voice test playback. |

When the demo contains usable voice frames, the converter writes
`voice/roundXX.dtv` beside the manifest archive. Keep the sidecar with its
matching manifest; copying only `.dtr` files is insufficient. DemoTracer maps
speaker XUIDs to loaded replay bots. Observers hear all replay voice, human T/CT
players hear their own team, and bots and HLTV are not recipients. Missing or
unusable voice evidence simply produces no sidecar.

## Manual Replay Control

These commands bypass some sequence lifecycle handling and are intended for
debugging.

| Command | Purpose |
| --- | --- |
| `dtr_load round <manifest.json> <source_round>` | Load one round onto safe bot slots. |
| `dtr_load slot <slot> <path.dtr>` | Load one raw replay without manifest-only metadata. |
| `dtr_play loaded [loop:0|1]` | Start every loaded slot immediately. |
| `dtr_play slot <slot> [loop:0|1]` | Start one loaded slot. |
| `dtr_unload <slot>` | Unload one slot and clear its metadata. |
| `dtr_kick <exact-name>|slot <slot>|sid <steamid64>` | Release and kick a replay bot safely. |

Looping waits for all still-controlled looping slots to finish, then restarts
them together from the live-play index. Each iteration refreshes event and
projectile cursors and the replay loadout. Stopped or handed-off slots stay out
of subsequent iterations. A complete loaded-round loop also restarts automatic
chat and voice; manual slot loops do not start round media, and an independent
voice test keeps playing.

## Configuration

Optional defaults live in `demotracer.config.json` next to `DemoTracer.dll`.
Start from the packaged `demotracer.config.example.json`. The parser accepts
JSON comments and trailing commas.

| Setting | Default |
| --- | --- |
| Identity | `steam` |
| Fidelity | weapons, projectiles, crosshair, and left-hand on |
| Round-start balance | off |
| Match presentation | off |
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
| `dtr_doctor [manifest.json]` | Check dependencies and optional manifest compatibility. |
| `dtr_bots` | List candidate bots and replay ownership. |
| `dtr_status [slot <slot>|<slot>]` | Print replay state. |
| `bh_status` | Print BotHider provider and managed-slot state. |
| `bc_status` | Print native hooks and per-slot locks. |
| `bc_perf [0|1|reset]` | Print, toggle, or reset native performance counters. |

## Compatibility Aliases

Kept for existing scripts; new tooling should use the commands above.

| Alias | Preferred command |
| --- | --- |
| `dtr_seq_restart` | `dtr_go seq` |
| `dtr_round_restart` | `dtr_go round` |
| `dtr_run_manifest` | `dtr_arm seq` |
| `dtr_arm_round` | `dtr_arm round` |
| `dtr_stop_sequence`, `dtr_stop_all` | `dtr_stop ...` |
| `dtr_load_round`, `dtr_play_loaded` | `dtr_load round`, `dtr_play loaded` |
| `dtr_weapon_align`, `dtr_projectile_align`, `dtr_crosshair_align`, `dtr_left_hand_desired` | `dtr_align ...` |
| `dtr_cosmetic_align`, `dtr_sticker_align`, `dtr_charm_align` | `dtr_cosmetics ...` |
| `dtr_set ...` | Dedicated identity, alignment, handoff, or partial command |

## Known Boundaries

- The bundled BotController `!record` / `!replay` commands capture and replay
  public motion recordings. They do not capture or execute weapon-drop events.
  Older JSON recordings with a nonzero native event tail are rejected with an
  unsupported-event message. DTR gameplay events keep their existing executor.
- Playback targets local Windows x64 servers with the source map and enough safe
  bot slots; it is not intended for matchmaking.
- `.dtr` preserves its recorded evidence but is not a complete reconstruction
  of every CS2 command or physics interaction.
- Plugins that control bot AI, buying, inventory, movement, identity, or
  presentation can conflict with replay state.
- Boosts and player-on-player movement can differ when a human replaces a
  recorded participant; complex handoff transitions remain best-effort.
- Scoreboard alignment is best-effort and default-off. Some demos contain team
  or default avatars rather than true player avatars.
- Projectile alignment is not exact for every throw, especially molotov and
  incendiary effects. Uncertain evidence stays on native CS2 behavior.
- Voice requires usable voice netmessages. Sticker and keychain transforms
  cannot reproduce every CS2 presentation detail exactly.
- Cosmetic writes are explicit opt-in and positive-evidence-only. A missing or
  incompatible bundled provider makes DemoTracer fail closed without stopping
  playback; there is no managed direct-write fallback.
