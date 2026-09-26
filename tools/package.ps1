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
$project = Join-Path $root 'DemoTracer.csproj'
$version = [regex]::Match((Get-Content (Join-Path $root 'DemoTracerPlugin.cs') -Raw), 'ModuleVersion\s*=>\s*"([^"]+)"').Groups[1].Value
$companionApi = [int][regex]::Match((Get-Content (Join-Path $root 'BotControllerNativeTypes.cs') -Raw), 'DemoTracerApiVersion\s*=\s*(\d+)').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $companionApi -le 0) { throw 'Invalid plugin version/API' }
$hooks = Get-Content (Join-Path $root '.deps/common/contracts/hook-runtime.v1.json') -Raw | ConvertFrom-Json
if ($hooks.backend -cne 'khook') { throw 'The playback plugin requires the matched shared KHook host' }
if (-not $SkipBuild) {
    & $DotnetPath build $project -c Release '--nologo' '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-m:1' '-nodeReuse:false' "-p:PathMap=$root=/_/DemoTracer"
    if ($LASTEXITCODE -ne 0) { throw 'DemoTracer build failed' }
}
$output = Join-Path $root 'bin/Release/net10.0'
$apiOutput = Join-Path $root '.deps/common/csharp/DemoTracerApi/bin/Release/net10.0/DemoTracerApi.dll'
$stage = Join-Path $OutputDirectory ('stage-' + [guid]::NewGuid().ToString('N'))
$plugin = 'addons/counterstrikesharp/plugins/DemoTracer'
$files = [ordered]@{}
foreach ($name in @('DemoTracer.dll', 'DemoTracer.deps.json', 'ZstdSharp.dll', 'cs2-lib-econ-index.v1.json', 'demotracer-native.json')) {
    $files["$plugin/$name"] = Join-Path $output $name
}
foreach ($name in @('demotracer.config.example.json', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md', 'UPSTREAM.md')) {
    $files["$plugin/$name"] = Join-Path $root $name
}
$files['addons/counterstrikesharp/shared/DemoTracerApi/DemoTracerApi.dll'] = $apiOutput
$entries = @()
foreach ($path in $files.Keys) {
    $source = $files[$path]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $source" }
    $destination = Join-Path $stage $path
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    $entries += [ordered]@{ path = $path; size = (Get-Item $destination).Length; sha256 = (Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$receipt = [ordered]@{
    schema_version = 1; component = 'cs2-css-demotrace'; plugin_version = $version
    companion_api = $companionApi; hook_runtime = $hooks; files = $entries
}
[IO.File]::WriteAllText((Join-Path $stage 'demotracer-css-package.v1.json'), ($receipt | ConvertTo-Json -Depth 12) + "`n")
$zip = Join-Path $OutputDirectory "DemoTracer-CSS-v$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
[IO.File]::WriteAllText("$zip.sha256", (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + [IO.Path]::GetFileName($zip) + "`n")
Write-Output $zip
