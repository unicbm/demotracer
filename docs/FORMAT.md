# `.dtr` Format Contract

`.dtr` is the native replay file consumed by DemoTracer's CounterStrikeSharp
loader and BotController runtime.

All values are little-endian. The format is lossless for stored replay evidence:
movement snapshots, projectile events, high-fidelity metadata, subtick records,
command-frame data, and shooting input-history data retain their original
`f32`, integer, or UTF-8 JSON values. Readers do not reconstruct snapshot
velocities from positions.

## Version Gates

- Magic: `CSDTRREC`
- Current writer format: `.dtr` v12
- Rust/Desktop and runtime readers: v12 only
- Required manifest ABI: 19
- High-fidelity metadata: writer schema 5; readers accept schemas 4 and 5
- Current BotController native ABI: 22
- Current DemoTracer companion API: 7

Manifest ABI 19 and DTR v12 are required. Readers adapt metadata schema 4 to
schema 5 once at load. Older or unversioned manifests, DTR v3–v11, and metadata
schemas 1–3 are rejected with a request to reconvert the original demo using
the current GUI. Readers do not infer missing versions.
Playback requires the matching ABI 22 bundle.

## Reader Safety Limits

The maintained Rust/Desktop and C# readers apply the same default resource
policy before allocating or decompressing file data:

| Resource | Default ceiling |
| --- | ---: |
| File bytes | 64 MiB |
| sections | 32 |
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
actually remaining in the opened file. Unknown sections count against the
same byte budgets and are skipped through a fixed-size buffer. Zstd
output is bounded by the declared decoded length; shorter or larger output is
rejected. Zstd readers allocate the validated section size, never an allocation
size supplied by the compressed frame.

## Manifest Statistics and Player Identity

Round and player `scoreboard` snapshots remain archive data for GUI analysis.
Playback reads only `files[].scoreboard.player_color`, `player_user_id` and
`player_entity_id` for player assignment and missing teammate colors. It ignores
all match totals and round scoreboards; statistics and settlement remain owned
by CS2. This does not change the manifest ABI or the `.dtr` format.

## Manifest Player Clan

Each `files[]` entry may carry `"clan": { "tag": "example", "id": 12345 }`.
The pair comes from `CCSPlayerController.m_szClan` and `m_unClanId32bit` at
the replay's live-start row. `id` is the unsigned 32-bit Steam group account ID,
not a SteamID64 or the team's `m_szClanTeamname`.

Absent or incomplete evidence produces no override. An explicit empty tag and
ID zero clears the clan. Tags preserve Unicode and are bounded to 127 UTF-8
bytes with embedded NUL rejected; this is an application safety limit.
Playback requires the matched dtr-hider managed API v3 to apply clan evidence.

## Manifest Crosshair Evidence

`files[].view.crosshair_code` preserves the demo's sharing code as an opaque
string. Both legacy `CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx` codes and the newer
46-character `CS`-prefixed codes are retained. Playback publishes the original
code through the controller's networked crosshair field; the game client owns
rendering, including its GPU crosshair path. No server-side HUD geometry or
re-encoding is involved. The existing 63-byte publication limit accommodates
both formats.

## Manifest Cosmetic Inspect Data

Manifest cosmetics may include this optional object on each
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
is not an inventory/market asset identifier.

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
| version | `u32` | Must be `12` |
| tick_rate | `f32` | Demo tickrate estimate |
| round | `u32` | `total_rounds_played` window |
| side | `u8` | `2=T`, `3=CT`, `0=unknown` |
| flags | `u32` | Reserved |
| steam_id | `u64` | Player SteamID64 |
| tick_count | `u32` | Number of replay ticks |
| subtick_count | `u32` | Number of subtick moves |
| projectile_count | `u32` | Number of replay projectile events |
| play_start_tick_index | `u32` | First tick simulated at playback start |
| metadata_json_len | `u32` | Byte length of high-fidelity metadata JSON |
| map | `u16 len + utf8` | Map name |
| player_name | `u16 len + utf8` | Demo player name |
| section_count | `u32` | Number of section records |

