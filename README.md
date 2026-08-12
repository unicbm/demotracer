<p align="center">
  <img src="desktop/gui/brand/demotracer-logo-color.svg" alt="DemoTracer logo" width="128" height="128">
</p>

<h1 align="center">CS2 DemoTracer</h1>

<p align="center">
  Turn Counter-Strike 2 demos into bot-executable replays, then inspect, organize,
  and play them from one Windows desktop app.
</p>

<p align="center">
  <a href="https://github.com/unicbm/demotracer/releases/latest"><strong>Download the latest release</strong></a>
  · <a href="docs/README.md">Documentation</a>
  · <a href="docs/DEVELOPMENT.md">Development</a>
</p>

<p align="center">
  <img src="https://github.com/unicbm/demotracer/actions/workflows/ci.yml/badge.svg" alt="CI status">
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D4" alt="Windows x64">
  <img src="https://img.shields.io/badge/license-AGPL--3.0--only-blue" alt="AGPL-3.0-only">
</p>

DemoTracer is a matched desktop and server playback system. The desktop app
reads CS2 demos, explains what can be replayed, creates compact `.dtr` archives,
and builds the commands needed to reproduce selected rounds through bots on a
local CS2 server.

> The supported product is the Windows x64 Tauri desktop app and its matched
> playback bundle. Parsing and export run through the Rust backend linked into
> the application; there is no supported converter CLI.

## One App, the Complete Replay Workflow

| | Desktop workflow |
| --- | --- |
| **Import** | Open one demo for detailed round selection, or select up to eight demos for parallel batch conversion. Split `-p1` / `-p2` match recordings are recognized and merged automatically. |
| **Analyze** | Review the match, roster, score, round quality, player configuration, and available demo-backed evidence before exporting anything. |
| **Archive** | Keep converted matches in a searchable local replay library organized by map, team, player, date, and custom notes. Existing archives can be imported, repaired, or re-linked to moved source demos. |
| **Play** | Choose a starting round, single-round or sequence playback, identity and fidelity options, then copy the generated server command. |
| **Maintain** | Inspect a CS2 installation, verify runtime contracts and plugin conflicts, install or roll back a matched playback ZIP, and edit the server configuration without discarding unknown fields. |

The interface supports Simplified Chinese and English, light and dark themes,
adjustable interface scaling, task progress and completion feedback, and a
local-first library. Demo parsing and replay generation run on the local
machine; the limited optional network behavior is documented in
[Online behavior](docs/ONLINE_SERVICES.md).

## What Gets Replayed

DemoTracer reconstructs demo-backed player state rather than drawing a route on
top of the game. Depending on the source demo and selected options, a replay can
include:

- movement, view angles, buttons, and available subtick command state;
- weapon state, purchases, drops, and selected high-fidelity combat events;
- grenade throws and projectile alignment;
- freeze-time pre-roll, round score, player names, and team presentation;
- optional demo voice and chat;
- optional, evidence-gated Steam identity, avatars, crosshair, viewmodel,
  agents, gloves, knives, stickers, charms, music kits, and scoreboard details.

Movement playback uses maintained movement and input hooks. Teleporting is not
the primary replay mechanism. Presentation features remain explicit,
demo-backed, and conservative when the source does not contain enough evidence.

## Playback Results

<table>
  <tr>
    <td align="center" width="50%">
      <img src="docs/media/first-person-replay-nuke.gif" alt="First-person CS2 bot replay on Nuke" width="100%"><br>
      <sub>First-person route replay</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/media/first-person-replay-route.gif" alt="First-person CS2 bot replay through an indoor route" width="100%"><br>
      <sub>Indoor route replay</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="50%">
      <img src="docs/media/mirage-opening-replay.gif" alt="Mirage multi-bot opening replay" width="100%"><br>
      <sub>Mirage multi-bot opening</sub>
    </td>
    <td align="center" width="50%">
      <img src="docs/media/mirage-projectile-smokes.gif" alt="Projectile-aligned Mirage smoke replay" width="100%"><br>
      <sub>Projectile-aligned Mirage smokes</sub>
    </td>
  </tr>
</table>

## Get Started

### 1. Download the matched release

