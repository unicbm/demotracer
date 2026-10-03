# Development

## Architecture

All product modules are maintained in this repository. Only
`third_party/demoparser` is a submodule; `components.json` records ownership.

| Path | Responsibility |
| --- | --- |
| `desktop/gui/` | Supported Tauri/React application and thin Rust command bridge |
| `desktop/converter/` | Converter component: Rust analysis, `.dtr` writing, manifests, and validation |
| `third_party/demoparser/` | Maintained `demoparser` branch with the minimal `parser` / `csgoproto` workspace |
| `server/plugins/DemoTracer/` | Playback module: production project in `src/DemoTracer`, configuration in `config`, tests in `tests/DemoTracer.Tests` |
| `server/runtime/common/csharp/DemoTracerApi/` | Contract-only companion API installed under CounterStrikeSharp `shared/` |
| `server/runtime/dtr-controller/` | Native replay buffers, movement/input injection, weapon control, and C ABI |
| `server/runtime/dtr-hider/` | Native and managed bot identity/presentation provider |
| `server/runtime/BotRandomizer/` | Bundled and version-locked cosmetic entity writer |
| `server/runtime/common/native/` | Common component's shared native utilities and tests |
| `server/runtime/common/contracts/` | Shared source-field declarations and native host/toolchain pins |
| `server/runtime/common/econ/` | Cross-runtime projection generated from the pinned `@ianlucas/cs2-lib` package |
| `server/runtime/common/tools/cs2-lib-data/` | Generator and source lock for that econ projection |
| `shared/contracts/` | Product-level supported Playback compatibility contract |
| `tooling/` | Validation, packaging, signing, and publishing automation |

The GUI calls the Rust converter crate (`cs2-demotracer` / `cs2_demotracer`)
directly; there is no supported converter CLI. Provider APIs live under each
provider's `csharp/` directory, or `BotRandomizerApi/` for BotRandomizer.
See the [playback source map](../server/plugins/DemoTracer/docs/DEVELOPMENT.md)
for the managed plugin's internal layout.

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

