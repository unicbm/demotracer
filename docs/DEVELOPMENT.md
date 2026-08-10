# Development

## Architecture

| Path | Responsibility |
| --- | --- |
| `desktop/gui/` | Supported Tauri/React application and thin Rust command bridge |
| `desktop/converter/` | Rust parsing, analysis, `.dtr` writing, manifests, and validation |
| `server/plugins/DemoTracer/` | CounterStrikeSharp orchestration and `dtr_` commands |
| `server/plugins/DemoTracerApi/` | Contract-only companion API installed under CounterStrikeSharp `shared/` |
| `server/runtime/BotController/` | Native replay buffers, movement/input injection, weapon control, and C ABI |
| `server/runtime/BotHider/` | Native and managed bot identity/presentation provider |
| `shared/contracts/` | Versioned desktop/server release contracts |
| `shared/econ/` | Cross-runtime projection generated from the pinned `@ianlucas/cs2-lib` package |
| `tooling/` | Validation, packaging, signing, and publishing automation |

The Rust converter crate is the conversion truth source. The desktop backend
calls it directly; there is no supported converter CLI. Future automation
should use a separately versioned API instead of recreating a second UI.

## Dependencies and Provenance

The packaged Windows x64 desktop app requires Microsoft Edge WebView2 but no
developer toolchain. Source builds require Rust stable with the Windows MSVC
target, Node.js 22, pnpm 11.9, .NET 10, and the Tauri Windows prerequisites.

Pinned parser, inspect-link, cosmetic, crosshair, flag, and professional-player
sources are recorded under `third_party/`, `tooling/cs2-lib-data/`, lockfiles,
and their accompanying notices. Generated catalog projections are never edited
by hand.

`server/runtime/BotController` and `server/runtime/BotHider` are maintained
derivatives of XBribo's projects. Preserve their own licenses, attribution, and
`UPSTREAM.md` files; they are not first-party DemoTracer source for copyright
header purposes. The playback server additionally requires Windows x64 CS2,
Metamod:Source, CounterStrikeSharp 1.0.371 or newer, and a matching DemoTracer
bundle.

BotRandomizer 1.5 integration is optional and uses its separately installed v1
API. Ray-Trace 1.0.16 or newer is optional for stricter handoff line-of-sight
checks. Do not mix BotController or BotHider binaries from full
CS2-Bot-Improver packages into a DemoTracer bundle.

| Contract | Required value |
| --- | --- |
| `.dtr` writer / reader | v8 / v3-v8 |
| Manifest ABI | 17 |
| BotController native ABI | 16, minor 33+ |
| BotHider / BotRandomizer API | 1 / 1 |
| DemoTracer companion API | 7 |

## Build and Test

Requirements:

- Local CS2 Metamod/SDK toolchain only when rebuilding native runtimes