Use the artifacts attached to the
[latest official release](https://github.com/unicbm/demotracer/releases/latest):

- `demotracer-gui-vVERSION.exe` — Windows NSIS desktop installer.
- `demotracer-css-vVERSION.zip` — matched CS2 playback plugins and runtimes.

Only artifacts attached by `unicbm` to this repository's GitHub Releases are
official DemoTracer builds. See the
[Trademark and Official Build Policy](TRADEMARKS.md).

### 2. Prepare playback

The desktop app runs on Windows 10 and Windows 11 x64 and requires Microsoft
Edge WebView2. Parsing, analysis, conversion, and library management need no
Python, Node.js, Rust, or local build toolchain after installation.

Playback additionally requires a local Windows x64 CS2 server with
[Metamod:Source](https://www.sourcemm.net/) and
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp). In the
app, open **Settings → Install & environment** to select the CS2 folder, inspect
the installation, and install the matched playback ZIP.

### 3. Convert and play

1. Choose one or more `.dem` files.
2. Review the analysis and select the rounds to export.
3. Open the resulting archive from the local library.
4. Select a round and playback options, copy the generated command, and run it
   in the local replay server console.

Public playback commands and server defaults are documented in
[Commands](docs/COMMANDS.md).

## Supported Scope

DemoTracer is local replay tooling for research, content creation, tactical
review, analysis, and plugin development. It is not intended for matchmaking or
cheating, and replay control must never be assigned to human players.

The maintained product target is Windows x64. Desktop releases, playback
bundles, `.dtr` files, manifests, native runtimes, and companion APIs are
versioned together. The release truth source is
[shared/contracts/playback-contract.v1.json](shared/contracts/playback-contract.v1.json),
and the binary format and decoder limits are documented in
[`.dtr` Format](docs/FORMAT.md).

## Documentation

- [Commands](docs/COMMANDS.md) — playback commands, options, and runtime
  defaults.
- [`.dtr` Format](docs/FORMAT.md) — file layout, versions, validation, and
  decoder limits.
- [Online behavior](docs/ONLINE_SERVICES.md) — update checks, Steam profile
  lookups, and local data policy.
- [Anonymous telemetry](docs/TELEMETRY.md) — opt-in data contract, privacy
  boundaries, retention, and aggregate reporting.
- [Development](docs/DEVELOPMENT.md) — architecture, dependencies, source
  builds, validation, and release packaging.
- [Contributing](CONTRIBUTING.md) — contribution workflow and repository
  boundaries.

<details>
<summary><strong>Build and validate from source</strong></summary>

The maintained source target is Windows x64. Install Rust stable, Node.js 22,
pnpm 11.9, .NET 10, and the Tauri Windows prerequisites, then run:

```powershell
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
```

Native BotController and BotHider builds additionally require the local CS2,
Metamod, and SDK toolchain. Follow
[Development](docs/DEVELOPMENT.md) for the maintained build and packaging path.

</details>

<details>
<summary><strong>Repository layout</strong></summary>

- `desktop/gui` — Tauri and React application plus its Rust desktop backend.
- `desktop/converter` — Rust parsing, analysis, replay synthesis, archive
  export, and validation.
- `server/plugins` — CounterStrikeSharp playback orchestration and companion
  API.
- `server/runtime` — maintained BotController and BotHider native runtimes.
- `shared` — versioned compatibility contracts and generated runtime metadata.
- `third_party` — vendored dependencies, provenance, and license notices.
- `tooling` — validation, packaging, and release automation.

</details>

## Credits and License

DemoTracer builds on
[CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller),
[CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider),
[demoparser](https://github.com/LaihoE/demoparser),
[minidemo-encoder](https://github.com/csgowiki/minidemo-encoder),
Metamod:Source, and CounterStrikeSharp. `minidemo-encoder` provided an early
foundation for reconstructing continuous movement from discrete demo
trajectories.

First-party source is licensed under **AGPL-3.0-only**. Vendored components and
datasets retain their recorded licenses and attribution. The code license does
not grant rights to misrepresent modified builds as official releases.

Selected information-layout and demo-presentation references draw from
[CS2 Insight Agent](https://github.com/DrEAmSs59/CS2-insight-agent) under direct,
paid, project-specific authorization from DrEAmSs59. The original reference
material remains the property of that project and is not granted to third
parties under DemoTracer's AGPL-3.0-only license. See the maintained
[source and authorization notice](docs/CS2_INSIGHT_GUI_REFERENCE.md).