Non-empty replays require `play_start_tick_index < tick_count`.

Round replay files may store up to 120 seconds of same-round freeze-time
context before `play_start_tick_index`. Playback still begins at
`round_freeze_end`; the pre-start context preserves held grenade button state
without replaying arbitrarily long paused freeze time.

## Section Container

Each section is:

| Field | Type | Notes |
| --- | --- | --- |
| section_id | `u32` | Known IDs listed below |
| section_version | `u32` | Layout version for this section |
| codec | `u8` | `0 = none`; `2 = Zstandard` |
| pad | 3 bytes | Must be zero |
| flags | `u32` | Reserved |
| element_count | `u32` | Logical item count |
| uncompressed_len | `u64` | Expected decoded payload byte length |
| compressed_len | `u64` | Stored payload byte length |
| payload | bytes | Raw or compressed section payload |

Required sections:

| ID | Section | Section version | Count | Decoded payload |
| ---: | --- | ---: | ---: | --- |
| 1 | `MovementSnapshotV3` chain | `2` | `0 if tick_count == 0, else tick_count + 1` | Columnar delta-varint stream |
| 2 | tick metadata | `1` | `tick_count` | 8 bytes each |
| 5 | `SubtickMoveV3` | `1` | `subtick_count` | 28 bytes each |
| 8 | input history | `1` | `tick_count` | Variable; 16-byte tick descriptor plus 128 bytes per entry |
| 9 | source state | `2` | Logical change count | 12 bytes per compact record |

Optional sections:

| ID | Section | Section version | Count | Decoded payload |
| ---: | --- | ---: | ---: | --- |
| 3 | `ProjectileEventV4` | `1` | `projectile_count` | 48 bytes each |
| 4 | `HighFidelityMetadataV6` | `1` | `0 or 1` | UTF-8 JSON |
| 6 | `CommandFrameV1` | `2` | `tick_count` | Columnar delta-varint stream |
| 7 | `MovementExtraV1` | `1` | `tick_count` | 48 bytes each |

Unknown section IDs must be skipped using `compressed_len`. Duplicate known
sections are invalid. Missing required sections are invalid. Optional
tick-aligned sections may be omitted; when present, their `element_count` must
equal `tick_count`.
Projectile and metadata sections are required when their header counts are nonzero.

The v12 writer uses Zstandard level 9 with independent, dictionary-free sections.
It stores a section uncompressed when compression would not reduce its size,
including empty sections. Brotli is unsupported. The section codec does not
change any replay values.

### Input-history section

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

The Windows runtime validates but does not inject input history into the
engine-owned command graph. Playback/prefetch discard decoded arrays after
validation; inspection reads retain them. `target_ent_index` refers to demo
entities and cannot be used as a live server identity.

## Columnar Delta-Varint Sections

Native BotController ABI 22 uses 228-byte replay ticks, including a reserved
36-byte event tail. Every tail field must be zero; native loading rejects nonzero
payloads because native weapon-drop recording/replay is unsupported. This is an
in-memory API layout, not the DTR disk layout.
DTR gameplay events remain in high-fidelity metadata and are executed by the
managed replay layer; DTR readers initialize the native event tail to zero to
preserve this contract.

Section version 2 stores `MovementSnapshotV3` and `CommandFrameV1` values
bit-exactly.

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
8-bit width. Native alignment padding is not stored in columnar payloads and is restored
as zero in native structs. For v2 sections, `uncompressed_len` is the exact
column stream length rather than `element_count × struct_size`.

## Tick Metadata

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

Demo rows are post-command observations. For adjacent rows `i` and `i + 1`,
the converter takes movement/source state from row `i`, and command planes,
subticks and input history from row `i + 1`. Projectile births in row `i + 1`
belong to replay tick `i`. This keeps jump and attack edges in the simulation
that produces `post`; pairing row `i` inputs with row `i` state replays them late.

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

