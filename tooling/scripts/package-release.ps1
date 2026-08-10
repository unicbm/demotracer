# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$UpdaterPublicKeyPath = "tooling\release\updater-public-key.txt",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$ReleaseNotes = "",
    [string]$ReleaseNotesZh = "",
    [switch]$AllowUnsignedInstaller,
    [string]$DotnetPath = "",
    [string]$RuntimePackage = "server\runtime\BotController\build\package",
    [string]$BotHiderRuntimePackage = "server\runtime\BotHider\build\package",
    [switch]$SkipGuiBuild,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRootPath = Join-Path $repoRoot $OutputRoot
$desktopRoot = Join-Path $repoRoot "desktop\gui"
$publishRootPath = Join-Path $outputRootPath "release-v$Version"
$updaterRootPath = Join-Path $outputRootPath "updater-v$Version"
$guiName = "demotracer-gui-v$Version.exe"
$cssName = "demotracer-css-v$Version.zip"
$releaseBase = $ReleaseBaseUrl.TrimEnd('/')
$releaseUri = $null
if (-not [System.Uri]::TryCreate($releaseBase, [System.UriKind]::Absolute, [ref]$releaseUri) -or
    $releaseUri.Scheme -ne "https" -or
    [string]::IsNullOrWhiteSpace($releaseUri.Host)) {
    throw "ReleaseBaseUrl must be an absolute HTTPS URL."
}
if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = "Stability improvements and bug fixes."
}
if ([string]::IsNullOrWhiteSpace($ReleaseNotesZh)) {
    $ReleaseNotesZh = "稳定性改进与问题修复。"
}

& (Join-Path $PSScriptRoot "assert-clean-worktree.ps1") -RepoRoot $repoRoot
& (Join-Path $PSScriptRoot "check-release-contract.ps1") -Version $Version

$guiArgs = @{
    Version = $Version
    OutputRoot = $OutputRoot
    UpdaterPublicKeyPath = $UpdaterPublicKeyPath
    CertificateThumbprint = $CertificateThumbprint
    TimestampUrl = $TimestampUrl
}
if ($AllowUnsignedInstaller) {
    $guiArgs.AllowUnsignedInstaller = $true
}
if ($SkipGuiBuild) {
    $guiArgs.SkipBuild = $true
}
& (Join-Path $PSScriptRoot "package-converter.ps1") @guiArgs

$cssArgs = @{
    Version = $Version
    Configuration = $Configuration
    OutputRoot = $OutputRoot
    DotnetPath = $DotnetPath
    RuntimePackage = $RuntimePackage
    BotHiderRuntimePackage = $BotHiderRuntimePackage
}
if ($SkipCssBuild) {
    $cssArgs.SkipCssBuild = $true
}
if ($IncludeSymbols) {
    $cssArgs.IncludeSymbols = $true
}
& (Join-Path $PSScriptRoot "package-server.ps1") @cssArgs

$assetNames = @($guiName, $cssName)
foreach ($assetName in $assetNames) {
    $assetPath = Join-Path $outputRootPath $assetName
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "release asset not found: $assetPath"
    }
}

$cssSignatureName = "$cssName.sig"
$cssSignaturePath = Join-Path $outputRootPath $cssSignatureName
Push-Location $desktopRoot
try {
    $signerArgs = @("tauri", "signer", "sign")
    if ([string]::IsNullOrEmpty($env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD)) {
        $signerArgs += "--password="
    }
    $signerArgs += (Join-Path $outputRootPath $cssName)
    & pnpm.cmd @signerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pnpm.cmd failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath $cssSignaturePath -PathType Leaf)) {
    throw "CSS updater signature not found: $cssSignaturePath"
}

if (Test-Path -LiteralPath $publishRootPath) {
    Remove-Item -LiteralPath $publishRootPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRootPath | Out-Null
foreach ($assetName in $assetNames) {
    Copy-Item -LiteralPath (Join-Path $outputRootPath $assetName) -Destination $publishRootPath -Force
}

$publishedNames = @(Get-ChildItem -LiteralPath $publishRootPath -File | Select-Object -ExpandProperty Name | Sort-Object)
$expectedPublishedNames = @($assetNames | Sort-Object)
if (Compare-Object -ReferenceObject $expectedPublishedNames -DifferenceObject $publishedNames) {
    throw "Release directory contains an unexpected asset set: $publishRootPath"
}

Write-Host "Clean release assets: $publishRootPath"

if (Test-Path -LiteralPath $updaterRootPath) {
    Remove-Item -LiteralPath $updaterRootPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $updaterRootPath | Out-Null

$guiSignatureName = "$guiName.sig"
$guiSignaturePath = Join-Path $outputRootPath $guiSignatureName
if (-not (Test-Path -LiteralPath $guiSignaturePath -PathType Leaf)) {
    throw "GUI updater signature not found: $guiSignaturePath"
}
Copy-Item -LiteralPath (Join-Path $outputRootPath $guiName) -Destination $updaterRootPath -Force
Copy-Item -LiteralPath $guiSignaturePath -Destination $updaterRootPath -Force
Copy-Item -LiteralPath (Join-Path $outputRootPath $cssName) -Destination $updaterRootPath -Force
Copy-Item -LiteralPath $cssSignaturePath -Destination $updaterRootPath -Force

$publishedAt = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
$localizedReleaseNotes = [ordered]@{
    zh = $ReleaseNotesZh
    en = $ReleaseNotes
} | ConvertTo-Json -Compress
$latestManifest = [ordered]@{
    version = $Version
    notes = $localizedReleaseNotes
    pub_date = $publishedAt
    platforms = [ordered]@{
        "windows-x86_64" = [ordered]@{
            signature = (Get-Content -LiteralPath $guiSignaturePath -Raw -Encoding UTF8).Trim()
            url = "$releaseBase/releases/v$Version/$guiName"
        }
    }
    playback = [ordered]@{
        version = $Version
        url = "$releaseBase/releases/v$Version/$cssName"
        signature = (Get-Content -LiteralPath $cssSignaturePath -Raw -Encoding UTF8).Trim()
        sha256 = (Get-FileHash -LiteralPath (Join-Path $outputRootPath $cssName) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$latestManifestPath = Join-Path $updaterRootPath "latest.json"
$latestManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $latestManifestPath -Encoding UTF8

$updaterAssetNames = @($guiName, $guiSignatureName, $cssName, $cssSignatureName, "latest.json")
$checksumLines = foreach ($assetName in $updaterAssetNames) {
    $assetPath = Join-Path $updaterRootPath $assetName
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName"
}
Set-Content -LiteralPath (Join-Path $updaterRootPath "SHA256SUMS.txt") -Value $checksumLines -Encoding ASCII

$updaterNames = @(Get-ChildItem -LiteralPath $updaterRootPath -File | Select-Object -ExpandProperty Name | Sort-Object)
$expectedUpdaterNames = @($updaterAssetNames + "SHA256SUMS.txt" | Sort-Object)
if (Compare-Object -ReferenceObject $expectedUpdaterNames -DifferenceObject $updaterNames) {
    throw "Updater directory contains an unexpected asset set: $updaterRootPath"
}
Write-Host "Signed R2 updater assets: $updaterRootPath"
