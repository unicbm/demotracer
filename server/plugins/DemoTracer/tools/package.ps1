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
$project = Join-Path $root 'src/DemoTracer/DemoTracer.csproj'
$version = [regex]::Match((Get-Content (Join-Path $root 'src/DemoTracer/Lifecycle/DemoTracerPlugin.cs') -Raw), 'ModuleVersion\s*=>\s*"([^"]+)"').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid plugin version' }
$hooks = Get-Content (Join-Path $root '../../runtime/common/contracts/hook-runtime.v1.json') -Raw | ConvertFrom-Json
if ($hooks.backend -cne 'khook') { throw 'The playback plugin requires the matched shared KHook host' }
if (-not $SkipBuild) {
    & $DotnetPath build $project -c Release '--nologo' '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-m:1' '-nodeReuse:false' "-p:PathMap=$root=/_/DemoTracer"
    if ($LASTEXITCODE -ne 0) { throw 'DemoTracer build failed' }
}
$output = Join-Path $root 'src/DemoTracer/bin/Release/net10.0'
$apiOutput = Join-Path $root '../../runtime/common/csharp/DemoTracerApi/bin/Release/net10.0/DemoTracerApi.dll'
$stage = Join-Path $OutputDirectory ('stage-' + [guid]::NewGuid().ToString('N'))
$plugin = 'addons/counterstrikesharp/plugins/DemoTracer'
$files = [ordered]@{}
foreach ($name in @('DemoTracer.dll', 'DemoTracer.deps.json', 'ZstdSharp.dll', 'cs2-lib-econ-index.v1.json', 'demotracer-native.json')) {
    $files["$plugin/$name"] = Join-Path $output $name
}
$files['addons/counterstrikesharp/shared/DemoTracerApi/DemoTracerApi.dll'] = $apiOutput
foreach ($path in $files.Keys) {
    $source = $files[$path]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $source" }
    $destination = Join-Path $stage $path
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
$zip = Join-Path $OutputDirectory "DemoTracer-CSS-v$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
[IO.File]::WriteAllText("$zip.sha256", (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + [IO.Path]::GetFileName($zip) + "`n")
Write-Output $zip
