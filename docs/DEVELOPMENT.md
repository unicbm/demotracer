# Development

## Architecture

This repository owns the GUI, product compatibility contract and release
integration. The reusable components are independently maintained repositories
mounted as Git submodules at the paths below. `components.json` records their
maintained repositories, release branches and tag prefixes; Git records the
exact selected commits.

| Path | Responsibility |
| --- | --- |
| `desktop/gui/` | Supported Tauri/React application and thin Rust command bridge |
| `desktop/converter/` | Converter component: Rust analysis, `.dtr` writing, manifests, and validation |
| `third_party/demoparser/` | Maintained `demoparser` branch with the minimal `parser` / `csgoproto` workspace |
| `server/plugins/DemoTracer/` | `cs2-css-demotracer` component: production project in `src/DemoTracer`, configuration in `config`, tests in `tests/DemoTracer.Tests` |
| `server/runtime/common/csharp/DemoTracerApi/` | Contract-only companion API installed under CounterStrikeSharp `shared/` |
| `server/runtime/BotController/` | Native replay buffers, movement/input injection, weapon control, and C ABI |
| `server/runtime/BotHider/` | Native and managed bot identity/presentation provider |
| `server/runtime/BotRandomizer/` | Bundled and version-locked cosmetic entity writer |
| `server/runtime/common/native/` | Common component's shared native utilities and tests |
| `server/runtime/common/contracts/` | Shared source-field declarations and native host/toolchain pins |
| `server/runtime/common/econ/` | Cross-runtime projection generated from the pinned `@ianlucas/cs2-lib` package |
| `server/runtime/common/tools/cs2-lib-data/` | Generator and source lock for that econ projection |
| `shared/contracts/` | Product-level supported Playback compatibility contract |
| `tooling/` | Validation, packaging, signing, and publishing automation |

The Rust converter crate is the conversion truth source. The desktop backend
calls it directly; there is no supported converter CLI. Future automation
should use a separately versioned API instead of recreating a second UI.
Its repository name is `cs2-dtr-converter`; the Cargo package and library remain
`cs2-demotracer` and `cs2_demotracer`. Provider APIs live with their providers:
BotController and BotHider under `csharp/`, and BotRandomizer under
`BotRandomizerApi/`. They are referenced from each consumer's pinned `.deps`
submodules instead of separate copies in this repository.

The CSS project is
`server/plugins/DemoTracer/src/DemoTracer/DemoTracer.csproj`. Its production code
is organized by responsibility beneath that directory, with the plugin entry
point in `Lifecycle/DemoTracerPlugin.cs` and native ABI declarations in
`Native/BotControllerNativeTypes.cs`. Builds write to
`src/DemoTracer/bin/<Configuration>/net10.0` inside the component. Configuration
templates remain separate in `config/`; packaging places the native profile and
example configuration beside the plugin DLL under their existing filenames.

Demo-backed appearance rules live in `desktop/converter/src/cosmetics/`:

- `mod.rs` validates appearance fields and matches player-scoped end-of-match
  evidence for knives and gloves. Normalization does not encode inspect links.
- `catalog.rs` owns the embedded econ index and shared equipment/item validation.
- `inventory.rs` owns item identity, original ownership and first-valid inventory
  observations. Purchases remain evidence even without a buyer inventory tick.
- `playback.rs` selects round-scoped appearances. A known start inventory,
  including an unpainted one, takes precedence over later inventory or active
  weapon observations. Conflicting fields are omitted independently.

Player analysis uses the common rules directly, without depending on `export`.
Whole-demo ownership summaries and round-start playback retain different
selection policies. Shared inventory snapshots may skip repeated observations
only while holder and side are unchanged; new snapshots must still be examined.
Inspect links are encoded at final output. Parser entity revisions, purchase
capture, public manifest fields and runtime contracts remain separate concerns.

## Dependencies and Provenance

The packaged Windows x64 desktop app requires Microsoft Edge WebView2 but no
developer toolchain. Source builds require Rust stable with the Windows MSVC
target, Node.js 22, pnpm 11.9, .NET 10, and the Tauri Windows prerequisites.

Pinned parser, inspect-link, cosmetic, crosshair, flag, and professional-player
sources are recorded under `third_party/`,
`server/runtime/common/tools/cs2-lib-data/`, component manifests, lockfiles,
and their accompanying notices. Generated catalog projections are never edited
by hand.

