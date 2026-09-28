# Agent Guidance

This is the public GUI and product-integration repository for **DemoTracer**,
whose product name is **CS2 DemoTracer**. The GUI, converter, playback plugin, bot runtimes and shared infrastructure
are product modules in this working tree. The maintained parser fork and the
external XBribo BotHider API dependency are pinned Git submodules. Keep product
contracts and packaging aligned.

## Project Boundaries

- Keep this repository public, portable, and free of local server state.
- The supported product is the Windows x64 Tauri GUI plus its matched playback
  bundle. There is no supported converter CLI in the 1.x line.
- Preserve .dtr, manifest, native ABI, BotHider API, and companion API
  alignment. Update shared/contracts/playback-contract.v1.json and all readers,
  writers, docs, tests, and package metadata together when a contract changes.
- Never commit local paths, Steam paths, demo files, generated replay archives,
  logs, credentials, signing keys, certificates, server-local configuration,
  build output, or user inventory and session data.
- Preserve third-party source, provenance, and license files. BotController and
  BotHider remain AGPL-3.0-only maintained derivatives with attribution.
- Product names and official-build marks are governed by TRADEMARKS.md.

## Engineering Workflow

- Initialize the pinned parser with `git submodule update --init --recursive`.
  Keep the parser and BotHider gitlinks.
  Do not introduce other submodules for product modules.
- Change product modules and all affected consumers in one coherent change.
  Shared contracts and generated catalogs have one source in
  `server/runtime/common`; do not copy them into consumer directories.
- GUI/Playback, standalone package and API/ABI versions remain independent.
  Source movement does not itself require an API/ABI or DLL identity change.
- Keep CI impact selection conservative. Shared/build changes and releases
  require full validation; reuse tested artifacts when packaging.
- Do not automatically request another AI review for unchanged source already
  reviewed internally or mechanical release metadata. Review new source,
  contracts, Action major versions and permission changes as appropriate.
- Make the smallest evidence-backed change and run the narrowest relevant
  validation first.
- Use release builds for performance and Windows installer checks.
- Never assign replay control to human players.
- On stop, unload, natural finish, handoff, or failure, release replay locks,
  injected input, pending alignments, ownership, and transient bot state.
- Movement playback must use maintained movement and input hooks; teleport is
  not the primary replay mechanism.
- Cosmetic and scoreboard behavior must remain demo-backed, defensive, and
  default-off where documented.
- Preserve user configuration and unrelated worktree changes.
- Publish only maintained product, contributor, contract, and provenance
  documentation. Keep research notes, binary investigations, compatibility
  audits, and temporary validation reports local and ignored; do not force-add
  them. `docs/` has an explicit public-document allowlist in `.gitignore`.

## Validation

    git submodule update --init --recursive
    node tooling/scripts/check-source-layout.mjs --require-checkout
    pwsh -NoProfile -File desktop\converter\tools\check.ps1

    cd desktop\gui
    pnpm install --frozen-lockfile
    pnpm run check
    pnpm test
    cargo test --manifest-path src-tauri\Cargo.toml --locked

    cd ..\..
    .\tooling\scripts\test-css.ps1
    .\tooling\scripts\check-release-contract.ps1
    git diff --check

Native runtime changes additionally require the maintained CMake release build
and CTest suite for the affected module. Shared native sources/tests live in
`server/runtime/common/native`, companion API in its `csharp/DemoTracerApi`,
shared fields in its `contracts`, and the econ generator in its
`tools/cs2-lib-data`. The playback module lives at
`server/plugins/DemoTracer`; its project and production source live under
`src/DemoTracer`, organized by responsibility. Configuration templates live in
`config`, and regression tests in `tests/DemoTracer.Tests`. Provider APIs remain
with their providers.
Before every public push, inspect the staged set and scan it
for local paths, demos, replay data, logs, credentials, keys, certificates, and
build artifacts.
