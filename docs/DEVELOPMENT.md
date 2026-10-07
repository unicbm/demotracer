# Playback development

Requires Windows x64, .NET 10, Node.js 22, CMake 3.28+ and MSVC.

```powershell
node tooling/scripts/check-public-source.mjs
pwsh -NoProfile -File tooling/scripts/check-playback-contract.ps1
pwsh -NoProfile -File tooling/scripts/test-css.ps1
```

For native builds, use the Metamod/HL2SDK pins in
`server/runtime/common/contracts/{hook-runtime,native-toolchain}.v1.json`.
Set `MMSOURCE_DEV`, `HL2SDKCS2` and `PROTOC`, then run:

```powershell
pwsh -NoProfile -File server/runtime/dtr-controller/tools/check.ps1 -NativeBuild
pwsh -NoProfile -File server/runtime/dtr-hider/tools/check.ps1 -NativeBuild
pwsh -NoProfile -File server/runtime/BotRandomizer/tools/check.ps1
```

To package locally, commit reviewed changes and stage the tested native builds:

```powershell
foreach ($module in @('dtr-controller', 'dtr-hider')) {
    $root = "server/runtime/$module"
    New-Item -ItemType Directory -Force "$root/build/package" | Out-Null
    Copy-Item "$root/.build/native/package/*" "$root/build/package" -Recurse -Force
}
pwsh -NoProfile -File tooling/scripts/package-server.ps1 -Version 1.5.8 -SkipCssBuild
```

Use the current plugin version for `-Version`. External packages require a matching receipt.
