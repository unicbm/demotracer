# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repoRoot
try {
    & node server/runtime/common/tools/check-components.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record mismatched component sources.' }
    $registry = Get-Content components.json -Raw | ConvertFrom-Json
    $productCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read product commit.' }
    $components = @($registry.components | ForEach-Object {
        $componentPath = Join-Path $repoRoot $_.path
        $commit = (& git -C $componentPath rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cannot read component commit: $($_.id)" }
        [ordered]@{
            id = $_.id
            repository = "https://github.com/$($_.repository)"
            commit = $commit
            source_version = (Get-Content (Join-Path $componentPath 'version.txt') -Raw).Trim()
        }
    })
    $manifest = [ordered]@{ schema_version = 1; product_commit = $productCommit; components = $components }
    $destination = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
} finally { Pop-Location }
