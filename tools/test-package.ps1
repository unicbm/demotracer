# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param([string]$Package = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = [regex]::Match((Get-Content (Join-Path $root 'BotRandomizer.cs') -Raw), 'ModuleVersion\s*=>\s*"([^"]+)"').Groups[1].Value
if (-not $Package) { $Package = Join-Path $root "dist/BotRandomizer-v$version.zip" }
$work = Join-Path $root ('dist/package-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
if (Test-Path (Join-Path $root 'host-contract.json')) {
    $hooks = Get-Content (Join-Path $root 'host-contract.json') -Raw | ConvertFrom-Json
    [xml]$xml = Get-Content (Join-Path $root 'BotRandomizer.csproj') -Raw
    $apiRoot = Split-Path ([IO.Path]::GetFullPath((Join-Path $root ([string]$xml.Project.ItemGroup.ProjectReference.Include))))
    $api = [int][regex]::Match((Get-Content (Join-Path $apiRoot 'IBotRandomizerApi.cs') -Raw), 'ApiVersion\s*=\s*(\d+)').Groups[1].Value
    $contract = @{bot_randomizer = @{provider_version = $version; api = $api}; hook_runtime = $hooks}
} else {
    $contract = Get-Content (Join-Path $root '../../../shared/contracts/playback-contract.v1.json') -Raw | ConvertFrom-Json
}
$expected = Join-Path $work 'contract.json'
[IO.File]::WriteAllText($expected, ($contract | ConvertTo-Json -Depth 12))
$valid = Join-Path $work 'valid'
& (Join-Path $PSScriptRoot 'import-package.ps1') -Package $Package -DestinationRoot $valid -ExpectedContractPath $expected | Out-Null
$dlls = @(Get-ChildItem (Join-Path $valid 'addons') -Recurse -Filter '*.dll')
if ($dlls.Count -ne 2 -or @($dlls | Where-Object Name -eq 'BotRandomizer.dll').Count -ne 1 -or
    @($dlls | Where-Object Name -eq 'BotRandomizerApi.dll').Count -ne 1) { throw 'Package must contain exactly one provider and one shared API' }
foreach ($case in @('hash', 'version', 'second-provider', 'private-api', 'traversal')) {
    $bad = Join-Path $work "$case.zip"
    Copy-Item -LiteralPath $Package -Destination $bad
    $archive = [IO.Compression.ZipFile]::Open($bad, [IO.Compression.ZipArchiveMode]::Update)
    try {
        if ($case -eq 'hash') {
            $path = 'addons/counterstrikesharp/plugins/BotRandomizer/cosmetic_catalog.json'
            $inputStream = $archive.GetEntry($path).Open()
            $buffer = [IO.MemoryStream]::new()
            try { $inputStream.CopyTo($buffer); $bytes = $buffer.ToArray() } finally { $inputStream.Dispose(); $buffer.Dispose() }
            $bytes[0] = $bytes[0] -bxor 1
            $archive.GetEntry($path).Delete()
            $outputStream = $archive.CreateEntry($path).Open()
            try { $outputStream.Write($bytes) } finally { $outputStream.Dispose() }
        } elseif ($case -eq 'version') {
            $path = 'botrandomizer-package.v1.json'
            $reader = [IO.StreamReader]::new($archive.GetEntry($path).Open())
            try { $receipt = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            $archive.GetEntry($path).Delete()
            $receipt.api = 999
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($path).Open())
            try { $writer.Write(($receipt | ConvertTo-Json -Depth 12)) } finally { $writer.Dispose() }
        } else {
            $path = switch ($case) {
                'second-provider' { 'addons/counterstrikesharp/plugins/OtherRandomizer/BotRandomizer.dll' }
                'private-api' { 'addons/counterstrikesharp/plugins/BotRandomizer/BotRandomizerApi.dll' }
                'traversal' { '../escape.txt' }
            }
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($path).Open())
            try { $writer.Write('invalid') } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    $destination = Join-Path $work "reject-$case"
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'import-package.ps1') -Package $bad -DestinationRoot $destination -ExpectedContractPath $expected | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or (Test-Path $destination)) { throw "Invalid package was not rejected before writes: $case" }
}
Write-Host 'Unified package tests passed (one provider, shared API, hashes, contract, duplicate writers and path isolation).'
