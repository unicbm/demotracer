# DemoTracer public playback source

Use main for routine work. This repository contains AGPL playback modules,
shared runtime code and DTR v12 contracts. Preserve upstream licenses.

Never add desktop GUI/converter sources, private workflows, private configuration,
demos, recordings, credentials, build outputs or private repository history.
Do not merge the private development branch. Keep DTR writer/reader contract at 12.

Run node tooling/scripts/check-public-source.mjs, then
pwsh -NoProfile -File tooling/scripts/check-playback-contract.ps1 and
pwsh -NoProfile -File tooling/scripts/test-css.ps1. Native changes also require
the affected runtime's tools/check.ps1 -NativeBuild and Release CTest suite.
Packaging uses tooling/scripts/package-server.ps1 and needs no desktop checkout.

Never assign replay control to humans. Release all replay ownership, injected
input, locks and transient state on every completion and failure path.
Preserve user configuration. Inspect the staged files before every public push.
