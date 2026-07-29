# BotController upstream tracking

The DemoTracer BotController runtime is a maintained derivative of
[`XBribo/CS2-Bot-Controller`](https://github.com/XBribo/CS2-Bot-Controller).
XBribo's engine-level bot control, lock, movement-recording, and replay work
formed the native foundation on which DemoTracer playback was built.

The initial DemoTracer import is recorded in repository commit
`999b51dcb25641d11e2ebe6ed2c73956a29b3d05` (2026-06-15). It entered through
an earlier local integration rather than a Git subtree, so this document does
not claim an unverifiable upstream commit hash.

The copy under `server/runtime/BotController` is the DemoTracer source of
truth. DemoTracer has since added its versioned replay ABI, `.dtr` loading,
subtick command replay, handoff safety, projectile and voice integration,
runtime health reporting, and defensive buffer validation. Upstream changes
are reviewed and imported deliberately rather than merged mechanically.

The runtime remains AGPL-3.0-only. XBribo's authorship is retained in the
native plugin metadata and README, and the repository-level license applies to
this maintained derivative.
