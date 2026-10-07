# Playback development

Required: Windows x64, .NET 10, Node.js 22, CMake 3.28+, and the supported MSVC
toolchain for native builds. See the pinned contracts in server/runtime/common.
No GUI, converter, Rust parser or private repository access is required.

```powershell
node tooling/scripts/check-public-source.mjs
pwsh -NoProfile -File tooling/scripts/check-playback-contract.ps1
pwsh -NoProfile -File tooling/scripts/test-css.ps1
```

For native builds, initialize the Metamod and HL2SDK revisions recorded in
`server/runtime/common/contracts/hook-runtime.v1.json` and
`server/runtime/common/contracts/native-toolchain.v1.json`. Set MMSOURCE_DEV,
HL2SDKCS2 and PROTOC to those local dependencies, then run:

```powershell
pwsh -NoProfile -File server/runtime/dtr-controller/tools/check.ps1 -NativeBuild
pwsh -NoProfile -File server/runtime/dtr-hider/tools/check.ps1 -NativeBuild
pwsh -NoProfile -File server/runtime/BotRandomizer/tools/check.ps1
```

CI builds the five public modules and assembles the matched playback package.
For a local package, commit the reviewed changes, then stage the freshly tested
native packages into the same locations used by CI:

```powershell
foreach ($module in @('dtr-controller', 'dtr-hider')) {
    $root = "server/runtime/$module"
    New-Item -ItemType Directory -Force "$root/build/package" | Out-Null
    Copy-Item "$root/.build/native/package/*" "$root/build/package" -Recurse -Force
}
pwsh -NoProfile -File tooling/scripts/package-server.ps1 -Version 1.5.8 -SkipCssBuild
```

Use the current plugin version for `-Version`. External package locations require
a matching source receipt; arbitrary DLL directories are not accepted.
The DTR format remains v12; source repository separation changes no ABI.
