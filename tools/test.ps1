# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dataRoot = Join-Path $PSScriptRoot 'data'
if (-not (Test-Path $dataRoot)) { $dataRoot = Join-Path $root '../../../tooling/cs2-lib-data' }
$npm = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
Push-Location $dataRoot
try {
    & $npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Data dependency restore failed' }
    & $npm run check
    if ($LASTEXITCODE -ne 0) { throw 'Generated data is stale' }
    & $npm test
    if ($LASTEXITCODE -ne 0) { throw 'Data tests failed' }
} finally { Pop-Location }
& $DotnetPath build (Join-Path $root 'BotRandomizer.csproj') -c Release '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' '-m:1' '-nodeReuse:false'
if ($LASTEXITCODE -ne 0) { throw 'Provider build failed' }
$econ = Join-Path $root 'bin/Release/net10.0/cs2-lib-econ-index.v1.json'
& $DotnetPath run --project (Join-Path $root 'tests/BotRandomizer.SelfTest/BotRandomizer.SelfTest.csproj') -c Release '-p:NuGetAudit=false' -- (Join-Path $root 'cosmetic_catalog.json') $econ
if ($LASTEXITCODE -ne 0) { throw 'Provider coexistence tests failed' }
