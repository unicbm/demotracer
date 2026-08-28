# `.dtr` Format Contract

`.dtr` is the native replay file consumed by DemoTracer's CounterStrikeSharp
loader and BotController runtime.

All values are little-endian. The format is lossless for stored replay evidence:
movement snapshots, projectile events, high-fidelity metadata, subtick records,
command-frame data, and shooting input-history data retain their original
`f32`, integer, or UTF-8 JSON values.

## Version Gates

- Magic: `CSDTRREC`
- Current writer format: `.dtr` v9
- Runtime reader support: v3 through v9
- Current manifest ABI: 17
- Current BotController native ABI: 18
- Current DemoTracer companion API: 7

Compatibility notes:

- v3 files do not contain projectile metadata.
- v3/v4 files use `play_start_tick_index = 0`.
- v3-v5 files do not contain high-fidelity metadata JSON.
- v7+ files require the matching playback bundle with BotController native ABI
  16 and extended replay capability.
- v8 keeps the v7 section container and changes only the snapshot and command
  frame section payloads to bit-exact columnar delta-varint layouts.
- v9 adds per-command `CSGOUserCmdPB.input_history` and attack start indexes.
  Playback rebases stored absolute history ticks to the live command tick;
  demo entity indexes are retained as evidence but are not injected.

## Reader Safety Limits

The maintained Rust/Desktop and C# readers apply the same default resource
policy before attacker-controlled allocation or Brotli decoding. These are
reader safety limits, not a change to the binary layout or ABI:

| Resource | Default ceiling |
| --- | ---: |
| File bytes | 64 MiB |
| v7+ sections | 32 |
| Compressed bytes per section | 48 MiB |
| Total compressed section bytes | 64 MiB |
| Decoded bytes per section | 48 MiB |
| Total decoded section bytes | 64 MiB |
| Replay ticks | 32,768 |
| Subtick moves | 1,179,648 |
| Subtick moves per tick | 36 |
| Projectile events | 4,096 |
| High-fidelity metadata JSON | 8 MiB |

The tick ceiling still permits about 8.5 minutes at 64 tick or 4.25 minutes at
128 tick for a single player-round replay.

File-backed readers also compare every declared payload length with the bytes
actually remaining in the opened file. Unknown v7+ sections count against the
same byte budgets and are skipped through a fixed-size buffer. A Brotli stream
that produces more than its declared decoded length is rejected immediately.

## Manifest Cosmetic Inspect Data

Manifest ABI 17 cosmetics may include this additive, optional object on each
weapon, knife, or glove cosmetic:

```json
{
  "inspect": {
    "command": "csgo_econ_action_preview <payload-hex>",
    "steam_url": "steam://run/730/en/+csgo_econ_action_preview%20<payload-hex>"
  }
}
```

`command` and `steam_url` contain the same preview payload and are emitted
together when the cosmetic has a usable item definition, paint kit, seed, and
wear. The URL is the command wrapped in Steam's CS2 launch URI. The uppercase
payload is a deterministic CS2 `CEconItemPreviewDataBlock` protobuf with the
native leading byte and xCRC trailer. It contains appearance evidence only and
is not an inventory/market asset identifier. Because this is additive derived
JSON that old readers ignore, it does not change the `.dtr` format or manifest
ABI 17.

Glove evidence is retained when the demo exposes an exact item definition,
paint kit, and wear but omits the texture seed. Such entries carry
a deterministic fallback `seed` in the CS2 range plus `"seed_known": false`.
The fallback is stable for the same player, side, glove, and wear so replays do
not change patterns between rounds. The evidence UI still reports the seed as
unresolved, while playback writes the fallback after clearing prior glove
attributes. No inspect payload is generated for partial glove evidence.

## Header

| Field | Type | Notes |
| --- | --- | --- |
| magic | 8 bytes | `CSDTRREC` |
| version | `u32` | Current writer emits `9` |
| tick_rate | `f32` | Demo tickrate estimate |
| round | `u32` | `total_rounds_played` window |
| side | `u8` | `2=T`, `3=CT`, `0=unknown` |
| flags | `u32` | Reserved |
| steam_id | `u64` | Player SteamID64 |
| tick_count | `u32` | Number of replay ticks |
| subtick_count | `u32` | Number of subtick moves |
| projectile_count | `u32` | Number of replay projectile events |
| play_start_tick_index | `u32` | First tick simulated at playback start; v5+ |
| metadata_json_len | `u32` | Byte length of high-fidelity metadata JSON; v6+ |
| map | `u16 len + utf8` | Map name |
| player_name | `u16 len + utf8` | Demo player name |
| section_count | `u32` | v7+ only; number of section records |

