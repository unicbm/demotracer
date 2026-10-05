# Development

## Source map

| Path | Responsibility |
| --- | --- |
| `desktop/gui/` | React UI, Tauri bridge, installer and updater |
| `desktop/converter/` | Rust demo analysis, `.dtr` export and validation |
| `third_party/demoparser/` | Pinned parser submodule |
| `server/plugins/DemoTracer/` | Managed playback, commands and tests |
| `server/runtime/dtr-controller/` | Native movement, input and replay control |
| `server/runtime/dtr-hider/` | Native identity runtime and managed presentation provider |
| `server/runtime/BotRandomizer/` | Cosmetic entity writer and API |
| `server/runtime/common/` | Shared native utilities, companion API, contracts and generated econ data |
| `shared/contracts/` | Product compatibility and telemetry contracts |
| `tooling/scripts/` | Validation, packaging and release automation |

The GUI calls the converter crate directly; there is no public converter CLI.
Only the parser is a submodule. Component origins and archived history are in
[component-provenance.json](../tooling/component-provenance.json).

## Build and Test

Use Windows x64, PowerShell 7, Rust stable/MSVC, Node.js 22, pnpm 11.9,
.NET 10 and Tauri's Windows prerequisites. Native builds additionally require
CMake, Visual Studio Build Tools and the [server SDKs](../server/README.md#shared-hook-runtime).

From the repository root:

```powershell
git submodule update --init --recursive
node tooling/scripts/check-source-layout.mjs --require-checkout
pnpm --dir desktop/gui install --frozen-lockfile
```

The GUI's generated catalogs are tracked. A normal build needs no external
identity-data checkout; [GUI data](../desktop/gui/src/data/README.md) describes updates.

Run checks for the affected component:

```powershell
# Converter
pwsh -NoProfile -File desktop/converter/tools/check.ps1

# GUI
pnpm --dir desktop/gui run check
pnpm --dir desktop/gui test
cargo test --manifest-path desktop/gui/src-tauri/Cargo.toml --locked

# Managed playback and product contracts
./tooling/scripts/test-css.ps1
./tooling/scripts/check-release-contract.ps1
git diff --check
```

Each runtime has a `tools/check.ps1`; the controller and hider accept
`-NativeBuild` to build the SDK-dependent plugin as well as their Release tests.
The [common check](../server/runtime/common/README.md) covers shared native code,
the companion API and generated econ data. Shared/build changes and releases
require the full suite. Engine hooks and playback also need a matched-server smoke test.

Parser tests: `pwsh -NoProfile -File third_party/demoparser/tools/check.ps1`.
Add `-FixtureTests -DemoPath <path>` to run upstream tests with your own demo.

### GUI development

From `desktop/gui`:

```powershell
pnpm run dev:acceptance
pnpm run tauri:build --target x86_64-pc-windows-msvc -- --locked
```

`dev:acceptance` runs the Release Rust backend with Vite hot reload.
Use `dev:debug` for Rust debugging or `dev:web` for frontend-only work without Tauri IPC.
The build command produces an NSIS installer. For only the executable, use
`pnpm tauri build --no-bundle --ci -- --locked`; bare `cargo build` does not
perform the frontend embedding step. Verify a standalone build with Vite stopped.

Preferences live in `gui-preferences.v1.json` in Tauri's local-data directory;
the workspace background lives in `appearance/workspace-background.png`.

### Parser diagnostics

Use Release builds for performance measurements. Enable diagnostics only when needed:

| Environment variable | Effect |
| --- | --- |
| `DEMOTRACER_PROFILE` | Parse, conversion, hashing and materialization timings |
| `DEMOTRACER_SPARSE_COLUMNS=0` | Disable sparse scalar collection for comparison |
| `DEMOTRACER_PROFILE_PROPERTIES=1` | Sample getters/appends once per 256 rows; adds timer overhead |

## Change boundaries

- Update readers, writers and [playback-contract.v1.json](../shared/contracts/playback-contract.v1.json)
  together when changing formats or APIs. GUI, Playback and API/ABI versions are independent.
- Reuse one complete `ParsedDemo` across analysis and export; round selection
  happens after parsing. Stored replay evidence must remain bit-exact.
- BotRandomizer owns cosmetic entity writes. DemoTracer submits demo-backed
  plans; cosmetics and scoreboard alignment stay opt-in.
- `ReplaySlotRegistry` owns loaded, claimed and playing slots. Delayed work must
  match the current ownership epoch and identity generation. Stop, finish,
  handoff, failure and unload release control; human players are never targets.
- Movement uses native input hooks. See the [controller](../server/runtime/dtr-controller/README.md)
  for movement and handoff behavior, and [FORMAT.md](FORMAT.md) for replay semantics.
- Output promotion uses a target-scoped lock. Archive metadata updates must honor
  `writeRevision` so stale work cannot overwrite newer edits.
- Generate shared econ data through [cs2-lib-data](../server/runtime/common/tools/cs2-lib-data/README.md);
  do not edit generated IDs or copy catalogs into consumers.

## Automated updates

`Update dependencies` runs weekly or manually. Renovate handles npm/pnpm,
Cargo, NuGet and the parser pin; Dependabot handles Actions. Bot PRs require
review and workflow approval. Native SDK/KHook pins, engine profiles,
CounterStrikeSharp, the Zstd decoder and ABI/API contracts are updated together manually.

`Prepare version PR` accepts independent GUI, Playback and converter versions.
A GUI bump requires Chinese and English release notes. It updates the pending
`codex/prepare-version` PR and explicitly dispatches full CI for that branch.
GitHub does not trigger `pull_request` workflows for PRs created with
`GITHUB_TOKEN`; the explicit dispatch covers the bot's commit.

## GitHub bot releases

After merging the version PR, run **Release NSIS** on `main` with `publish`
enabled. The bot runs full CI, builds and signs the installer, creates a GitHub
draft, publishes and verifies the stable R2 updater, then publishes the GitHub
release. It reads component versions and bilingual notes from the checkout;
there is no separate version to type or local build to upload. Normal tag builds
and dispatches without `publish` only produce build artifacts.

For a GUI hotfix, change only `gui_version` in **Prepare version PR**. Leave
Playback unchanged: the release workflow downloads its existing immutable ZIP
and signature. When GUI and Playback versions match, it reuses the bundle
already assembled by full CI instead of rebuilding the native runtimes.

Configure the `release` GitHub environment once, with deployment branches limited
to `main` and release tags, and these Secrets:

| Secret | Purpose |
| --- | --- |
| `TAURI_SIGNING_PRIVATE_KEY` | Existing updater private key matching the public key shipped in the app |
| `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` | Updater key password, if set |
| `CLOUDFLARE_API_TOKEN` | Token authorized to upload to the existing release R2 bucket |
| `CLOUDFLARE_ACCOUNT_ID` | Account owning the release bucket |
| `WINDOWS_CERTIFICATE_PFX_BASE64` | Base64 Windows code-signing PFX |
| `WINDOWS_CERTIFICATE_PASSWORD` | PFX password |

The `allow_unsigned_installer` input explicitly permits releases without Windows
Authenticode when a certificate is unavailable. Updater signing is always
required. Keep the existing updater key so installed clients can verify updates.
The bot uses `GITHUB_TOKEN` for GitHub releases; no personal access token is needed.

Publication is serialized and refuses versions at or below stable or an existing
GitHub release. If publication fails after creating a draft, inspect the retained
`demotracer-release-<version>` artifact and draft before retrying. If R2 is already
published, publish the existing GitHub draft; do not rebuild or overwrite the
immutable version. If R2 is not published, finish publishing the retained signed
payloads with `publish-r2.ps1`, then publish that draft.

## Packaging

Build native runtimes first. From a clean checkout, provide the updater signing
key through `TAURI_SIGNING_PRIVATE_KEY` (and `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`
when needed), then run:

```powershell
./tooling/scripts/package-release.ps1 `
  -Version <gui-version> `
  -PlaybackVersion <playback-version> `
  -RuntimePackage server/runtime/dtr-controller/.build/native/package `
  -BotHiderRuntimePackage server/runtime/dtr-hider/.build/native/package `
  -CertificateThumbprint <certificate-thumbprint>
```

Omit `-PlaybackVersion` when it matches the GUI. For a GUI-only hotfix, provide
the existing Playback ZIP and `.sig` in `dist/`; the packager reuses them.
Without an Authenticode certificate, use `-AllowUnsignedInstaller` instead of
`-CertificateThumbprint`. Updater signing is still required.

| Output | Destination |
| --- | --- |
| `dist/release-v<version>/` | GitHub assets: `demotracer-gui-v<gui-version>.exe` and `demotracer-css-v<playback-version>.zip` |
| `dist/updater-v<version>/` | R2 payloads, signatures, `latest.json` and checksums |

Release text comes from `tooling/release/release-notes.v<version>.json`;
`-ReleaseNotesZh` and `-ReleaseNotes` override it. Keep JSON notes for any Playback
version reused by a hotfix. Historical GitHub release drafts can be removed after publishing.

Publish only the updater directory to R2:

```powershell
./tooling/scripts/publish-r2.ps1 -Version <gui-version> -PlaybackVersion <playback-version>
```

Before a public push, inspect the staged files for local paths, demos, archives,
logs, credentials, signing material and build output. Preserve licenses and
source notices; see [Contributing](../CONTRIBUTING.md).
