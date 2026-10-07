# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'check-playback-contract.ps1')
    $registry = Get-Content components.json -Raw | ConvertFrom-Json
    $productCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read product commit.' }
    $plugin = Get-Content server/plugins/DemoTracer/src/DemoTracer/Lifecycle/DemoTracerPlugin.cs -Raw
    $version = [regex]::Match($plugin, 'ModuleVersion\s*=>\s*"([^"]+)"').Groups[1].Value
    if (-not $version) { throw 'Playback source version is missing.' }
    $components = @($registry.components | Where-Object { $_.path.StartsWith('server/') } | ForEach-Object {
        $componentPath = Join-Path $repoRoot $_.path
        $commit = if ($_.source -eq 'submodule') { (& git -C $componentPath rev-parse HEAD).Trim() } else { $productCommit }
        if ($LASTEXITCODE -ne 0) { throw "Cannot read component commit: $($_.id)" }
        [ordered]@{
            id = $_.id
            repository = "https://github.com/$($_.repository)"
            commit = $commit
            source_version = $version
        }
    })
    $manifest = [ordered]@{ schema_version = 1; product_commit = $productCommit; components = $components }
    $destination = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
} finally { Pop-Location }