For v3-v6 legacy files, the header continues after `player_name` with
`codec: u8`, `body_uncompressed_len: u64`, `body_compressed_len: u64`, followed
by one Brotli-compressed legacy body.

Round replay v5+ files may store up to 10 seconds of same-round freeze-time
context before `play_start_tick_index`. Playback still begins at
`round_freeze_end`; the pre-start context preserves held grenade button state
without replaying arbitrarily long paused freeze time.

## v7+ Section Container

Each v7+ section is:

| Field | Type | Notes |
| --- | --- | --- |
| section_id | `u32` | Known IDs listed below |
| section_version | `u32` | Layout version for this section |
| codec | `u8` | `0 = none`; readers may also accept `1 = Brotli` |
| pad | 3 bytes | Ignored by readers |
| flags | `u32` | Reserved |
| element_count | `u32` | Logical item count |
| uncompressed_len | `u64` | Expected decoded payload byte length |
| compressed_len | `u64` | Stored payload byte length |
| payload | bytes | Raw or compressed section payload |

Required sections:

| ID | Section | Section version | Count | Decoded payload |
| ---: | --- | ---: | ---: | --- |
| 1 | `MovementSnapshotV3` chain | `1` in v7; `2` in v8+ | `0 if tick_count == 0, else tick_count + 1` | v1: 92 bytes each; v2: columnar delta-varint stream |
| 2 | tick metadata | `1` | `tick_count` | 8 bytes each |
| 5 | `SubtickMoveV3` | `1` | `subtick_count` | 28 bytes each |
| 8 | input history (required in v9) | `1` | `tick_count` | Variable; 16-byte tick descriptor plus 128 bytes per entry |

Optional sections:

| ID | Section | Section version | Count | Decoded payload |
| ---: | --- | ---: | ---: | --- |
| 3 | `ProjectileEventV4` | `1` | `projectile_count` | 48 bytes each |
| 4 | `HighFidelityMetadataV6` | `1` | `0 or 1` | UTF-8 JSON |
| 6 | `CommandFrameV1` | `1` in v7; `2` in v8+ | `tick_count` | v1: 68 bytes each; v2: columnar delta-varint stream |
| 7 | `MovementExtraV1` | `1` | `tick_count` | 48 bytes each |

Unknown section IDs must be skipped using `compressed_len`. Duplicate known
sections are invalid. Missing required sections are invalid. Optional
tick-aligned sections may be omitted; when present, their `element_count` must
equal `tick_count`.

### v9 input-history section

For each replay tick, the payload stores a 16-byte descriptor followed
immediately by that tick's entries:

`source_client_tick: i32`, `attack1_start_history_index: i32`,
`attack2_start_history_index: i32`, `num_entries: u32`.

Each entry is 128 bytes and starts with a `u32 fields` presence mask, followed
by view angles, render/player tick and fraction fields, client/server/player
interpolation fields, frame and target indexes, shoot position, and the three
target check vectors in protobuf field order. At most 64 entries are allowed
per tick. Attack indexes are `-1` or index the same tick's retained entries.
All stored floats must be finite. Converter output retains only valid entries
referenced by `attack1_start_history_index` or `attack2_start_history_index`,
deduplicates shared references, and remaps both indexes to the retained order.

The matched Windows runtime currently retains this evidence in the file but
does not advertise or perform input-history injection. `CSGOUserCmdPB` and its
entries are engine-owned; even in-place protobuf mutation can corrupt the live
command ring across the module ABI boundary. When the capability is absent,
the managed loader uses the extended replay entry point and leaves the entire
live input-history graph untouched. The section remains available for a future
engine-owned injection path. `target_ent_index` additionally requires live
identity remapping because demo entity indexes are not stable on the replay
server.

## v8 Columnar Delta-Varint Sections

