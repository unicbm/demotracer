# Upstream tracking

## Maintained component source

`unicbm/demotracer` owns the provider and its API source in `server/runtime/BotRandomizer`. The
initial split preserves the working provider from `unicbm/demotracer` commit
`012d978ecdce8a949306fdcec39e4dcd9bf7624e`, paths
`server/runtime/BotRandomizer/` and `server/vendor/BotRandomizerApi/`, including
the existing attribution and licenses. The product and standalone package build the same source in this working tree.

Generated catalogs and their generator are maintained once in `server/runtime/common`.
The installed `cosmetic_catalog.json` is linked from common's
`econ/randomizer-catalog.json`; it is not a separate maintained snapshot.

## Upstream sources

- [ed0ard/CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver),
  `addons/counterstrikesharp/plugins/BotRandomizer/`

Historical source references:

- Bot Improver `7649abe4b1f0b67c6826aea0c3c488348799ca60`, with the
  Randomizer subtree last changed by `d68973289622a58c7580e7bb4c214304a2f99408`.
- Standalone Randomizer `a083fbcf62db669105afc75dbe006919150596b2` and
  `6e9986270aac9c0405a7430a2f26727e7aff3c48`.

The maintained derivative adds replay cosmetic plans, ownership validation,
Panel category controls, generated catalogs, packaging, and regression tests.
Current behavior and integration requirements are documented in
[README.md](README.md) and [API.md](API.md).

Catalog versions and source commits are recorded in
[`../common/tools/cs2-lib-data/source.json`](../common/tools/cs2-lib-data/source.json).
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [LICENSE](LICENSE)
for attribution and licensing.