The converter writes `0xff` for unknown `actual_move_type`. Playback derives
it through native `SetMoveType`. Source-state presence governs duck-state
restoration.

`buttons`, `buttons1`, and `buttons2` store
`CInButtonStatePB.buttonstate1`, `buttonstate2`, and `buttonstate3` respectively.
They are three bit planes of one `EInButtonState` code, not interchangeable
masks. `buttonstate1` is the held-at-command-end plane; the transition planes
must remain separate so releases and short press-release sequences survive.
For raw planes `state1`, `state2`, and `state3`, semantic masks are decoded as
`held = state1`, `pressed = state3 | (state1 & state2)`, and
`released = state3 | (~state1 & state2)`. If only adjacent held masks are
available, the canonical loss-limited reconstruction is `state1 = current`,
`state2 = current ^ previous`, and `state3 = 0`; a same-command press-release
cannot be reconstructed from held snapshots alone.

Silent reload uses the existing reload input bit (`1 << 13`): a sustained hold
and its eventual release must survive as command planes and subtick edges,
including a press and release within one command. The converter does not
reduce reload input to a `weapon_reload` event or invent a fixed reload duration.
The live engine decides reload speed and sound; archived ammo/reload flags do
not overwrite that state. Starting playback in the middle of a reload cannot
reconstruct the missing earlier input from a reload flag alone.

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

Source order is preserved for accepted subtick moves. DTR preserves every
finite `when < 1` value, including negative engine-authored phases for buffered
input events that predate the current command window. The file reader and
native staging buffers retain those signed values unchanged. Live playback
projects negative phases to `0` only when building CS2's `CSubtickMoveStepPB`,
whose accepted `when` domain is `[0, 1)`; the stored evidence is not rewritten.

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
| buttons | `u64[3]` | buttonstate1/2/3; present when bit `4` is set |
| mouse_dx | `i32` | Present with mouse bit `5` |
| mouse_dy | `i32` | Present with mouse bit `5` |
| weapon_select | `i32` | Raw demo command value; present when bit `6` is set |
| fields | `u32` | Presence bitset |
| left_hand_desired | `u8` | Present when bit `7` is set |
| pad | 3 bytes | |

### `MovementExtraV1`

Optional movement extras are validated, but the current runtime
does not consume them. Playback discards the decoded values; native compatibility
entry points validate these arguments without retaining a second copy.

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

Section ID `4` contains optional UTF-8 JSON metadata. Schemas `4` and `5` are accepted.

The top-level object contains:

- `schema_version`: current metadata schema is `5`.
- `round_start_balance`: optional demo-backed `m_iAccount` value from the first
  player row at or after the source round's live-start tick. Absence means no
  balance evidence and must never be interpreted as zero.
- `events`: player-scoped high-fidelity events.
- `inventory_snapshots`: inventory state after weapon, armor, helmet or defuser
  changes, including freeze time. Playback initializes from the snapshot at or
  before its actual start cursor, then grants only newly acquired equipment at
  its recorded time. Later snapshots do not refill unrelated utility, undo
  damage, or remove human-introduced items.
- `projectiles`: player-scoped projectile effect metadata. This supplements
  the fixed-size `ProjectileEventV4` section without changing its binary
  layout.

Event `kind` values include `bomb_initial_owner`, `item_drop`, `item_pickup`,
`item_transfer`, `bomb_drop`, `bomb_pickup`, `bomb_beginplant`, `bomb_planted`,
`weapon_fire`, `player_hurt`, `player_death`, `round_start`, and
`round_freeze_end`.

Combat events are record-only for now: the CSS plugin loads them for diagnostics
and future behavior, but does not force damage or death.

