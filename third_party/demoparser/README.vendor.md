# Vendored demoparser notes

This repository vendors the minimal Rust crates used by the converter:

- `src/parser`
- `src/csgoproto`

Large upstream test demos, JavaScript packages, and the full GameTracking-CS2 checkout are intentionally excluded.

The vendored build scripts use the generated `protobuf.rs` and `maps.rs` files already present in this tree, so normal converter builds do not clone GameTracking-CS2 or rewrite vendored source files.

Local button-map maintenance adds JUMP/DUCK properties and aligns WALK,
SCOREBOARD, and ZOOM with the maintained CS2 usercmd button definitions.
The mapping regression test lives in `src/parser/src/maps.rs`.

Local parser maintenance removes the unused private channel-based second-pass
implementation and its duplicate fallback. The active fullpacket-based Rayon
and single-threaded parsing paths remain the supported implementations.

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
