# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Configuration = "Release",
    [string]$DotnetPath = "",
    [string]$BotHiderRoot = "third_party/BotHider",
    [switch]$SkipRandomizer
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$BotHiderRoot = if ([System.IO.Path]::IsPathRooted($BotHiderRoot)) {
    [System.IO.Path]::GetFullPath($BotHiderRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $BotHiderRoot))
}
if (-not (Test-Path -LiteralPath (Join-Path $BotHiderRoot "csharp/BotHiderApi/BotHiderApi.csproj") -PathType Leaf)) {
    throw "BotHider API checkout not found at $BotHiderRoot; pass -BotHiderRoot pointing to XBribo/CS2-Bot-Hider."
}
$projectPath = Join-Path $repoRoot "server\plugins\DemoTracer\tests\DemoTracer.Tests\DemoTracer.Tests.csproj"
$botRandomizerSelfTest = Join-Path $repoRoot "server\runtime\BotRandomizer\tests\BotRandomizer.SelfTest\BotRandomizer.SelfTest.csproj"
$botRandomizerProvider = Join-Path $repoRoot "server\runtime\BotRandomizer\BotRandomizer.csproj"
$botRandomizerCatalog = Join-Path $repoRoot "server\runtime\BotRandomizer\bin\$Configuration\net10.0\cosmetic_catalog.json"
$replayEconIndex = Join-Path $repoRoot "server\runtime\BotRandomizer\bin\$Configuration\net10.0\cs2-lib-econ-index.v1.json"
$componentArguments = @(
    "-p:PathMap=$repoRoot=/_/demotracer",
    "-p:DtrCommonRoot=$(Join-Path $repoRoot 'server/runtime/common')",
    "-p:BotHiderRoot=$BotHiderRoot",
    "-p:DtrRandomizerRoot=$(Join-Path $repoRoot 'server/runtime/BotRandomizer')",
    "-p:DtrControllerRoot=$(Join-Path $repoRoot 'server/runtime/BotController')"
)
$nugetConfigPath = Join-Path $repoRoot "NuGet.Config"

function Test-DotnetHasSdk([string]$Command) {
    try {
        $sdks = & $Command --list-sdks 2>$null
        return $LASTEXITCODE -eq 0 -and $null -ne ($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)
    } catch {
        return $false
    }
}

function Resolve-DotnetPath([string]$PreferredPath) {
    if ($PreferredPath) {
        if (Test-DotnetHasSdk $PreferredPath) { return $PreferredPath }
        throw ".NET 10 SDK not found at preferred path: $PreferredPath"
    }

    $candidates = @(
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        "C:\Program Files\dotnet\dotnet.exe",
        "C:\Program Files (x86)\dotnet\dotnet.exe"
    )
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-DotnetHasSdk $candidate) { return $candidate }
    }

    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command -and (Test-DotnetHasSdk $command.Source)) { return $command.Source }
    throw ".NET 10 SDK not found. Install it or pass -DotnetPath."
}

function Invoke-Dotnet([string]$Command, [string[]]$Arguments) {
    $verb = $Arguments[0]
    $remainingArguments = @($Arguments | Select-Object -Skip 1)
    & $Command $verb @componentArguments @remainingArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

$dotnet = Resolve-DotnetPath $DotnetPath
Write-Host "Using $dotnet"
Invoke-Dotnet $dotnet @("restore", $projectPath, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("build", $projectPath, "-c", $Configuration, "--no-restore", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("test", $projectPath, "-c", $Configuration, "--no-build", "--no-restore", "-m:1", "-nodeReuse:false")
if (-not $SkipRandomizer) {
Invoke-Dotnet $dotnet @("restore", $botRandomizerProvider, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("build", $botRandomizerProvider, "-c", $Configuration, "--no-restore", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false")
foreach ($asset in @("BotRandomizer.dll", "cosmetic_catalog.json", "charm_placements.json", "cs2-lib-econ-index.v1.json")) {
    $assetPath = Join-Path (Split-Path $botRandomizerProvider) "bin\$Configuration\net10.0\$asset"
    if (-not (Test-Path -LiteralPath $assetPath)) { throw "Missing Randomizer build asset: $asset" }
}
Invoke-Dotnet $dotnet @("restore", $botRandomizerSelfTest, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("run", "--project", $botRandomizerSelfTest, "-c", $Configuration, "--no-restore", "--", $botRandomizerCatalog, $replayEconIndex)
}
& (Join-Path $PSScriptRoot "check-demotracer-source-governance.ps1") -RepoRoot $repoRoot
