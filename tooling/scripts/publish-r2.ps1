# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Version = "1.0.0",
    [string]$PlaybackVersion = "",
    [string]$Bucket = "cs2-demotracer-releases",
    [string]$ReleaseBaseUrl = "https://releases.detr.site",
    [string]$WranglerVersion = "4.118.0",
    [string]$UpdaterRoot = "",
    [switch]$AllowUnsignedInstaller,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PlaybackVersion)) {
    $PlaybackVersion = $Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $PlaybackVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version and PlaybackVersion must be semantic versions such as 1.1.6."
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($UpdaterRoot)) {
    $UpdaterRoot = Join-Path $repoRoot "dist\updater-v$Version"
} elseif (-not [System.IO.Path]::IsPathRooted($UpdaterRoot)) {
    $UpdaterRoot = Join-Path $repoRoot $UpdaterRoot
}
$UpdaterRoot = [System.IO.Path]::GetFullPath($UpdaterRoot)
$releaseBase = $ReleaseBaseUrl.TrimEnd('/')
$releaseUri = $null
if (-not [System.Uri]::TryCreate($releaseBase, [System.UriKind]::Absolute, [ref]$releaseUri) -or
    $releaseUri.Scheme -ne "https" -or
    [string]::IsNullOrWhiteSpace($releaseUri.Host)) {
    throw "ReleaseBaseUrl must be an absolute HTTPS URL."
}
$installerName = "demotracer-gui-v$Version.exe"
$signatureName = "$installerName.sig"
$playbackName = "demotracer-css-v$PlaybackVersion.zip"
$playbackSignatureName = "$playbackName.sig"
$required = @($installerName, $signatureName, $playbackName, $playbackSignatureName, "latest.json", "SHA256SUMS.txt")

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    Write-Host "> $Command $($Arguments -join ' ')"
    if ($DryRun) {
        return
    }
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

function Content-TypeFor([string]$Name) {
    if ($Name.EndsWith(".json", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "application/json; charset=utf-8"
    }
    if ($Name.EndsWith(".txt", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Name.EndsWith(".sig", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "text/plain; charset=utf-8"
    }
    if ($Name.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "application/zip"
    }
    return "application/vnd.microsoft.portable-executable"
}

if (-not (Test-Path -LiteralPath $UpdaterRoot -PathType Container)) {
    throw "Updater directory not found: $UpdaterRoot"
}
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $UpdaterRoot $name) -PathType Leaf)) {
        throw "Required updater asset is missing: $name"
    }
}

$installerPath = Join-Path $UpdaterRoot $installerName
$authenticode = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($authenticode.Status -ne "Valid") {
    if (-not $DryRun -and -not $AllowUnsignedInstaller) {
        throw "Refusing to publish an installer without a valid Authenticode signature: $($authenticode.Status)"
    }
    Write-Warning "Publishing an explicitly allowed unsigned installer: $($authenticode.Status). Windows SmartScreen may warn users."
}

$expectedSums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $UpdaterRoot "SHA256SUMS.txt") -Encoding ASCII) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/]+)$') {
        throw "Invalid SHA256SUMS.txt line: $line"
    }
    $expectedSums[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
foreach ($name in $required | Where-Object { $_ -ne "SHA256SUMS.txt" }) {
    if (-not $expectedSums.ContainsKey($name)) {
        throw "SHA256SUMS.txt omits updater asset: $name"
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $UpdaterRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedSums[$name]) {
        throw "SHA-256 mismatch for updater asset: $name"
    }
}

$latest = Get-Content -LiteralPath (Join-Path $UpdaterRoot "latest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ($latest.version -ne $Version) {
    throw "Updater manifest version does not match v$Version."
}
$expectedInstallerUrl = "$releaseBase/releases/v$Version/$installerName"
if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals(
        [string]$latest.platforms.'windows-x86_64'.url,
        $expectedInstallerUrl)) {
    throw "Updater manifest does not point at the immutable v$Version R2 prefix."
}
$expectedSignature = (Get-Content -LiteralPath (Join-Path $UpdaterRoot $signatureName) -Raw -Encoding UTF8).Trim()
if (-not [System.StringComparer]::Ordinal.Equals(
        [string]$latest.platforms.'windows-x86_64'.signature,
        $expectedSignature)) {
    throw "Updater manifest signature does not match $signatureName."
}
$expectedPlaybackUrl = "$releaseBase/releases/v$PlaybackVersion/$playbackName"
if ([string]$latest.playback.version -ne $PlaybackVersion -or
    -not [System.StringComparer]::OrdinalIgnoreCase.Equals([string]$latest.playback.url, $expectedPlaybackUrl)) {
    throw "Updater manifest playback asset does not point at the immutable v$PlaybackVersion R2 prefix."
}
$expectedPlaybackSignature = (Get-Content -LiteralPath (Join-Path $UpdaterRoot $playbackSignatureName) -Raw -Encoding UTF8).Trim()
if (-not [System.StringComparer]::Ordinal.Equals([string]$latest.playback.signature, $expectedPlaybackSignature)) {
    throw "Updater manifest playback signature does not match $playbackSignatureName."
}
$expectedPlaybackHash = (Get-FileHash -LiteralPath (Join-Path $UpdaterRoot $playbackName) -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals([string]$latest.playback.sha256, $expectedPlaybackHash)) {
    throw "Updater manifest playback SHA-256 does not match $playbackName."
}

