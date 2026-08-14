# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Version = "1.0.0",
    [string]$PlaybackVersion = "",
    [string]$OutputRoot = "dist",
    [string]$UpdaterPublicKeyPath = "tooling\release\updater-public-key.txt",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowUnsignedInstaller,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRootPath = Join-Path $repoRoot $OutputRoot
$desktopRoot = Join-Path $repoRoot "desktop\gui"
$bundleRoot = Join-Path $desktopRoot "src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis"
$packageName = "demotracer-gui-v$Version"
$installerPath = Join-Path $outputRootPath "$packageName.exe"
$signaturePath = "$installerPath.sig"
$publicKeyPath = if ([System.IO.Path]::IsPathRooted($UpdaterPublicKeyPath)) {
    $UpdaterPublicKeyPath
} else {
    Join-Path $repoRoot $UpdaterPublicKeyPath
}

& (Join-Path $PSScriptRoot "assert-clean-worktree.ps1") -RepoRoot $repoRoot
$releaseContractArgs = @{ Version = $Version }
if (-not [string]::IsNullOrWhiteSpace($PlaybackVersion)) {
    $releaseContractArgs.PlaybackVersion = $PlaybackVersion
}
& (Join-Path $PSScriptRoot "check-release-contract.ps1") @releaseContractArgs

function Require-Path([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

Require-Path $publicKeyPath "Tauri updater public key"
if ([string]::IsNullOrWhiteSpace($env:TAURI_SIGNING_PRIVATE_KEY) -and
    [string]::IsNullOrWhiteSpace($env:TAURI_SIGNING_PRIVATE_KEY_PATH)) {
    $defaultPrivateKey = Join-Path $env:USERPROFILE ".tauri\cs2-demotracer.key"
    if (Test-Path -LiteralPath $defaultPrivateKey -PathType Leaf) {
        $env:TAURI_SIGNING_PRIVATE_KEY_PATH = $defaultPrivateKey
    } else {
        throw "Tauri updater private key is unavailable. Set TAURI_SIGNING_PRIVATE_KEY_PATH or TAURI_SIGNING_PRIVATE_KEY."
    }
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -and -not $AllowUnsignedInstaller) {
    throw "Authenticode certificate thumbprint is required by default. Pass -CertificateThumbprint or explicitly use -AllowUnsignedInstaller."
}
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
    $CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "CertificateThumbprint must be a 40-character hexadecimal certificate thumbprint."
}

$releaseConfigPath = ""
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $releaseConfig = [ordered]@{
        bundle = [ordered]@{
            windows = [ordered]@{
                certificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
                digestAlgorithm = "sha256"
                timestampUrl = $TimestampUrl
            }
        }
    }
    $buildConfigRoot = Join-Path $outputRootPath ".release-build"
    $releaseConfigPath = Join-Path $buildConfigRoot "tauri.release.v$Version.json"
    New-Item -ItemType Directory -Force -Path $buildConfigRoot | Out-Null
    $releaseConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $releaseConfigPath -Encoding UTF8
}

if (-not $SkipBuild) {
    if (Test-Path -LiteralPath $bundleRoot) {
        $resolvedBundleRoot = [System.IO.Path]::GetFullPath($bundleRoot)
        $resolvedDesktopRoot = [System.IO.Path]::GetFullPath($desktopRoot)
        if (-not $resolvedBundleRoot.StartsWith($resolvedDesktopRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean bundle output outside desktop: $resolvedBundleRoot"
        }
        Remove-Item -LiteralPath $resolvedBundleRoot -Recurse -Force
    }
    Push-Location $desktopRoot
    try {
        Invoke-Checked "pnpm.cmd" @("install", "--frozen-lockfile")
        $tauriArgs = @(
            "run", "tauri:build",
            "--target", "x86_64-pc-windows-msvc"
        )
        if (-not [string]::IsNullOrWhiteSpace($releaseConfigPath)) {
            $tauriArgs += @("--config", $releaseConfigPath)
        }
        $tauriArgs += @("--", "--locked")
        Invoke-Checked "pnpm.cmd" $tauriArgs
    } finally {
        Pop-Location
    }
}

Require-Path $bundleRoot "NSIS bundle directory"
$builtInstallers = @(Get-ChildItem -LiteralPath $bundleRoot -Filter "*-setup.exe" -File)
if ($builtInstallers.Count -ne 1) {
    throw "Expected exactly one NSIS installer under $bundleRoot; found $($builtInstallers.Count)."
}

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
Copy-Item -LiteralPath $builtInstallers[0].FullName -Destination $installerPath -Force

Push-Location $desktopRoot
try {
    $signerArgs = @("tauri", "signer", "sign")
    if ([string]::IsNullOrEmpty($env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD)) {
        $signerArgs += "--password="
    }
    $signerArgs += $installerPath
    Invoke-Checked "pnpm.cmd" $signerArgs
} finally {
    Pop-Location
}
Require-Path $signaturePath "Tauri updater signature"

$authenticode = Get-AuthenticodeSignature -LiteralPath $installerPath
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($authenticode.Status -ne "Valid") {
        throw "NSIS installer Authenticode verification failed: $($authenticode.Status) $($authenticode.StatusMessage)"
    }
    Write-Host "Authenticode Valid: $($authenticode.SignerCertificate.Subject)"
} elseif ($authenticode.Status -ne "NotSigned") {
    Write-Warning "Unsigned installer returned Authenticode status $($authenticode.Status)."
}

Write-Host "Wrote $installerPath"
Write-Host "Wrote $signaturePath"
if ($authenticode.Status -ne "Valid") {
    Write-Warning "Created an unsigned NSIS installer. Windows SmartScreen may warn."
}
