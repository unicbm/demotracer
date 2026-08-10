# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Read-Text([string]$RelativePath) {
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "release contract source not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

function Read-RegexValue([string]$RelativePath, [string]$Pattern, [string]$Label) {
    $match = [regex]::Match((Read-Text $RelativePath), $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $match.Success) {
        throw "could not read $Label from $RelativePath"
    }
    return $match.Groups[1].Value
}

function Assert-Equal([string]$Label, [string]$Actual, [string]$Expected) {
    if (-not [System.StringComparer]::Ordinal.Equals($Actual, $Expected)) {
        throw "$Label mismatch: expected '$Expected', found '$Actual'"
    }
}

function Assert-PathAbsent([string]$RelativePath, [string]$Label) {
    $path = Join-Path $repoRoot $RelativePath
    if (Test-Path -LiteralPath $path) {
        throw "$Label must not be present in the supported 1.0 product: $path"
    }
}

function Assert-TextAbsent([string]$RelativePath, [string]$Pattern, [string]$Label) {
    if ([regex]::IsMatch((Read-Text $RelativePath), $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        throw "$Label must not be present in $RelativePath"
    }
}

function Assert-TextPresent([string]$RelativePath, [string]$Pattern, [string]$Label) {
    if (-not [regex]::IsMatch((Read-Text $RelativePath), $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        throw "$Label is missing from $RelativePath"
    }
}

function Read-CargoPackageVersion([string]$RelativePath, [string]$PackageName) {
    $escaped = [regex]::Escape($PackageName)
    return Read-RegexValue $RelativePath "(?ms)^\[\[package\]\]\s*\r?\nname = `"$escaped`"\s*\r?\nversion = `"([^`"]+)`"" "$PackageName lock version"
}

$contract = (Read-Text "shared\contracts\playback-contract.v1.json") | ConvertFrom-Json
$desktopPackage = (Read-Text "desktop\gui\package.json") | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$desktopPackage.version
}
$null = Read-Text "desktop\gui\pnpm-lock.yaml"
$tauriConfig = (Read-Text "desktop\gui\src-tauri\tauri.conf.json") | ConvertFrom-Json
$tauriCapability = (Read-Text "desktop\gui\src-tauri\capabilities\default.json") | ConvertFrom-Json

$versionSources = [ordered]@{
    "converter Cargo.toml" = Read-RegexValue "desktop\converter\Cargo.toml" '(?ms)^\[package\]\s*.*?^version = "([^"]+)"' "converter version"
    "converter Cargo.lock" = Read-CargoPackageVersion "desktop\converter\Cargo.lock" "cs2-demotracer"
    "desktop package.json" = [string]$desktopPackage.version
    "desktop Tauri Cargo.toml" = Read-RegexValue "desktop\gui\src-tauri\Cargo.toml" '(?ms)^\[package\]\s*.*?^version = "([^"]+)"' "desktop Tauri version"
    "desktop converter dependency lock" = Read-CargoPackageVersion "desktop\gui\src-tauri\Cargo.lock" "cs2-demotracer"
    "desktop Tauri Cargo.lock" = Read-CargoPackageVersion "desktop\gui\src-tauri\Cargo.lock" "cs2-demotracer-gui"
    "desktop tauri.conf.json" = [string]$tauriConfig.version
    "DemoTracer ModuleVersion" = Read-RegexValue "server\plugins\DemoTracer\DemoTracerPlugin.cs" 'ModuleVersion\s*=>\s*"([^"]+)"' "DemoTracer module version"
}
foreach ($entry in $versionSources.GetEnumerator()) {
    Assert-Equal $entry.Key ([string]$entry.Value) $Version
}

Assert-Equal "manifest ABI" (Read-RegexValue "desktop\converter\src\model\mod.rs" 'DEMOTRACER_ABI:\s*i32\s*=\s*(\d+)' "manifest ABI") ([string]$contract.manifest_abi)
Assert-Equal "DTR writer" (Read-RegexValue "desktop\converter\src\model\mod.rs" 'DTR_FORMAT_VERSION:\s*u32\s*=\s*(\d+)' "DTR writer") ([string]$contract.dtr_writer)
Assert-Equal "CSS minimum DTR reader" (Read-RegexValue "server\plugins\DemoTracer\BotControllerNativeTypes.cs" 'MinRecFormatVersion\s*=\s*(\d+)' "minimum DTR reader") ([string]$contract.dtr_reader.min)
Assert-Equal "CSS maximum DTR reader" (Read-RegexValue "server\plugins\DemoTracer\BotControllerNativeTypes.cs" 'RecFormatVersion\s*=\s*(\d+)' "maximum DTR reader") ([string]$contract.dtr_reader.max)
Assert-Equal "CSS native ABI" (Read-RegexValue "server\plugins\DemoTracer\BotControllerNativeTypes.cs" 'ExpectedAbiVersion\s*=\s*(\d+)' "CSS native ABI") ([string]$contract.bot_controller.abi_major)
Assert-Equal "runtime native ABI" (Read-RegexValue "server\runtime\BotController\src\common\exports.cpp" 'kBotControllerAbiMajor\s*=\s*(\d+)' "runtime native ABI") ([string]$contract.bot_controller.abi_major)
Assert-Equal "minimum native ABI minor" (Read-RegexValue "server\plugins\DemoTracer\DemoTracerRuntimeHealth.cs" 'MinimumBotControllerAbiMinor\s*=\s*(\d+)' "minimum native ABI minor") ([string]$contract.bot_controller.min_abi_minor)

$runtimeMinor = [int](Read-RegexValue "server\runtime\BotController\src\common\exports.cpp" 'kBotControllerAbiMinor\s*=\s*(\d+)' "runtime native ABI minor")
if ($runtimeMinor -lt [int]$contract.bot_controller.min_abi_minor) {
    throw "runtime native ABI minor $runtimeMinor is below required $($contract.bot_controller.min_abi_minor)"
}

Assert-Equal "DemoTracer companion API" (Read-RegexValue "server\plugins\DemoTracer\BotControllerNativeTypes.cs" 'DemoTracerApiVersion\s*=\s*(\d+)' "DemoTracer companion API") ([string]$contract.demotracer.companion_api)
Assert-Equal "BotHider API" (Read-RegexValue "server\runtime\BotHider\csharp\BotHiderApi\IBotHiderApi.cs" 'ApiVersion\s*=\s*(\d+)' "BotHider API") ([string]$contract.bot_hider.api)
Assert-Equal "DemoTracer target framework" (Read-RegexValue "server\plugins\DemoTracer\DemoTracer.csproj" '<TargetFramework>([^<]+)</TargetFramework>' "DemoTracer target framework") ([string]$contract.counterstrikesharp.target_framework)
Assert-Equal "CounterStrikeSharp minimum version" (Read-RegexValue "server\plugins\DemoTracer\DemoTracer.csproj" 'CounterStrikeSharp\.API" Version="([^"]+)"' "CounterStrikeSharp version") ([string]$contract.counterstrikesharp.minimum_version)

Assert-PathAbsent "desktop\converter\src\main.rs" "converter CLI entrypoint"
Assert-PathAbsent "desktop\converter\src\cli" "converter CLI module"
Assert-PathAbsent "desktop\converter\src\pool.rs" "round-pool export module"
Assert-PathAbsent "desktop\converter\src\workflows\pool.rs" "round-pool workflow"
Assert-TextAbsent "desktop\converter\Cargo.toml" '(?m)^\s*\[\[bin\]\]' "converter binary target"
Assert-TextAbsent "desktop\converter\src\model\mod.rs" 'RoundPool(?:Manifest|Candidate)' "round-pool manifest model"
Assert-TextAbsent "server\plugins\DemoTracer\DemoTracerPlayback.cs" 'dtr_(?:run_pool|pool_restart|stop_pool)|case\s+"pool"' "round-pool playback command"
Assert-TextAbsent "docs\COMMANDS.md" 'pool_manifest|dtr_(?:go|arm)\s+pool' "round-pool public documentation"

if (-not [bool]$tauriConfig.bundle.active -or @($tauriConfig.bundle.targets) -notcontains "nsis") {
    throw "desktop release bundling must target NSIS"
}
Assert-Equal "NSIS install mode" ([string]$tauriConfig.bundle.windows.nsis.installMode) "currentUser"
if (@($tauriCapability.permissions) -notcontains "process:default") {
    throw "desktop process permission is missing"
}
if (@($tauriCapability.permissions) -notcontains "updater:default") {
    throw "desktop updater permission is missing"
}
Assert-TextPresent "desktop\gui\package.json" '"@tauri-apps/plugin-updater"\s*:\s*"2\.10\.1"' "desktop updater dependency"
Assert-TextPresent "desktop\gui\package.json" '"@tauri-apps/plugin-process"\s*:' "desktop process dependency"
Assert-TextPresent "desktop\gui\src-tauri\Cargo.toml" '^tauri-plugin-updater\s*=\s*"=2\.10\.1"' "Tauri updater dependency"
Assert-TextPresent "desktop\gui\src-tauri\Cargo.toml" '^minisign-verify\s*=\s*"=0\.2\.5"' "playback signature verifier"
Assert-TextPresent "desktop\gui\src-tauri\tauri.conf.json" 'https://releases\.detr\.site/channels/stable/latest\.json' "stable updater endpoint"
Assert-Equal "updater install mode" ([string]$tauriConfig.plugins.updater.windows.installMode) "passive"
$updaterPublicKey = (Read-Text "tooling\release\updater-public-key.txt").Trim()
Assert-Equal "Tauri updater public key" ([string]$tauriConfig.plugins.updater.pubkey) $updaterPublicKey
Assert-TextPresent "tooling\scripts\package-converter.ps1" 'CertificateThumbprint' "Authenticode configuration"
Assert-TextPresent "tooling\scripts\package-converter.ps1" 'demotracer-gui-v\$Version' "GUI release asset name"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'demotracer-css-v\$Version' "CSS release asset name"
Assert-TextPresent "tooling\scripts\package-release.ps1" '\$cssSignatureName\s*=\s*"\$cssName\.sig"' "CSS updater signature"
Assert-TextPresent "tooling\scripts\package-release.ps1" 'playback\s*=\s*\[ordered\]@\{' "playback updater manifest"
Assert-TextPresent "tooling\scripts\publish-r2.ps1" 'demotracer-css-v\$Version\.zip' "published CSS updater asset"
Assert-TextPresent "tooling\scripts\publish-r2.ps1" 'latest\.playback\.sha256' "published CSS hash verification"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'addons\\counterstrikesharp\\shared\\BotRandomizerApi' "packaged BotRandomizer API directory"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'Copy-RequiredFile[^\r\n]+BotRandomizerApi\.dll[^\r\n]+BotRandomizerApi\.dll' "packaged BotRandomizer API assembly"
Assert-PathAbsent "tooling\scripts\package-gui-update-test.ps1" "GUI updater test packager"
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "tooling\scripts\publish-r2.ps1") -PathType Leaf)) {
    throw "R2 updater publisher is missing"
}

& (Join-Path $PSScriptRoot "check-first-party-headers.ps1") -RepoRoot $repoRoot

Write-Host "Release contract verified for v$Version."
