# Crosshair dependency patch

`csgo-sharecode@7.0.0.patch` corrects the current CS2 share-code layout in the
upstream package's source and distributed JavaScript. The game stores recoil in
bit 5, thickness and outline together in byte 13, and a signed 16-bit gap in
bytes 14–15. The patch also aligns import/export limits with the game.

Keep the pinned patch in `pnpm-workspace.yaml` and `pnpm-lock.yaml` so analysis,
Tools imports, share-code exports, and console settings use the same package.
Legacy CSGO code layouts remain handled by the unmodified upstream decoder.
Remove this patch when an upstream release implements these fields and passes
`src/crosshairCs2Preview.test.ts`, including the native-code export fixtures.

Upstream: <https://github.com/akiver/csgo-sharecode> (MIT license, retained in the
installed package). The patch does not include game binaries or decompiled code.