$wrangler = "wrangler@$WranglerVersion"
Invoke-Checked "npx.cmd" @("--yes", $wrangler, "whoami", "--json")
Invoke-Checked "npx.cmd" @("--yes", $wrangler, "r2", "bucket", "info", $Bucket, "--json")

$versionedAssets = @($installerName, $signatureName)
if ($PlaybackVersion -eq $Version) {
    $versionedAssets += @($playbackName, $playbackSignatureName, "latest.json", "SHA256SUMS.txt")
} elseif (-not $DryRun) {
    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $remotePlaybackHead = Invoke-WebRequest -Uri "$expectedPlaybackUrl`?verify=$cacheBuster" -Method Head
    $localPlaybackLength = (Get-Item -LiteralPath (Join-Path $UpdaterRoot $playbackName)).Length
    if ($remotePlaybackHead.StatusCode -ne 200 -or
        [long]$remotePlaybackHead.Headers.'Content-Length' -ne $localPlaybackLength) {
        throw "Previously published Playback v$PlaybackVersion asset is missing or has the wrong length."
    }
    $remotePlaybackSignature = (Invoke-RestMethod -Uri "$expectedPlaybackUrl.sig?verify=$cacheBuster").Trim()
    if (-not [System.StringComparer]::Ordinal.Equals($remotePlaybackSignature, $expectedPlaybackSignature)) {
        throw "Previously published Playback v$PlaybackVersion signature does not match the local immutable asset."
    }
}

foreach ($name in $versionedAssets) {
    $path = Join-Path $UpdaterRoot $name
    $arguments = @(
        "--yes", $wrangler, "r2", "object", "put",
        "$Bucket/releases/v$Version/$name",
        "--file=$path",
        "--content-type=$(Content-TypeFor $name)",
        "--cache-control=public, max-age=31536000, immutable",
        "--remote",
        "--force"
    )
    if ($name.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
        $arguments += "--content-disposition=attachment; filename=`"$name`""
    }
    Invoke-Checked "npx.cmd" $arguments
}

$aliases = [ordered]@{
    "demotracer-gui.exe" = $installerName
    "demotracer-gui.exe.sig" = $signatureName
    "demotracer-css.zip" = $playbackName
    "demotracer-css.zip.sig" = $playbackSignatureName
}
foreach ($alias in $aliases.GetEnumerator()) {
    $path = Join-Path $UpdaterRoot $alias.Value
    $arguments = @(
        "--yes", $wrangler, "r2", "object", "put",
        "$Bucket/downloads/$($alias.Key)",
        "--file=$path",
        "--content-type=$(Content-TypeFor $alias.Value)",
        "--cache-control=public, max-age=300, must-revalidate",
        "--remote",
        "--force"
    )
    if ($alias.Key.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
        $arguments += "--content-disposition=attachment; filename=`"$($alias.Key)`""
    }
    Invoke-Checked "npx.cmd" $arguments
}

Invoke-Checked "npx.cmd" @(
    "--yes", $wrangler, "r2", "object", "put",
    "$Bucket/channels/stable/latest.json",
    "--file=$(Join-Path $UpdaterRoot 'latest.json')",
    "--content-type=application/json; charset=utf-8",
    "--cache-control=public, max-age=300, must-revalidate",
    "--remote",
    "--force"
)

if (-not $DryRun) {
    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $remoteLatest = Invoke-RestMethod -Uri "$releaseBase/channels/stable/latest.json?verify=$cacheBuster"
    if ($remoteLatest.version -ne $Version) {
        throw "R2 verification returned a stale updater manifest."
    }
    if ($remoteLatest.playback.version -ne $PlaybackVersion -or
        $remoteLatest.playback.url -ne "$releaseBase/releases/v$PlaybackVersion/$playbackName") {
        throw "R2 verification returned stale playback updater metadata."
    }
    Write-Host "Published and verified DemoTracer GUI v$Version with Playback v$PlaybackVersion at $releaseBase"
}
