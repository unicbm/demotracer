# cs2-lib data projection

DemoTracer does not maintain an item-ID registry. The exact
`@ianlucas/cs2-lib` dependency in this directory is the only upstream source
for the generated runtime econ data shared by the Rust converter and the C#
playback plugin, and the BotRandomizer random-roll catalog. Both outputs are
generated from the same package; no game files or demo corpus are needed.

```powershell
npm.cmd ci --ignore-scripts
npm.cmd run check
npm.cmd test
```

To check for updates without modifying files:

```powershell
pwsh -NoProfile -File update.ps1 -CheckOnly
```

To prepare a candidate, run `pwsh -NoProfile -File update.ps1` (latest stable),
or pass `-Version 9.2.0` to reproduce a specific release. The script checks the
upstream Randomizer HEAD using a public GET, pins npm version and source commit,
installs with lifecycle scripts disabled, and generates and checks both outputs.
It never changes the reviewed Randomizer revision automatically, commits,
pushes, opens PRs, or publishes releases. Run it in a clean checkout and inspect
the diff; a failed generation can leave partial candidate files for review.

Review and commit `source.json`, `package.json`, `package-lock.json`,
`shared/econ/cs2-lib-econ-index.v1.json`, and
`server/runtime/BotRandomizer/cosmetic_catalog.json` together. Bump the provider
version and playback contract when shipping data. Run `tooling/scripts/test-css.ps1`
and `tooling/scripts/check-release-contract.ps1` from the repository root,
then perform in-game randomization and replay smoke tests before release.
Do not hand-edit generated JSON or add local fallback item identifiers.

`randomizer-policy.json` preserves the existing aggregate knife preferences and
their historical demo provenance, plus the existing default music-kit exclusions.
Updating item availability does not recompute those preferences. Sticker finish
classification recognizes compound names such as `(Gold, Ranked)` and
`(Glitter, Champion)`. Agent model mappings and sampled charm placements remain
separately maintained; a catalog update does not certify engine signatures,
new cosmetic mechanics, or model paths.

The standalone common provider uses these same generators and tests. `layout.json`
selects output locations only; it never changes data or randomization rules.
The source exporter relocates this directory to `tools/data` and points the two
outputs at the standalone repository root. The ordinary-server ZIP and the
DemoTracer playback bundle consume the same resulting provider and shared API.

Normal CI checks reproducibility and coverage against cs2-lib. The
`Bot Randomizer data maintenance` workflow runs weekly or manually, builds a
candidate and runs consumer checks, then saves a patch and source comparison as
an artifact. Its token is read-only. The patch may be incomplete if the job failed;
only passing candidates should be considered. No GitHub-side setup or execution
is performed by adding this workflow locally; it starts after normal review and
publication on the default branch.

The checked baseline is the latest published package, not unversioned GitHub
main. Upstream source changes are reported for manual review even when npm has
not published them. Update `source.json.randomizerCommit` only after reviewing
the source delta and recording compatibility in the provider's `UPSTREAM.md`.