Section version 2 is bit-exact and lossless. It changes storage only; decoded
`MovementSnapshotV3` and `CommandFrameV1` values are identical to v7 values.

Each logical field is stored as one complete time-series column. Array fields
use component order. Snapshot columns follow this order:

`origin[3]`, `velocity[3]`, `angles[3]`, `entity_flags`, `move_type`,
`buttons`, `buttons1`, `buttons2`, `duck_amount`, `duck_speed`,
`ladder_normal[3]`, `ducked`, `ducking`, `desires_duck`,
`actual_move_type`.

Command-frame columns follow this order:

`forward_move`, `left_move`, `up_move`, `pitch`, `yaw`, `roll`, `buttons`,
`buttons1`, `buttons2`, `mouse_dx`, `mouse_dy`, `weapon_select`, `fields`,
`left_hand_desired`.

For every column:

1. An empty column writes no bytes.
2. The first value is written in its original little-endian width.
3. Each later value computes `delta = current_bits - previous_bits` modulo the
   field width.
4. Interpret `delta` as signed two's-complement, ZigZag-encode it, then write
   canonical unsigned LEB128.

`f32` columns operate on the original IEEE-754 `to_bits()` value, not on a
numeric approximation. Signed integer columns operate on their raw bit pattern.
The five one-byte snapshot columns and `left_hand_desired` use the same rule at
8-bit width. V1 alignment padding is not stored in v2 payloads and is restored
as zero in native structs. For v2 sections, `uncompressed_len` is the exact
column stream length rather than `element_count × struct_size`.

## Legacy v3-v6 Body

After legacy body decompression, the layout is:

| Part | Count | Bytes Each |
| --- | ---: | ---: |
| `MovementSnapshotV3` | `0 if tick_count == 0, else tick_count + 1` | 92 |
| tick metadata | `tick_count` | 8 |
| `ProjectileEventV4` | `projectile_count` | 48 |
| `HighFidelityMetadataV6` | `metadata_json_len` | UTF-8 JSON |
| `SubtickMoveV3` | `subtick_count` | 28 |

Tick metadata is:

| Field | Type |
| --- | --- |
| weapon_def_index | `i32` |
| num_subtick | `u32` |

Reconstruct replay ticks as:

- `tick[i].pre = snapshots[i]`
- `tick[i].post = snapshots[i + 1]`
- `tick[i].weapon_def_index = metadata[i].weapon_def_index`
- `tick[i].num_subtick = metadata[i].num_subtick`

The sum of all `num_subtick` values must equal header `subtick_count`.

## Structs

### `MovementSnapshotV3`

This layout is 92 bytes with `Pack=4`.

| Field | Type |
| --- | --- |
| origin | `f32[3]` |
| velocity | `f32[3]` |
| angles | `f32[3]` pitch/yaw/roll |
| entity_flags | `u32` |
| move_type | `u8` |
| pad | 3 bytes |
| buttons | `u64` |
| buttons1 | `u64` |
| buttons2 | `u64` |
| duck_amount | `f32` |
| duck_speed | `f32` |
| ladder_normal | `f32[3]` |
| ducked | `u8` |
| ducking | `u8` |
| desires_duck | `u8` |
| actual_move_type | `u8` |

### `SubtickMoveV3`

| Field | Type |
| --- | --- |
| when | `f32` |
| button | `u32` |
| pressed | `f32` |
| analog_forward | `f32` |
| analog_left | `f32` |
| pitch_delta | `f32` |
| yaw_delta | `f32` |

### `ProjectileEventV4`

| Field | Type | Notes |
| --- | --- | --- |
| tick_index | `u32` | |
| weapon_def_index | `i32` | |
| kind | `u8` | `0=unknown`, `1=smoke`, `2=flash`, `3=he`, `4=molotov/incendiary`, `5=decoy` |
| pad | 3 bytes | |
| initial_position | `f32[3]` | |
| initial_velocity | `f32[3]` | |
| detonation_position | `f32[3]` | |

### `CommandFrameV1`

