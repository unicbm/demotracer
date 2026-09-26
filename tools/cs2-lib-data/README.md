# cs2-lib data projection

The exact `@ianlucas/cs2-lib` package version in this directory is the upstream
data source for `../../econ/cs2-lib-econ-index.v1.json` and
`../../econ/randomizer-catalog.json`. The converter, playback plugin, and
maintained Randomizer consume these shared projections; no game files or demo
corpus are needed to regenerate them.

```powershell
npm.cmd ci --ignore-scripts
npm.cmd run check
npm.cmd test
```

Run `pwsh -NoProfile -File update.ps1 -CheckOnly` to inspect the latest stable
package without modifying files. To prepare a candidate, omit `-CheckOnly`, or
pass `-Version 9.2.0` to reproduce a specific published version. The script pins
the npm version and source commit, disables package lifecycle scripts, and
generates and checks both outputs. It does not update provider source, commit,
push, open pull requests, or publish releases. A failed generation can leave
partial candidate files; inspect the diff before committing.

Review `source.json`, `package.json`, `package-lock.json`, and both `econ/`
outputs together. Run this repository's `tools/check.ps1`, then release the
common source revision through its reviewed version PR. Consumers adopt that
release by updating their common gitlink and running their own integration
checks. The common source version does not automatically change any provider
version, ABI, or DemoTracer playback contract.

`randomizer-policy.json` retains aggregate knife preferences, historical demo
provenance, and default music-kit exclusions. Updating item availability does
not recompute those policies. `source.json.randomizerCommit` records the
historically reviewed provider source; the data updater neither queries nor
tracks the original Randomizer repository. Engine signatures, agent model
mappings, charm placements, and new cosmetic mechanics require separate
review in the maintained provider repository.

`layout.json` selects output locations only. Normal CI checks reproducibility
and catalog coverage against the pinned published package. Do not hand-edit
generated JSON or add fallback item identifiers. Consumer runtime smoke tests
remain necessary when shipping new item availability or cosmetic behavior.
