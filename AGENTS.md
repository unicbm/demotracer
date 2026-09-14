# Agent Guidance

This is the public canonical monorepo for **DemoTracer**, whose product name is
**CS2 DemoTracer**. Keep the desktop application, Rust converter, server
plugins, native runtimes, contracts, and release tooling aligned.

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

    cd desktop\converter
    cargo test --locked

    cd ..\gui
    pnpm install --frozen-lockfile
    pnpm run check
    pnpm test
    cargo test --manifest-path src-tauri\Cargo.toml --locked

    cd ..\..
    .\tooling\scripts\test-css.ps1
    .\tooling\scripts\check-release-contract.ps1
    git diff --check

Native runtime changes additionally require the maintained CMake release build
and CTest suite. Before every public push, inspect the staged set and scan it
for local paths, demos, replay data, logs, credentials, keys, certificates, and
build artifacts.