Schema 5 compiles inventory acquisitions during export. Each `weapon_def_counts`
entry includes `acquired`, true only when its count increased since the previous
snapshot. `gear_acquired` is a bit mask: armor increase `1`, helmet acquisition
`2`, defuser acquisition `4`. Full counts and gear remain checkpoints for starting
or seeking; normal playback consumes acquisition flags and cancels pending grants
when later counts fall. It never repairs damage or refills unchanged utility.
For schema 4, readers calculate these flags once from successive inventory
snapshots per player and normalize the in-memory metadata to schema 5. Files
are not rewritten. Playback consumes the same plan for both schemas and does
not infer utility acquisitions from pickup events.

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

## Source State Changes

Section 9, version 2, stores compact source-state records. Field IDs and scalar
types are specified in `server/runtime/common/contracts/replay-source-fields.v1.json`.
Records sort by `(tick_index, field_id)`, with no duplicate keys. The section is
required even when empty. Absent values require zero bits; present values preserve
exact float/integer bits, including a real zero. Floats must be finite and booleans
must be zero or one. All changes refer to a replay pre tick.

Native playback indexes these changes for start/seek/loop initialization and the
first use of a newly created or acquired weapon. It does not write them on every
tick or reset an existing weapon when switching back to it. Player tickbase and
ServerInfo tick interval establish source time; positive event/deadline clocks
are rebased to live simulation time, while inactive sentinel values are preserved.
Aim-punch base states belong to AimPunchServices and are not added to command view
angles. Stop and handoff preserve native motion and weapon state.

Boundary writes notify native entity replication once, including nested services.
Weapons are selected through the native deploy path before restoring their attack
deadlines. Writers omit clip counts, reserve ammo, reload flags and the unused
ServerTick field. Playback never restores
ammunition, including during initial start or weapon replacement.
The live server owns ammunition capacity, supply and reload rules. Source and
live tick intervals must match; playback does not resample state clocks.

A discontinuous start already on a ladder requires a valid recorded contact
normal. When the demo omits it, start before mounting the ladder so native movement
can establish contact. Playback rejects that unsupported start instead of using a
zero or stale plane. The ladder surface index is not a substitute for its normal.

Manifest ABI 19 requires the matching v12 reader and BotController ABI 22
source-state capability (bit 17).

### Compact Source State (section 9, version 2)

Source-state `element_count`
is the number of logical changes, including clock-run expansion; the number of
stored records is `uncompressed_len / 12`. Empty sections have both counts zero.
The body contains two little-endian u32 columns followed by value byte planes:

1. Tick-index deltas from the preceding stored record (initial index zero).
2. Descriptors: bits 0..6 field ID, bit 7 presence, bits 8..31 run length minus one.
3. Value-bit XORs against the preceding stored value of the same field (initial
   bits zero), grouped by ascending field ID. Each group stores four byte planes
   from least to most significant byte, preserving record order within the field.
   Group lengths are derived from the descriptor column. The XOR baseline is the
   preceding record's initial value, even when that record represents a run.

Only a present PlayerTick (ID 1) may have a run longer than one tick. It represents
consecutive changes at `start_tick + i` with value `start_bits + i` modulo 2^32.
Writers merge only observed consecutive increments; gaps, pauses, jumps and
absence retain their exact meaning. Runs must fit inside the replay, must not
overlap, and must sum to `element_count`. Stored records retain strict
`(tick_index, field_id)` ordering. Each decoded initial value obeys the presence
and type rules above. The final value of a run remains in force until the next change.

The converter and managed/native playback retain compact runs in memory. The
16-byte native change structure assigns `present`
bit 0 to presence and bits 1..24 to run length minus one (bits 25..31 must be zero).
Non-clock fields retain 0/1. Native seek/start queries calculate only the requested
clock value; neither loading nor continuous playback expands or rewrites it per
tick.

Field IDs 0, 52, 53, 54, 65 and 66 are reserved and omitted from exports. PlayerTick and all other source evidence remain available at
every recorded tick. No float quantization or snapshot/subtick decimation is used.
