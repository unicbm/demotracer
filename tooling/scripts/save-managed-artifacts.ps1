# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
param([string]$Destination = 'tmp/ci-managed')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach ($relative in @(
    'server/plugins/DemoTracer/src/DemoTracer/bin/Release/net10.0',
    'server/runtime/common/csharp/DemoTracerApi/bin/Release/net10.0',
    'server/runtime/BotController/csharp/BotControllerImpl/bin/Release',
    'server/runtime/BotController/csharp/BotControllerApi/bin/Release'
)) {
    $source = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing tested build output: $relative" }
    $target = Join-Path (Join-Path $root $Destination) $relative
    [IO.Directory]::CreateDirectory($target) | Out-Null
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $target -Recurse
}
