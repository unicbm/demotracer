# BotHider upstream tracking

Derived from [XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider)
commit `4895e6c47c7f490be79c268eef544693d9ba8f94` (2026-07-12).
The maintained source is
[`unicbm/demotracer/server/runtime/dtr-hider`](https://github.com/unicbm/demotracer/tree/main/server/runtime/dtr-hider).

## Imported changes

Upstream was reviewed through `c90a0ac5d04456a77ba266aaba01c9fb0f8d0a8f`
(2026-08-02). Selectively adapted changes include:

- controller lifetime safety through `1e87d6773e3e206c3add0f7b8424db657702c815`;
- Windows join-team identity scoping from `31b9bd04de3ea326847a701577e9c50779ffe366`;
- gamedata-driven layout resolution from `4e4768adf5bec2970e8d082e6e87475c04e31837`,
  extended to client names, entity systems and entity identities;
- strict fixed-field UTF-8 validation in the managed native publisher.

The review through upstream v0.4.3, `7fc83ea108c7b29cf014efb73584199565c6418c`
(2026-09-05), imported no additional runtime changes.

From `8568270e089c10ab627ac84c5031e7559f0e287e` (2026-10-29), selectively
adapted required disguise dependencies, explicit identity-check failure and
configured-only automatic SteamIDs. Exact demo leases and batch identity
permutations retain their existing synchronous publication path.

## Maintained boundary

This derivative uses `dtr-hider`, `DtrHiderApi`, `dtr-hider:api:v3` and
`DtrHider_` exports. It owns bot identity and presentation; DemoTracer and the
engine own teams, respawns and replay lifecycle. It retains its own persona,
map-whitelist and presentation-lease behavior. Upstream changes are reviewed
selectively; their roster data, auxiliary tools and unrelated gameplay features
are not bundled. Current installation and API behavior are in [README.md](README.md).

BotHider remains AGPL-3.0-only. Original copyright, attribution and license
files are preserved in [LICENSE](LICENSE) and [LICENSE.AGPL3](LICENSE.AGPL3).
