# Upstream tracking

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