Source builds require Rust stable with the Windows MSVC target, Node.js 22,
pnpm 11.9, .NET 10 and the Tauri Windows prerequisites. Native builds also need
the pinned [server toolchain](../server/README.md#shared-hook-runtime).

Dependency pins and licenses live in `third_party/`, component manifests,
lockfiles and accompanying notices. dtr-controller, dtr-hider and BotRandomizer
are maintained upstream derivatives; preserve their licenses, attribution and
`UPSTREAM.md` files. Generated catalogs must not be edited by hand.

BotRandomizer owns cosmetic entity writes; DemoTracer submits validated plans
through API v3. Contact handoff requires dtr-controller native perception; no
external tracing provider is needed. Install the matched playback bundle rather
than mixing provider DLLs.

Replay has one movement input path: after the engine's `SetupMove`, dtr-controller
supplies the demo's pre-command position and velocity in `CMoveData`. Native
movement and `FinishMove` compute and publish the resulting pawn state. Replay
initializes the pawn's pose and duck/ladder state only at start, seek, or loop
boundaries; it does not overwrite the engine's movement output or expose
alternative movement correction modes. Pre snapshots remain necessary input
because supported demo records can omit original analog command fields.
Missing command axes are neutral instead of inheriting bot AI input. Optional
`dtr_controller_perf` counters distinguish per-command movement inputs from boundary-only
movement initializations without writing per-tick logs.

| Contract | Required value |
| --- | --- |
| `.dtr` writer / reader | v12 / v3-v12 |
| Manifest ABI | 19 |
| dtr-controller native ABI | 21, minor 44+; 228-byte replay tick |
| dtr-hider / BotRandomizer API | 3 / 3 |
| DemoTracer companion API | 7 |

## Component Maintenance

The bundled DTR bot runtime uses independent component identities:

| Component | Native DLL | Managed DLL | Shared API | Capability |
| --- | --- | --- | --- | --- |
| `dtr-controller` | `dtr-controller.dll` | `DtrController.dll` | `DtrControllerApi.dll` | `dtr-controller:api` |
| `dtr-hider` | `dtr-hider.dll` | `DtrHider.dll` | `DtrHiderApi.dll` | `dtr-hider:api:v3` |

Native directories and Metamod aliases use the component names. CSS provider
directories match their assembly names. Native exports use `DtrController_`
and `DtrHider_`; commands use `dtr_controller_` and `dtr_hider_` (CSS recording
commands use `css_dtr_controller_`). No old-name aliases are registered.
BotRandomizer retains its current unified provider, API, name and packaging.

The GUI retires old DTR files only when the previous receipt still owns them.
An externally replaced native/managed DLL, dependency manifest or VDF preserves
the entire old component group. Without a receipt, upstream paths are untouched.
The old dedicated `DemoTracerBotHider` provider is still retired. Native hider
configuration and controller recordings are copied to the new namespace only
for receipt-owned installations, without replacing existing user data; rollback
restores the previous installation. Manual installers must make this ownership
distinction themselves and migrate their configuration before starting the server.

KHook coordinates shared detours, not competing bot control or presentation
writes. Separate identities avoid file, loader, API and command collisions;
simultaneous gameplay ownership still requires compatible consumer behavior.

Initialize the parser checkout:

```powershell
git clone --recurse-submodules https://github.com/unicbm/demotracer.git
# Or, inside an existing checkout:
git submodule update --init --recursive
node tooling/scripts/check-source-layout.mjs --require-checkout
```

The source-layout check rejects nested product submodules and validates the
single parser pin. Update the parser deliberately after reviewing its fork;
do not use `git submodule update --remote` as a product update mechanism.

Update shared declarations and affected consumers together. CI selects affected
checks; shared/build changes, missing diff information and releases run the full
suite. Packaging reuses tested artifacts. GUI, Playback and API/ABI versions are
independent. BotRandomizer also produces a standalone package.

### Component history

The six former product repositories were consolidated on 2026-09-27. Their
imported commits and paths are recorded in
[component-provenance.json](../tooling/component-provenance.json). Historical
branches and source tags are retained in this repository under
`component-archive/<id>/heads/*` and `component-archive/<id>/releases/*` tags.
These tags preserve the original commit history; they are not product releases.
Older product commits still contain historical gitlinks; use the archived
component refs when reconstructing those revisions after repository retirement.

## Build and Test

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

Generated identity and cosmetic assets are local build outputs. See
[GUI data](../desktop/gui/src/data/README.md) for their sources and generation.

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

The script compiles but does not run the upstream fixture tests. To run them,
supply `test_demo.dem` with `-FixtureTests -DemoPath <path>`. See the parser's
README for fixture provenance.

Use release builds for performance measurements, with diagnostics disabled.

| Environment variable | Diagnostic |
| --- | --- |
| `DEMOTRACER_PROFILE` | Parse phases, converter channels/fallbacks, hashing and materialization timings |
| `DEMOTRACER_SPARSE_COLUMNS=0` | Disable sparse scalar collection for comparison |
| `DEMOTRACER_PROFILE_PROPERTIES=1` | Sample getters/appends once per 256 rows; includes timer overhead |

Continuous-state fields use a sequential parse channel; other fields use
parallel fullpacket segments. Their tick/entity/SteamID/round keys must match
before merging. Missing columns or alignment failures trigger a full sequential
parse. `DecodePlan::project_entity_state` avoids storing unused properties while
still consuming all wire values and retaining requested data dependencies.

### GUI acceptance

Run the install-free GUI acceptance application with its real Rust backend and
Vite hot reload:

```powershell
cd desktop\gui
pnpm run dev:acceptance
```

This runs the release Rust backend with Vite hot reload at `127.0.0.1:1420`.
`pnpm dev` is an alias. Use `dev:debug` for Rust debugging or `dev:web` for
frontend-only layout work; a browser has no Tauri IPC.

For a standalone release executable that runs without Vite, use the Tauri
build entry point from `desktop/gui`:

```powershell
node node_modules/@tauri-apps/cli/tauri.js build --no-bundle --ci -- --locked
```

This embeds the frontend using Tauri's `custom-protocol`. Bare
`cargo build --release` may still produce a WebView that loads localhost.
Validate the standalone executable with Vite stopped: check the page renders
and a read-only Tauri command succeeds.

GUI preferences are stored in `gui-preferences.v1.json` in Tauri's local-data
directory. `localStorage` is a startup cache, imported once if the file is
missing. The workspace background is stored separately at
`appearance/workspace-background.png`.

To refresh econ data, update the exact `@ianlucas/cs2-lib` dependency and lockfile
in `server/runtime/common/tools/cs2-lib-data`, then run `npm.cmd run generate`
there. Commit the shared output and affected consumers together; do not patch
generated item IDs or copy the catalog into consumers.

Build the supported desktop target:

```powershell
cd desktop\gui
pnpm run tauri:build --target x86_64-pc-windows-msvc -- --locked
```

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
- Weapon skins, attachments, and scoreboard alignment remain default-off and
  demo-backed. DemoTracer may only submit complete cosmetic plans through the
  BotRandomizer v3 API. BotRandomizer is the only cosmetic entity writer and
  consumes plans during natural spawn/item construction; DemoTracer must not
  add a parallel econ/model/bodygroup repair path.

### Replay slot lifecycle

`ReplaySlotRegistry` owns loaded, claimed, and playing slot state:

- `Loaded`: the native replay remains available, but DemoTracer no longer owns
  gameplay or inventory writes for the slot.
- `Claimed`: the slot is loaded and DemoTracer may perform preparation writes.
- `Playing`: native playback has started and DemoTracer still owns the slot.

Loading starts a new claimed epoch. Starting playback preserves that epoch.
Handoff, stop, finish, or failure releases the slot back to `Loaded` and starts
a new epoch, invalidating callbacks captured by the prior owner. Unload removes
the slot entirely. Code outside the registry must not maintain parallel loaded,
owned, or playing collections.

Delayed writes verify the captured slot epoch and identity generation. Slot
work coalesces by slot, operation, epoch, and identity generation; presentation
and C4 work coalesce across spawn events. Stale callbacks cannot write to a
new slot incarnation.

Round loading establishes ownership and companion writer leases before spawn
callbacks, but defers pawn inventory and entity reconstruction until the live
pawns are ready. Companion lease replacement is transactional across the roster:
intermediate per-slot metadata changes do not publish partial claim sets. Freeze
pre-roll performs full pawn preparation at most once for each pre-roll token.
Remaining readiness checks use a bounded 50 ms cadence; polls must not perform
full-roster inventory or entity reconstruction.

`test-css.ps1` runs the managed regression suite and checks source boundaries
and file-size limits. Keep the plugin entry point small, the companion API
contract-only, and ownership state in `ReplaySlotRegistry` / `ReplayPlanState`.

## Packaging

The public release contains the Windows x64 GUI installer and the currently
advertised Playback bundle. Their component versions may differ for a GUI-only
hotfix:

- `demotracer-gui-v<gui-version>.exe`: NSIS desktop installer.
- `demotracer-css-v<playback-version>.zip`: compatible CS2 plugin bundle.

The updater verifies signed GUI and Playback downloads before installation.
See [Online behavior](ONLINE_SERVICES.md) for requests and user confirmation.

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
the same, the CSS bundle. It creates two output directories:

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
