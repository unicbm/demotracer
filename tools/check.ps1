# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$catalog = Join-Path $root '.deps/common/econ/randomizer-catalog.json'
$econ = Join-Path $root '.deps/common/econ/cs2-lib-econ-index.v1.json'
foreach ($path in @($catalog, $econ, (Join-Path $root '.deps/common/contracts/hook-runtime.v1.json'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Missing common dependency; run git submodule update --init --recursive'
    }
}
& $DotnetPath build (Join-Path $root 'BotRandomizer.csproj') -c Release '--nologo' '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' '-m:1' '-nodeReuse:false'
if ($LASTEXITCODE -ne 0) { throw 'Provider build failed' }
$catalog = Join-Path $root 'bin/Release/net10.0/cosmetic_catalog.json'
$econ = Join-Path $root 'bin/Release/net10.0/cs2-lib-econ-index.v1.json'
& $DotnetPath run --project (Join-Path $root 'tests/BotRandomizer.SelfTest/BotRandomizer.SelfTest.csproj') -c Release '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' -- $catalog $econ
if ($LASTEXITCODE -ne 0) { throw 'Provider catalog, ownership, and coexistence tests failed' }