| Field | Type | Notes |
| --- | --- | --- |
| forward_move | `f32` | Present when bit `0` is set |
| left_move | `f32` | Present when bit `1` is set |
| up_move | `f32` | Present when bit `2` is set |
| view_angles | `f32[3]` | pitch/yaw/roll; present when bit `3` is set |
| buttons | `u64[3]` | buttonstate0/1/2; present when bit `4` is set |
| mouse_dx | `i32` | Present with mouse bit `5` |
| mouse_dy | `i32` | Present with mouse bit `5` |
| weapon_select | `i32` | Raw demo command value; present when bit `6` is set |
| fields | `u32` | Presence bitset |
| left_hand_desired | `u8` | Present when bit `7` is set |
| pad | 3 bytes | |

### `MovementExtraV1`

| Field | Type |
| --- | --- |
| fields | `u32` |
| jump_pressed_time | `f32` |
| last_duck_time | `f32` |
| last_actual_jump_press_tick | `i32` |
| last_actual_jump_press_frac | `f32` |
| last_usable_jump_press_tick | `i32` |
| last_usable_jump_press_frac | `f32` |
| last_landed_tick | `i32` |
| last_landed_frac | `f32` |
| last_landed_velocity | `f32[3]` |

## High-Fidelity Metadata

v6+ files may include a UTF-8 JSON blob. In v3-v6 legacy files it appears after
projectile events and before subtick moves inside the Brotli body. In v7+ it is
section ID `4`.

The top-level object contains:

- `schema_version`: current metadata schema is `4`.
- `round_start_balance`: optional demo-backed `m_iAccount` value from the first
  player row at or after the source round's live-start tick. Absence means no
  balance evidence and must never be interpreted as zero.
- `events`: player-scoped high-fidelity events.
- `inventory_snapshots`: inventory state after inventory changes.
- `projectiles`: player-scoped projectile effect metadata. This supplements
  the fixed-size `ProjectileEventV4` section without changing its binary
  layout.

Event `kind` values include `bomb_initial_owner`, `item_drop`, `item_pickup`,
`item_transfer`, `bomb_drop`, `bomb_pickup`, `bomb_beginplant`, `bomb_planted`,
`weapon_fire`, `player_hurt`, `player_death`, `round_start`, and
`round_freeze_end`.

Combat events are record-only for now: the CSS plugin loads them for diagnostics
and future behavior, but does not force damage or death.

Projectile metadata entries contain:

| Field | Type | Notes |
| --- | --- | --- |
| tick_index | `u32` | Replay tick index of the throw event |
| tick | `i32` | Original demo tick of the throw event |
| kind | string | `smoke`, `flash`, `he`, `molotov`, `decoy`, or `unknown` |
| weapon_def_index | `i32` | Demo weapon definition index when known |
| effect_tick_index | `u32?` | Replay tick index of the matched effect event |
| effect_tick | `i32?` | Original demo tick of the matched effect event |
| effect_position | `f32[3]` | Demo effect position, such as inferno start burn |
| effect_source | string | Source event/property used for the effect position |
| effect_confidence | `f32` | Converter confidence in the effect match |

## Parser Checklist

1. Read and validate magic `CSDTRREC`.
2. Require `version == 9` for current writer output, or accept `version == 3`
   through `8` for backward compatibility.
3. Read `tick_count`, `subtick_count`, `projectile_count`,
   `play_start_tick_index`, `metadata_json_len`, `map`, and `player_name`. For
   v3, treat `projectile_count` as `0`; for v3/v4, treat
   `play_start_tick_index` as `0`; for v3-v5, treat `metadata_json_len` as `0`.
4. For v7+, read `section_count`, parse known sections, and skip unknown
   sections using `compressed_len`.
5. For v7+, require snapshot, tick metadata, and subtick sections; require
   projectile/high-fidelity sections when their header counts are non-zero.
   Require snapshot/command section version 1 for v7 and version 2 for v8+.
   For v9, also require the input-history section and validate its per-tick
   counts and attack indexes.
6. For v3-v6, require legacy `codec == 1`, verify legacy body length, then
   Brotli-decompress exactly `body_compressed_len` bytes.
7. Rebuild ticks from the snapshot chain and metadata.
8. Sum all tick `num_subtick` values and verify it equals `subtick_count`.
9. If `metadata_json_len > 0`, parse exactly that many bytes as UTF-8 JSON.
10. For non-empty replays, require `play_start_tick_index < tick_count`.