The full professional identity dataset is maintained in the separate public
[`unicbm/CS2-pro-steamid-lib`](https://github.com/unicbm/CS2-pro-steamid-lib)
repository and is not tracked here. Before desktop checks or builds, check out
the revision pinned by `desktop/gui/pro-steamid-catalog-source.json` and generate
the local ignored snapshot:

```powershell
node desktop\gui\scripts\import-pro-steamid-catalog.mjs <cs2-pro-steamid-lib>
```

The importer is offline: it reads that checkout's committed cache and never
contacts Liquipedia. It refuses dirty or unpinned source worktrees. CI performs
the same pinned checkout and generation step.

Run the narrowest affected checks first:

```powershell
cd tooling\cs2-lib-data
npm.cmd ci --ignore-scripts
npm.cmd run check

cd ..\..\desktop\converter
cargo test --locked

cd ..\gui
pnpm install --frozen-lockfile
pnpm run check
pnpm test
cargo test --manifest-path src-tauri\Cargo.toml --locked

cd ..\..
.\tooling\scripts\test-css.ps1
.\tooling\scripts\check-release-contract.ps1
```

Refresh `shared/econ/cs2-lib-econ-index.v1.json` only by updating the exact
`@ianlucas/cs2-lib` dependency and lockfile under `tooling/cs2-lib-data`, then
running `npm.cmd run generate` there. Do not add or patch item IDs in the
generated JSON.

Build the supported desktop target:

```powershell
cd desktop\gui
pnpm run tauri:build --target x86_64-pc-windows-msvc -- --locked
```

Debug Rust conversion is intentionally slow. Use release builds for performance
measurements.

## Converter Invariants

- CS2 demos only.
- The complete demo is parsed before round selection.
- Reuse one `ParsedDemo` across analysis and export; do not add redundant
  workflow-level parses.
- Preserve stored evidence bit-exactly. Format changes require an explicit
  version decision.
- Cosmetic/econ export stays explicit opt-in.
- Output contains `.dtr`, manifests, optional `.dtv` voice sidecars, and local
  GUI metadata—not CSV, Parquet, or raw debug dumps.
- Output promotion holds a target-scoped cross-process lock. Local archive
  sidecars use an archive-scoped lock plus monotonic `writeRevision`; metadata
  refreshes must reject a stale revision instead of overwriting newer evidence.

## Runtime Invariants

- Keep manifest ABI, C# readers, native ABI, and packaging contracts in sync.
- Never assign replay control to a human player.
- Release locks, injection state, pending alignments, and replay ownership on
  stop, unload, finish, handoff, or failure.
- Movement replay uses native movement/input hooks; teleport is not the primary
  playback path.
- Ordinary weapon, attachment, and scoreboard alignment remain default-off and
  demo-backed. BotRandomizer coordination claims Agent, Knife, Gloves, and
  ordinary weapon fields only when the selected preset has positive demo
  evidence. Missing cosmetic evidence must not trigger entity reconstruction.

### Replay slot lifecycle

`ReplaySlotRegistry` is the managed truth source for whether a slot is loaded,
claimed for DemoTracer writes, or actively playing. Its phases have these
meanings:

- `Loaded`: the native replay remains available, but DemoTracer no longer owns
  gameplay or inventory writes for the slot.
- `Claimed`: the slot is loaded and DemoTracer may perform preparation writes.
- `Playing`: native playback has started and DemoTracer still owns the slot.

Loading starts a new claimed epoch. Starting playback preserves that epoch.
Handoff, stop, finish, or failure releases the slot back to `Loaded` and starts
a new epoch, invalidating callbacks captured by the prior owner. Unload removes
the slot entirely. Code outside the registry must not maintain parallel loaded,
owned, or playing collections.

Delayed entity or inventory writes capture the registry epoch and verify it at
execution time. Replay identity generation remains separate because identity
metadata can change independently from the write-ownership lifecycle.

Round-boundary writes use two coalescing lanes. Slot work is keyed by slot,
operation kind, write epoch, and replay-identity generation, so spawn and
companion-lease callbacks cannot queue duplicate reconciliation for the same
owner. Global presentation and C4 reconciliation is coalesced across each burst
of player-spawn events. A stale callback may still be delivered by
CounterStrikeSharp, but it cannot consume work from a newer epoch or write to
the newer slot incarnation.

Round loading establishes ownership and companion writer leases before spawn
callbacks, but defers pawn inventory and entity reconstruction until the live
pawns are ready. Companion lease replacement is transactional across the roster:
intermediate per-slot metadata changes do not publish partial claim sets. Freeze
pre-roll performs full pawn preparation at most once for each pre-roll token.
Remaining readiness checks use a bounded 50 ms cadence; polls must not perform
full-roster inventory or entity reconstruction.

`test-css.ps1` also enforces the managed-source boundaries: the
CounterStrikeSharp entry point remains a small composition root and ordinary
source files cannot grow past the maintained limit. Playback planning, playoff,
replay-target safety, global teardown, slot lifecycle, and loaded metadata remain
separate domains. Control commands are grouped by alignment, cosmetics, and
general diagnostics. Replay loadout orchestration is kept apart from weapon
alignment, inventory observation, and entity mutation. The `.dtr` reader keeps
format entry, section decoding, payload decoding, and semantic validation in
separate files. Armed, sequence, and playoff reset invariants belong to
`ReplayPlanState`; the companion API assembly remains contract-only.

## Packaging

The public release contains exactly two Windows x64 assets:

- `demotracer-gui-vVERSION.exe`: NSIS desktop installer.
- `demotracer-css-vVERSION.zip`: matched CS2 plugin bundle.

The desktop app checks the signed stable GUI manifest at
`https://releases.detr.site/channels/stable/latest.json` on startup. A newer
version is shown with localized release notes and is installed only after user
confirmation. Tauri verifies the updater signature before starting the passive
NSIS install. The same manifest advertises the matched CSS bundle. Its immutable
release URL, SHA-256 digest, and minisign signature are verified before the
existing receipt and per-file validation changes CS2, with one rollback
preserved. Local CSS ZIP installation remains available as a fallback.

```powershell
.\tooling\scripts\package-release.ps1 `
  -Version <version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint>
```

The release contract check verifies package versions, updater configuration,
and ABI/API gates before packaging. `package-release.ps1` rebuilds the NSIS
installer and CSS bundle, then creates two deliberately separate directories:

- `dist/release-v<version>` contains only the public GitHub assets: the GUI EXE
  and CSS ZIP.
- `dist/updater-v<version>` contains the signed GUI and CSS updater payloads,
  `latest.json`, and checksums for R2 publishing.

Pass localized release notes while packaging, then publish only the updater
directory to R2:

```powershell
.\tooling\scripts\package-release.ps1 `
  -Version <version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint> `
  -ReleaseNotesZh "<简体中文更新说明>" `
  -ReleaseNotes "<English release notes>"

.\tooling\scripts\publish-r2.ps1 -Version <version>
```

An Authenticode code-signing certificate can reduce Windows reputation
warnings. Pass its SHA-1 certificate-store thumbprint when available. Without a
certificate, pass `-AllowUnsignedInstaller`; the script labels the result as
unsigned and Windows SmartScreen may warn. Never commit or upload a certificate
private key or PFX file.

Before publishing:

```powershell
git status -sb
git diff --check
```

Do not publish raw demos, generated replay archives, logs, local paths, private
server configuration, or build output.
