# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$required = @(
    '.deps/common/csharp/DemoTracerApi/DemoTracerApi.csproj',
    '.deps/common/econ/cs2-lib-econ-index.v1.json',
    '.deps/hider/csharp/BotHiderImpl/BotHiderImpl.csproj',
    '.deps/randomizer/BotRandomizerApi/BotRandomizerApi.csproj',
    '.deps/controller/csharp/BotControllerImpl/BotControllerImpl.csproj'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Missing dependency $relative; run git submodule update --init --recursive"
    }
}
$common = Join-Path $root '.deps/common'
& $DotnetPath test (Join-Path $root 'tests/DemoTracer.Tests/DemoTracer.Tests.csproj') -c Release '--nologo' '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' '-m:1' '-nodeReuse:false' "-p:DtrCommonRoot=$common"
if ($LASTEXITCODE -ne 0) { throw 'DemoTracer managed build or regression tests failed' }
