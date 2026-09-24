# Upstream tracking

## Current baseline: 2026-09-24

Unified provider **1.7.0**, replay API **v3**, reviews standalone `main` at
[`6e9986270aac9c0405a7430a2f26727e7aff3c48`](https://github.com/ed0ard/CS2-Bot-Randomizer/commit/6e9986270aac9c0405a7430a2f26727e7aff3c48).
The four September 23 commits switch the entrypoint to the modular cosmetics
implementation and pair agent models with econ definitions. The maintained
provider already contains those behaviors, including the same agent item/model
pairs, with stricter human-preview assignment and replay ownership handling.
There is no new upstream cosmetic catalog in those commits. Do not replace the
provider wholesale with upstream 1.3.1: it lacks the replay-plan API and Panel
category controls retained here.

Both runtime catalogs now use published `@ianlucas/cs2-lib` **9.2.0**, source
commit `94a5fce488f5976255d74b643bd58f993c04a86f`. This adds 579 stickers
(10,565 -> 11,144), preserving the existing weapon, knife, glove, charm and
music pools. The generator also recognizes compound sticker finishes such as
Ranked and Champion instead of classifying them as paper. Historical aggregate
knife preferences remain unchanged. See
[`tooling/cs2-lib-data`](../../../tooling/cs2-lib-data/README.md) for the exact
pin, reproducible generation, source monitoring and review-only candidate CI.
Unpublished cs2-lib main changes are not silently mixed into this release.

The standalone export includes the complete optional API, provider, self-tests,
data generation and CI. `tools/package.ps1` produces one common ZIP for ordinary
bot matches and playback. DemoTracer's packager validates and imports that ZIP;
it does not compile a different replay provider. Upstream submission remains a
separate maintainer-reviewed action; a local export is not an upstream release.

Hook management uses the shared KHook host baseline in the playback contract.
The pinned CounterStrikeSharp source routes managed `Hook/Unhook` to
`KHook::SetupHook/RemoveHook` and signature lookup to `KHook::LookupSignature`.
The Randomizer adds no native engine of its own. The common provider
keeps this single engine and the separate API v3 cosmetic ownership policy;
KHook alone does not prevent two providers from writing the same inventory.

The existing September 23 Windows attribute-writer signature is retained: it
already targets the new entrypoint and is more specific than the proposed
upstream PR #8 pattern. That PR was still unmerged at review. The Linux pattern
and real-server behavior are not certified by this data update; see
[`docs/SIGNATURES.md`](../../../docs/SIGNATURES.md) for the platform baseline.

The following sections describe historical comparisons; their API/version
numbers identify those historical providers, not the current contract.

This maintained provider tracks both upstream source and the Windows build
actually distributed with CS2-Bot-Improver:

- repository: `https://github.com/ed0ard/CS2-Bot-Improver.git`
- subtree: `addons/counterstrikesharp/plugins/BotRandomizer/`
- standalone source: `https://github.com/ed0ard/CS2-Bot-Randomizer`

## Bot Improver 1.4.4 compatibility

Checked on 2026-09-05 against the release asset `CS2BotImprover.zip` from
`https://github.com/ed0ard/CS2-Bot-Improver/releases/tag/v1.4.4`.

The shipped DLL reports BotRandomizer 1.3.1. Its SHA-256 is
`92E1B9090C59C42677D4291F64B94F5FD3C4B6894D3745BC597B8D018557B911`.
The tag's source reports 1.2.5, as does standalone commit
`a083fbcf62db669105afc75dbe006919150596b2`; neither source snapshot fully
represents the shipped DLL. The release's command behavior was verified through
static metadata/IL inspection, without loading the plugin into a server.

DemoTracer provider 1.6.3 supports the shipped Panel's server-only
`bot_randomizer github.com/ed0ard/CS2-Bot-Randomizer <category> <value>` command
for weapons, knives, gloves, agents, music, stickers, and charms. Randomized
defaults obey these switches; validated DTR plans retain priority. Options do
not reroll loadouts, revoke plans, or reconstruct live inventories. Map changes
keep the options; plugin reload requires reexecuting the server configuration.

The round-start team-intro agent items are also maintained. The 63 econ
item/model pairs match the shipped release; existing downstream default models
remain available with item ID 0. DTR plans retain their explicit agent item ID
and preserve-engine-default policy in the intro. Preview assignment first uses
unique XUID matches; anonymous positions use bot slot order only when every
human has an explicit preview, avoiding upstream's ambiguous human assignment.

This remains a single `BotRandomizer` provider in the standard plugin directory.
There is no second inventory writer or automatic runtime plugin unloading.
The replay-plan API stays at v2. This compatibility work does not imply that
BotHider, BotController, or the Panel's installation-file checks are compatible.

## Historical source baseline

Checked on 2026-07-30 against upstream `main` commit
`7649abe4b1f0b67c6826aea0c3c488348799ca60`.

The latest upstream commit touching the BotRandomizer subtree was
`d68973289622a58c7580e7bb4c214304a2f99408` (`Improved stability`). Its
functional change removed the runtime enable/category switches and the admin
gate on `br_reroll`. Version 1.6.3 restores the categories under the actual
1.4.4 Panel command contract; `br_reroll` retains its existing behavior.

This monorepo intentionally retains downstream-only material:

- catalog provenance and demo-evidence validation;
- standalone documentation, tools, notices, and self-tests;
- newer local versioning and the external replay-cosmetic plan API.

Those differences are not evidence that the upstream synchronization is
missing. Future checks should compare the upstream subtree by behavior and file
history, then preserve the downstream-only validation and API layers.
