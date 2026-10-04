# CS2 DemoTracer converter library

This product module reads CS2 demos and reads/writes .dtr recordings for the
GUI. The Cargo package remains `cs2-demotracer`, the library remains
`cs2_demotracer`, and there is no supported converter CLI.

Initialize `third_party/demoparser` from the repository root, then run
`pwsh -NoProfile -File desktop/converter/tools/check.ps1`.
The check runs formatting, Release compilation and tests with the pinned parser.
Contracts and embedded catalogs come directly from
`server/runtime/common`; no network generation or duplicate snapshots are used.

Preserve [PROVENANCE.md](PROVENANCE.md), third-party notices and the AGPL-3.0-only
license. Changes to formats require matching readers, writers and product checks.