`server/runtime/BotController`, `server/runtime/BotHider`, and
`server/runtime/BotRandomizer` are maintained
derivatives of their recorded upstream projects. Preserve their own licenses, attribution, and
`UPSTREAM.md` files; they are not first-party DemoTracer source for copyright
header purposes. The playback server additionally requires Windows x64 CS2,
Metamod:Source 2.0 build 1469+, a KHook-enabled CounterStrikeSharp host, and a
matching DemoTracer bundle. See the pinned source baseline and native hook
tests in [playback server requirements](../server/README.md#shared-hook-runtime).

BotRandomizer 1.7.0 is part of the matched playback bundle and implements the v3
replay-plan API. DemoTracer owns normalization and plan lifetime only;
BotRandomizer owns all cosmetic entity writes at spawn or item construction.
Ray-Trace 1.0.16 or newer is optional for stricter handoff line-of-sight checks.
Do not mix BotController, BotHider, or BotRandomizer binaries from full
CS2-Bot-Improver packages into a DemoTracer bundle.

Replay has one movement input path: after the engine's `SetupMove`, BotController
supplies the demo's pre-command position and velocity in `CMoveData`. Native
movement and `FinishMove` compute and publish the resulting pawn state. Replay
initializes the pawn's pose and duck/ladder state only at start, seek, or loop
boundaries; it does not overwrite the engine's movement output or expose
alternative movement correction modes. Pre snapshots remain necessary input
because supported demo records can omit original analog command fields.
Missing command axes are neutral instead of inheriting bot AI input. Optional
`bc_perf` counters distinguish per-command movement inputs from boundary-only
movement initializations without writing per-tick logs.

| Contract | Required value |
| --- | --- |
| `.dtr` writer / reader | v12 / v3-v12 |
| Manifest ABI | 19 |
| BotController native ABI | 21, minor 44+; 228-byte replay tick |
| BotHider / BotRandomizer API | 3 / 3 |
| DemoTracer companion API | 7 |

## Component Maintenance

For a fresh source checkout or after pulling a product pin update:

```powershell
git clone --recurse-submodules https://github.com/unicbm/demotracer.git
# Or, inside an existing checkout:
git submodule update --init --recursive
node server\runtime\common\tools\check-components.mjs
```

The component-pin check verifies that recursive consumers use the same shared
component revisions selected by the product. A mixed set of common, parser or
provider revisions must be reconciled through consumer releases before product
integration. Do not use `git submodule update --remote` to bypass the recorded
release gitlinks.

Make a component change in its own repository and submit its PR there. Run its
`tools/check.ps1` and any required live-server checks, then publish the reviewed
component release. The release workflow manages that component's source version
and changelog. Consumers and this product use the `automation/components` PR to
propose released gitlinks; product CI validates the resulting bundle before merge.
This automation follows our maintained repositories and release tags. Reviewing
or importing original upstream changes remains a separate maintenance decision.

GUI changes stay in `desktop/gui` in this repository. Component source versions,
GUI/Playback product versions, `.dtr` and manifest versions, and public API/ABI
versions are independent. A new source tag does not imply an API/ABI bump or a
compatible bundle. Update the product contract and all affected readers/writers
when a real compatibility change requires it.

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

Vite validates and merges the identity sources at build time, then emits a
display/search projection and the cosmetic catalog as separate, hashed JSON
assets. The UI loads these local assets on demand and shares each decoded
catalog across views. Original catalogs and provenance remain the build inputs;
do not hand-edit the generated runtime projection. Country flags retain the
complete supported country set in the UI's 4:3 aspect ratio.

Release binaries use Tauri's default Brotli asset compression. `dist` sizes are
uncompressed asset sizes, not installer sizes. Adding a second compression layer
requires measuring both compressed size and the additional decoding path.

Run the narrowest affected checks first:

```powershell
cd server\runtime\common\tools\cs2-lib-data
npm.cmd ci --ignore-scripts
npm.cmd run check
npm.cmd test

cd ..\..\..\..\..
pwsh -NoProfile -File desktop\converter\tools\check.ps1

cd desktop\gui
pnpm install --frozen-lockfile
pnpm run check
pnpm test
cargo test --manifest-path src-tauri\Cargo.toml --locked

cd ..\..
.\tooling\scripts\test-css.ps1
.\tooling\scripts\check-release-contract.ps1
```

### Parser implementation and diagnostics

The maintained parser submodule uses a root Cargo workspace and lockfile. Its
default tests are self-contained synthetic regressions; no demo is bundled:

```powershell
pwsh -NoProfile -File third_party\demoparser\tools\check.ps1
```

The script also compiles the original fixture golden tests, without executing
them. They remain behind `external-demo-tests` and explicit ignored annotations.
To run them, supply the original upstream `test_demo.dem` using the script's
`-FixtureTests -DemoPath <path>` options. Missing data fails explicitly; a default
green run does not claim the external fixture lane passed. See the parser's
README for the fixture provenance boundary.

`DEMOTRACER_PROFILE` enables coarse stderr timings for first/second pass, column
merge, converter channels/fallback reasons, hashing, sorting and row materialization.
It is off by default and does not log per-tick data. The GUI release profile uses
speed optimization (`opt-level = 3`), fat LTO and one codegen unit. Compare
performance with matching release code-generation settings and diagnostics
disabled. Keep local measurement tools, demo inputs and results out of commits.

Direct scalar columns record value changes during protocol decoding and expand
them into final typed columns using linear CPU cursors. Entity generations and
output-row sequence distinguish recreation, missing values and multiple samples
within one tick. Scalar columns also track the actual controller, rules, team
and active-weapon entities referenced by each row. Vectors and lists retain
their existing semantics. Immutable history, subticks and sticker snapshots share backing
storage; repeated strings use dictionary columns and are materialized by the
converter. Row-local entity links are resolved once, and inventory snapshots
invalidate on their actual data dependencies rather than every entity packet.

Set `DEMOTRACER_SPARSE_COLUMNS=0` to compare the normal collector against sparse
scalar collection in the same executable. Leave it unset for the default optimized
path. `DEMOTRACER_PROFILE` additionally reports frame decompression, entity
decoding, collection, and deferred-column finalization; include all these phases
in the complete parse time. These optimizations use CPU only and add no graphics
runtime or driver requirement.

For hotspot diagnosis, `DEMOTRACER_PROFILE_PROPERTIES=1` samples one row in
each 256-row block and reports the most expensive remaining getters/appends.
Sampling rotates across player slots. Timer overhead is included, so use this
to locate hotspots, not as a substitute for complete parse timings. Leave it
unset for performance measurements.

The converter splits continuous-state fields (usercmds, duck/fall state,
accumulated damage and affected source-state fields) into a sequential channel;
the remaining channel uses fullpacket segments in parallel. Both channels run
concurrently, and every tick/entity/SteamID/round key must match in order before
the overlay is accepted. Unsupported fields, missing columns or alignment
failure fall back to the complete sequential parse. Source-state aliases also
resolve through this overlay.

`DecodePlan::project_entity_state` is an opt-in dependency projection used by
the converter. It consumes every wire value while avoiding storage for unused
ordinary properties. Requested fields, query filters, parser metadata and
dynamic inventory/econ/event namespaces are retained. The restricted direct-row
channel can omit unrelated links; general parser plans keep full entity state.
Input hashing and column reclamation overlap independent parsing/row work.

Run the install-free GUI acceptance application with its real Rust backend and
Vite hot reload:

```powershell
cd desktop\gui
pnpm run dev:acceptance
```

This is the normal GUI acceptance entry point. It does not build or install an
NSIS package. It starts Vite on
`127.0.0.1:1420` and opens that frontend inside the Tauri WebView, so Tauri
commands, the converter, the local library, avatar cache, and filesystem access
remain available. The Rust backend uses the release profile for realistic demo
parsing performance while TSX and CSS edits hot-reload in the already-open
window. `pnpm dev` is a short alias for the same workflow. Use `pnpm run
dev:debug` only when debugging Rust itself. `pnpm run dev:web` starts only the
frontend server and is useful for isolated layout work; a regular browser at
that address does not have Tauri IPC and therefore cannot be used to accept
Manifest, library, conversion, or other real-backend behavior.

For a standalone release executable that runs without Vite, use the Tauri
build entry point from `desktop/gui`:

```powershell
node node_modules/@tauri-apps/cli/tauri.js build --no-bundle --ci -- --locked
```

This builds the frontend and enables Tauri's `custom-protocol` asset embedding.
Bare `cargo build --release` is only a Rust compilation check: it can still
produce a development-mode WebView that loads `localhost:1420`. Do not deliver
that executable as a standalone GUI. Validate the release executable with Vite
stopped, checking that the embedded page renders and a read-only Tauri command
succeeds. Use `pnpm run tauri:build` when an NSIS installer is required.

GUI appearance preferences use `gui-preferences.v1.json` in Tauri's application
local-data directory as their versioned source of truth. The document stores the
language, selected theme, UI font size, sidebar state, theme customization, and
custom CSS profiles. WebView `localStorage` retains only a synchronized startup
cache so the theme and font can be applied before the asynchronous Tauri command
returns. When the JSON file does not exist, the application imports the existing
startup cache once and creates it automatically. The workspace background remains
the separate bounded `appearance/workspace-background.png` asset.

Refresh `server/runtime/common/econ/cs2-lib-econ-index.v1.json` only in the
common repository by updating the exact `@ianlucas/cs2-lib` dependency and
lockfile under its `tools/cs2-lib-data`, then running `npm.cmd run generate`
there. Release and pin that shared change through its consumers. Do not add or
patch item IDs in the generated JSON or create consumer-local copies.

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
- Player weapon ownership evidence includes purchase-time entity snapshots
  throughout the demo, including warmup and buy-and-drop/refund cases. It does
  not require a buyer inventory tick or match participation by that weapon.
  Inventory observations are attributed by item account/original owner, and
  distinct items of the same weapon type are retained. Match statistics and
  replay round selection keep their existing time windows.
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
  demo-backed. DemoTracer may only submit complete cosmetic plans through the
  BotRandomizer v3 API. BotRandomizer is the only cosmetic entity writer and
  consumes plans during natural spawn/item construction; DemoTracer must not
  add a parallel econ/model/bodygroup repair path.

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

`test-css.ps1` runs the CSS component's `tests/DemoTracer.Tests` suite and
recursively checks production code under `src/DemoTracer`, excluding `bin/` and
`obj/`. Existing file limits follow each filename across responsibility
directories. It enforces the managed-source boundaries: the
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

The public release contains the Windows x64 GUI installer and the currently
advertised Playback bundle. Their component versions may differ for a GUI-only
hotfix:

- `demotracer-gui-v<gui-version>.exe`: NSIS desktop installer.
- `demotracer-css-v<playback-version>.zip`: compatible CS2 plugin bundle.

The desktop app checks the signed stable GUI manifest at
`https://releases.detr.site/channels/stable/latest.json` on startup. A newer
version is shown with localized release notes and is installed only after user
confirmation. Tauri verifies the updater signature before starting the passive
NSIS install. The same manifest advertises the compatible CSS bundle under its
own version and release notes. Its immutable
release URL, SHA-256 digest, and minisign signature are verified before the
existing receipt and per-file validation changes CS2, with one rollback
preserved. Local CSS ZIP installation remains available as a fallback.

```powershell
.\tooling\scripts\package-release.ps1 `
  -Version <version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint>
```

For a GUI-only hotfix, pin the unchanged Playback version explicitly. The
packager reuses its existing ZIP and signature without rebuilding or resigning
it, and the R2 publisher verifies the immutable prior object instead of
uploading a duplicate under the GUI version:

```powershell
.\tooling\scripts\package-release.ps1 `
  -Version <gui-version> `
  -PlaybackVersion <existing-playback-version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint>
```

The release contract check verifies the independent GUI and Playback versions,
updater configuration, and ABI/API gates before packaging.
`package-release.ps1` rebuilds the NSIS installer and, when both versions are
the same, the CSS bundle. It then creates two deliberately separate directories:

- `dist/release-v<version>` contains only the public GitHub assets: the GUI EXE
  and CSS ZIP.
- `dist/updater-v<version>` contains the signed GUI and CSS updater payloads,
  `latest.json`, and checksums for R2 publishing.

The packager automatically uses `tooling/release/release-notes.v<version>.json`
when present. Explicit parameters override the corresponding language. Publish
only the updater directory to R2:

```powershell
.\tooling\scripts\package-release.ps1 `
  -Version <version> `
  -CertificateThumbprint <code-signing-certificate-thumbprint> `
  -ReleaseNotesZh "<简体中文更新说明>" `
  -ReleaseNotes "<English release notes>"

.\tooling\scripts\publish-r2.ps1 `
  -Version <gui-version> `
  -PlaybackVersion <playback-version>
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
