# Vendored demoparser notes

This repository vendors the minimal Rust crates used by the converter:

- `src/parser`
- `src/csgoproto`

Large upstream test demos, JavaScript packages, and the full GameTracking-CS2 checkout are intentionally excluded.

Signon parsing retains `svc_ServerInfo.host_name` as `server_info_host_name`
alongside the unchanged demo file header. Platform detection can use the
recorded host when the file header contains a generic server name.

ServerInfo also supplies `server_tick_interval`; `game_time` uses that interval
and `server_tick` exposes the integer network clock. Missing timing remains
missing rather than being reconstructed from a fixed 64 Hz assumption.
`GameTick_t` uses the signed-varint decoder, including negative sentinels, so
movement, aim-punch and weapon deadlines retain their original tick values.

The vendored build scripts use the generated `protobuf.rs` and `maps.rs` files already present in this tree, so normal converter builds do not clone GameTracking-CS2 or rewrite vendored source files.

Local button-map maintenance adds JUMP/DUCK properties and aligns WALK,
SCOREBOARD, and ZOOM with the maintained CS2 usercmd button definitions.
The mapping regression test lives in `src/parser/src/maps.rs`.

Local parser maintenance removes the unused private channel-based second-pass
implementation and its duplicate fallback. The active fullpacket-based Rayon
and single-threaded parsing paths remain the supported implementations.

Local performance maintenance decodes codegen-delta usercmds directly, staging
sparse list updates before committing their baselines. History still exports the
full restored state; subticks export only updated entries. The previous decoder
remains a test oracle. Normal player collection compiles a dense output-column
layout once; velocity, query, duplicate-column and per-player modes retain the
legacy collection path. Inventory projection borrows cached snapshots, and
disabled packet messages skip their payloads without copying. None of these
changes removes cross-fullpacket continuity requirements for stateful properties.

Deferred scalar collection records schema-proven scalar changes and explicitly
typed usercmd scalars with entity generations and output-row sequence. Linear CPU
cursors write final typed columns without repeated per-row property lookups.
Other properties keep their existing semantics; row-local entity links and
inventory scratch buffers are reused. Inventory cache invalidation follows
weapon slots, life/ammo/owner state, econ changes, entity lifecycle and roster
changes, including the intermediate player-connect event callback. Immutable
history, subtick and sticker snapshots share backing storage; dictionary string
columns retain the same serialized strings and nulls. Aggregate diagnostics
separate decompression, entity decoding, collection and scalar finalization;
optional sampled property timings help locate remaining getter/allocation costs.

Scalar streams also cover direct controller, rules, team and weapon properties,
using each row's resolved entity generation. Usercmd updates reuse staging buffers,
preserve atomic malformed-delta failure, reuse unchanged history projections and
initialize history slices directly in their final shared allocation. Scalar
equality uses raw float bits, preserving NaNs and signed zero.

The opt-in `DecodePlan::project_entity_state` retains output/filter properties,
all parser dependencies and dynamic namespaces while consuming unused wire
values without storing them. A restricted direct-row plan omits unrelated links;
legacy plans remain unprojected. Player-connect bookkeeping still runs when game
event output is disabled. The converter maintains sequential stateful columns
beside parallel fullpacket output, accepting their merge only when every ordered
movement key agrees; this does not relax the upstream continuity restrictions.

Local cosmetic maintenance adds the purchased weapon entity snapshot and buyer
side to `item_purchase` events so buy-and-drop weapons retain econ evidence
without a buyer inventory tick. Inventory cosmetics use entity/revision caching;
the former player/side/weapon slot cache could overwrite a later item's paint,
name, or attachments with the first item observed in that slot.

Local projectile records retain entity serials and the optional native
`m_bIsIncGrenade` flag. Full-packet observations can be merged by instance
without collapsing separate throws with identical initial vectors. The compact
collector also checks serials when an entity index is replaced and accepts
variant evidence arriving after its first observation. Record `tick` remains
the first usable observation, not a proven native creation/subtick timestamp.

The maintained parser also preserves the no-clip sentinel and separates both `m_pReserveAmmo` array elements; the primary count retains its legacy property name.
