# Upstream tracking

The canonical upstream for this extracted plugin is:

- repository: `https://github.com/ed0ard/CS2-Bot-Improver.git`
- subtree: `addons/counterstrikesharp/plugins/BotRandomizer/`

The local `upstream` Git remote should point to that repository, not the older
standalone `ed0ard/CS2-Bot-Randomizer` repository.

## Verified baseline

Checked on 2026-07-30 against upstream `main` commit
`7649abe4b1f0b67c6826aea0c3c488348799ca60`.

The latest upstream commit touching the BotRandomizer subtree was
`d68973289622a58c7580e7bb4c214304a2f99408` (`Improved stability`). Its
functional change removes the runtime enable/category switches and the admin
gate on `br_reroll`. That change is present here.

This extracted repository intentionally retains downstream-only material:

- catalog provenance and demo-evidence validation;
- standalone documentation, tools, notices, and self-tests;
- newer local versioning and the external replay-cosmetic plan API.

Those differences are not evidence that the upstream synchronization is
missing. Future checks should compare the upstream subtree by behavior and file
history, then preserve the downstream-only validation and API layers.
