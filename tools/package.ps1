# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param(
    [string]$OutputDirectory = '',
    [string]$DotnetPath = 'dotnet',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $root 'BotRandomizer.csproj'
[xml]$projectXml = Get-Content $project -Raw
$apiProject = [IO.Path]::GetFullPath((Join-Path $root ([string]$projectXml.Project.ItemGroup.ProjectReference.Include)))
$apiRoot = Split-Path $apiProject
$version = [regex]::Match((Get-Content (Join-Path $root 'BotRandomizer.cs') -Raw), 'ModuleVersion\s*=>\s*"([^"]+)"').Groups[1].Value
$apiVersion = [int][regex]::Match((Get-Content (Join-Path $apiRoot 'IBotRandomizerApi.cs') -Raw), 'ApiVersion\s*=\s*(\d+)').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $apiVersion -le 0) { throw 'Invalid provider version/API' }
$hostContract = Join-Path $root 'host-contract.json'
if (Test-Path $hostContract) {
    $hooks = Get-Content $hostContract -Raw | ConvertFrom-Json
} else {
    $contract = Get-Content (Join-Path $root '../../../shared/contracts/playback-contract.v1.json') -Raw | ConvertFrom-Json
    $hooks = $contract.hook_runtime
    if ($contract.bot_randomizer.provider_version -ne $version -or $contract.bot_randomizer.api -ne $apiVersion) {
        throw 'Provider does not match playback contract'
    }
}
if ($hooks.backend -ne 'khook') { throw 'The unified provider requires shared KHook' }
if (-not $SkipBuild) {
    & $DotnetPath build $project -c Release '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-m:1' '-nodeReuse:false' "-p:PathMap=$root=/_/BotRandomizer"
    if ($LASTEXITCODE -ne 0) { throw 'Provider build failed' }
}
$output = Join-Path $root 'bin/Release/net10.0'
$builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output 'BotRandomizer.dll')).ProductVersion.Split('+')[0]
if ($builtVersion -ne $version) { throw 'Built provider version is stale; rebuild before packaging' }
$apiOutput = Join-Path $apiRoot 'bin/Release/net10.0/BotRandomizerApi.dll'
# A new staging directory avoids ever recursively deleting a caller's path.
$stage = Join-Path $OutputDirectory ('stage-' + [guid]::NewGuid().ToString('N'))
$plugin = 'addons/counterstrikesharp/plugins/BotRandomizer'
$api = 'addons/counterstrikesharp/shared/BotRandomizerApi/BotRandomizerApi.dll'
$files = [ordered]@{}
foreach ($name in @('BotRandomizer.dll', 'BotRandomizer.deps.json', 'cosmetic_catalog.json', 'charm_placements.json', 'cs2-lib-econ-index.v1.json')) {
    $files["$plugin/$name"] = Join-Path $output $name
}
foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md', 'API.md', 'UPSTREAM.md')) {
    $files["$plugin/$name"] = Join-Path $root $name
}
$files[$api] = $apiOutput
$entries = @()
foreach ($path in $files.Keys) {
    $from = $files[$path]
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing package input: $from" }
    $to = Join-Path $stage $path
    [IO.Directory]::CreateDirectory((Split-Path $to)) | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
    $entries += [ordered]@{ path = $path; size = (Get-Item $to).Length; sha256 = (Get-FileHash $to -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    schema_version = 1; product = 'BotRandomizer'; provider_version = $version; api = $apiVersion
    hook_runtime = $hooks; files = $entries
}
[IO.File]::WriteAllText((Join-Path $stage 'botrandomizer-package.v1.json'), ($manifest | ConvertTo-Json -Depth 12) + "`n")
$zip = Join-Path $OutputDirectory "BotRandomizer-v$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
[IO.File]::WriteAllText("$zip.sha256", (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + [IO.Path]::GetFileName($zip) + "`n")
Write-Host "Unified package: $zip"
Write-Output $zip
