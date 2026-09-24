# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [ValidatePattern('^(latest|[0-9]+\.[0-9]+\.[0-9]+)$')]
    [string]$Version = 'latest',
    [switch]$CheckOnly,
    [string]$ReportPath = ''
)

$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'source.json'
$source = Get-Content $sourcePath -Raw | ConvertFrom-Json
$npm = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
$metadataText = & $npm view "@ianlucas/cs2-lib@$Version" version gitHead --json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read cs2-lib package metadata' }
$metadata = $metadataText | ConvertFrom-Json
if ($metadata.version -notmatch '^\d+\.\d+\.\d+$' -or $metadata.gitHead -notmatch '^[a-f0-9]{40}$') {
    throw 'Package metadata must contain an exact stable version and source commit'
}
# Public GET only. A source revision is never accepted automatically as reviewed.
$head = Invoke-RestMethod 'https://api.github.com/repos/ed0ard/CS2-Bot-Randomizer/commits/main' -Headers @{
    'Accept' = 'application/vnd.github+json'
    'User-Agent' = 'DemoTracer-catalog-maintenance'
}
if ($head.sha -notmatch '^[a-f0-9]{40}$') { throw 'Invalid Randomizer upstream commit' }
$report = [ordered]@{
    package = $source.package
    previousVersion = $source.version
    candidateVersion = $metadata.version
    candidateCommit = $metadata.gitHead
    dataChanged = ($source.version -ne $metadata.version -or $source.commit -ne $metadata.gitHead)
    reviewedRandomizerCommit = $source.randomizerCommit
    currentRandomizerCommit = $head.sha
    upstreamChanged = ($source.randomizerCommit -ne $head.sha)
    upstreamUrl = $head.html_url
}
$reportJson = $report | ConvertTo-Json
Write-Output $reportJson
if ($ReportPath) {
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFile)) | Out-Null
    [IO.File]::WriteAllText($reportFile, $reportJson + "`n")
}
if ($CheckOnly) { return }
Push-Location $PSScriptRoot
try {
    & $npm install --save-exact --ignore-scripts "@ianlucas/cs2-lib@$($metadata.version)"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot install pinned cs2-lib candidate' }
    $source.version = $metadata.version
    $source.commit = $metadata.gitHead
    [IO.File]::WriteAllText($sourcePath, ($source | ConvertTo-Json) + "`n")
    & $npm run generate
    if ($LASTEXITCODE -ne 0) { throw 'Catalog generation failed; do not publish this candidate' }
    & $npm run check
    if ($LASTEXITCODE -ne 0) { throw 'Catalog reproducibility check failed' }
    & $npm test
    if ($LASTEXITCODE -ne 0) { throw 'Catalog validation failed' }
} finally {
    Pop-Location
}
