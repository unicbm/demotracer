# BotHider upstream tracking

This runtime started from
[`XBribo/CS2-Bot-Hider`](https://github.com/XBribo/CS2-Bot-Hider) commit
`4895e6c47c7f490be79c268eef544693d9ba8f94` (2026-07-12).

The copy under `server/runtime/BotHider` is the DemoTracer runtime source of truth.
Upstream changes are reviewed and imported selectively; this directory is not
kept in mechanical lockstep with the upstream repository.

Upstream `main` was reviewed through commit
`65125e54710be3ffb63b94189f7c6cf4dc847cf5` (2026-07-13). This fork imports
the Windows `HandleCommand_JoinTeam` identity scope from
`31b9bd04de3ea326847a701577e9c50779ffe366`, together with the gamedata-driven
team offset refinement from
`4e4768adf5bec2970e8d082e6e87475c04e31837`.

The broader Linux path unification, upstream `bot_info.json`, removal of the
map whitelist, and upstream module-version changes remain intentionally
unimported because DemoTracer has different runtime, packaging, and
presentation-lease boundaries.

The upstream `tools/BotHiderFlairGenerator` utility is intentionally excluded
because it is not part of DemoTracer server runtime or packaging.

BotHider remains licensed under AGPL-3.0-only. Original copyright, attribution,
and license files are preserved in this directory.
