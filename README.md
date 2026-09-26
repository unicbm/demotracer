# CS2 DTR converter library

The maintained [unicbm/cs2-dtr-converter](https://github.com/unicbm/cs2-dtr-converter)
library reads CS2 demos, builds replay data, and reads/writes `.dtr` recordings
for CS2 DemoTracer. Its Cargo package remains `cs2-demotracer`, and its Rust
library remains `cs2_demotracer`, so existing GUI imports remain compatible.
This repository has no converter CLI target.

## Dependencies and validation

Initialize recursive Git submodules before building:

```powershell
git submodule update --init --recursive
pwsh -NoProfile -File tools/check.ps1
```

The check script requires stable Rust with rustfmt and the platform C/C++ build
toolchain. It checks formatting and all targets, then runs Release tests with
the default parser feature and without default features. Tests are synthetic
and use temporary files; no private demo or server installation is required.

The pinned dependencies are:

| Path | Purpose |
| --- | --- |
| `.deps/demoparser` | Maintained `parser` / `csgoproto` source dependency |
| `.deps/common/contracts/replay-source-fields.v1.json` | Shared replay-source field contract regression input |
| `.deps/common/econ/cs2-lib-econ-index.v1.json` | Shared generated econ catalog embedded in the library |

These files are consumed from the checked-out dependency revisions. The build
does not retrieve or generate replacement contracts or catalogs. `Cargo.lock`
locks registry dependencies; the submodule revisions lock the source/data inputs.
Default builds enable `demoparser`; `--no-default-features` retains supported
format/model functionality and reports feature-disabled errors for parser work.

## Integration and licensing

The supported application is the CS2 DemoTracer GUI. Its build should consume a
pinned converter revision and preserve the matched `.dtr`, manifest and playback
contracts. Moving this library does not change those formats or expose a new CLI.

First-party converter code is AGPL-3.0-only; see [LICENSE](LICENSE).
[PROVENANCE.md](PROVENANCE.md) records its source and external dependencies.
