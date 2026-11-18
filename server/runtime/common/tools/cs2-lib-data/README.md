# cs2-lib data projection

The locked cs2-lib source archive supplies item records and translations. The
published npm package supplies enum constants. Valve scoreboard coin definitions
omitted by cs2-lib are recorded with provenance in `tournament-coins.json`.
Together they are the data source for `../../econ/cs2-lib-econ-index.v1.json` and
`../../econ/randomizer-catalog.json`. The converter, playback plugin, and
maintained Randomizer consume these shared projections; no game files or demo
corpus are needed to regenerate them.

```powershell
npm.cmd ci --ignore-scripts
npm.cmd run check
npm.cmd test
```

Run `node update.mjs --check-only` to inspect the latest stable
package without modifying files. To prepare a candidate, omit `--check-only`, or
pass `--version 9.2.0` to reproduce a specific published version. The script pins
the npm version and source commit, disables package lifecycle scripts, and
generates and checks both outputs. It does not update provider source, commit,
push, open pull requests, or publish releases. A failed generation can leave
partial candidate files; inspect the diff before committing.

Review `source.json`, `package.json`, `package-lock.json`, and both `econ/`
outputs together. Consumers use these files directly from
`server/runtime/common`; run the affected converter and playback checks before
committing generated data changes.

`randomizer-policy.json` retains aggregate knife preferences, historical demo
provenance, and default music-kit exclusions. Updating item availability does
not recompute those policies. `source.json.randomizerCommit` records the
historically reviewed provider source; the data updater neither queries nor
tracks the original Randomizer repository. Engine signatures, agent model
mappings, charm placements, and new cosmetic mechanics require separate
review in the maintained provider repository.

`layout.json` selects output locations only. Normal CI checks reproducibility
and catalog coverage against the locked upstream data. Do not hand-edit
generated JSON or add fallback item identifiers. Consumer runtime smoke tests
remain necessary when shipping new item availability or cosmetic behavior.
