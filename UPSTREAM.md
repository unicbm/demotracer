# BotHider upstream tracking

This runtime started from
[`XBribo/CS2-Bot-Hider`](https://github.com/XBribo/CS2-Bot-Hider) commit
`4895e6c47c7f490be79c268eef544693d9ba8f94` (2026-07-12).

The copy under `server/runtime/BotHider` is the DemoTracer runtime source of truth.
Upstream changes are reviewed and imported selectively; this directory is not
kept in mechanical lockstep with the upstream repository.

Upstream `main` was reviewed through commit
`c90a0ac5d04456a77ba266aaba01c9fb0f8d0a8f` (2026-08-02). The current native
runtime includes selectively adapted upstream work through the controller
lifetime safety changes in
`1e87d6773e3e206c3add0f7b8424db657702c815`, including deferred,
identity-checked controller removal and safer transient fake-client flag
restoration. It also keeps the Windows `HandleCommand_JoinTeam` identity scope
from `31b9bd04de3ea326847a701577e9c50779ffe366`.

The gamedata-driven team offset refinement from
`4e4768adf5bec2970e8d082e6e87475c04e31837` is extended here to the dangerous
`CServerSideClient::SetName`, entity-system, and entity-identity layout targets.
The managed shared-memory writer also follows upstream's strict fixed-field
UTF-8 encoding rule: an overlong value is rejected rather than truncated in
the middle of a sequence.

The broader native file-layout refactors, Linux path unification, upstream
`bot_info.json`, removal of the map whitelist, raw per-slot mutation API,
native avatar ownership, automatic bot voting, and upstream module-version
changes remain intentionally unimported because DemoTracer has different
runtime, packaging, BotController ownership, and presentation-lease
boundaries.

The upstream `tools/BotHiderFlairGenerator` utility is intentionally excluded
because it is not part of DemoTracer server runtime or packaging.

BotHider remains licensed under AGPL-3.0-only. Original copyright, attribution,
and license files are preserved in this directory.
