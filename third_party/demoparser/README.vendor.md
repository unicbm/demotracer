# Vendored demoparser notes

This repository vendors the minimal Rust crates used by the converter:

- `src/parser`
- `src/csgoproto`

Large upstream test demos, JavaScript packages, and the full GameTracking-CS2 checkout are intentionally excluded.

The vendored build scripts use the generated `protobuf.rs` and `maps.rs` files already present in this tree, so normal converter builds do not clone GameTracking-CS2 or rewrite vendored source files.

Local button-map maintenance adds JUMP/DUCK properties and aligns WALK,
SCOREBOARD, and ZOOM with the maintained CS2 usercmd button definitions.
The mapping regression test lives in `src/parser/src/maps.rs`.
