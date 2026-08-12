# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

$firstPartyRoots = @(
    "cloudflare\telemetry",
    "desktop\converter\src",
    "desktop\gui",
    "server\plugins\DemoTracer",
    "server\plugins\DemoTracer.Tests",
    "server\plugins\DemoTracerApi",
    "tooling\cs2-lib-data",
    "tooling\scripts"
)
$sourceExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(
    ".cs", ".css", ".html", ".js", ".mjs", ".ps1", ".psm1", ".rs", ".ts", ".tsx"
)) {
    $null = $sourceExtensions.Add($extension)
}
$excludedFiles = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$null = $excludedFiles.Add("desktop\gui\src\vite-env.d.ts")

$copyrightMarker = "Copyright (c) 2026 unicbm. All rights reserved."
$licenseMarker = "Licensed under the GNU Affero General Public License v3.0 only."
$missingHeaders = [System.Collections.Generic.List[string]]::new()
$checkedCount = 0

foreach ($relativeRoot in $firstPartyRoots) {
    $absoluteRoot = Join-Path $RepoRoot $relativeRoot
    if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
        throw "first-party source root not found: $absoluteRoot"
    }
}

$repositoryFiles = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw "could not enumerate repository files for first-party header validation"
}
$rootPrefixes = @($firstPartyRoots | ForEach-Object { "$_\" })

foreach ($repositoryPath in $repositoryFiles) {
    $relativePath = $repositoryPath.Replace('/', '\')
    if (-not ($rootPrefixes | Where-Object {
        $relativePath.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase)
    })) {
        continue
    }
    if (-not $sourceExtensions.Contains([System.IO.Path]::GetExtension($relativePath)) -or
        $excludedFiles.Contains($relativePath)) {
        continue
    }

    $absolutePath = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        continue
    }

    $checkedCount++
    $source = [System.IO.File]::ReadAllText($absolutePath)
    $prefixLength = [Math]::Min(640, $source.Length)
    $prefix = $source.Substring(0, $prefixLength)
    if (-not $prefix.Contains($copyrightMarker, [System.StringComparison]::Ordinal) -or
        -not $prefix.Contains($licenseMarker, [System.StringComparison]::Ordinal)) {
        $missingHeaders.Add($relativePath)
    }
}

if ($missingHeaders.Count -gt 0) {
    throw "first-party source header check failed:`n - $($missingHeaders -join "`n - ")"
}

Write-Host "First-party source headers verified: files=$checkedCount"
