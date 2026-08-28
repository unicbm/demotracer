# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Configuration = "Release",
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot "server\plugins\DemoTracer.Tests\DemoTracer.Tests.csproj"
$botRandomizerSelfTest = Join-Path $repoRoot "server\runtime\BotRandomizer\tests\BotRandomizer.SelfTest\BotRandomizer.SelfTest.csproj"
$botRandomizerCatalog = Join-Path $repoRoot "server\runtime\BotRandomizer\cosmetic_catalog.json"
$replayEconIndex = Join-Path $repoRoot "shared\econ\cs2-lib-econ-index.v1.json"
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
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

$dotnet = Resolve-DotnetPath $DotnetPath
Write-Host "Using $dotnet"
Invoke-Dotnet $dotnet @("restore", $projectPath, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("build", $projectPath, "-c", $Configuration, "--no-restore", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("test", $projectPath, "-c", $Configuration, "--no-build", "--no-restore", "-m:1", "-nodeReuse:false")
Invoke-Dotnet $dotnet @("restore", $botRandomizerSelfTest, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false")
Invoke-Dotnet $dotnet @("run", "--project", $botRandomizerSelfTest, "-c", $Configuration, "--no-restore", "--", $botRandomizerCatalog, $replayEconIndex)
& (Join-Path $PSScriptRoot "check-demotracer-source-governance.ps1") -RepoRoot $repoRoot
