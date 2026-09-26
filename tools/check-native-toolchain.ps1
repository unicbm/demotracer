# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hooks = Get-Content (Join-Path $root 'contracts/hook-runtime.v1.json') -Raw | ConvertFrom-Json
$sdk = Get-Content (Join-Path $root 'contracts/native-toolchain.v1.json') -Raw | ConvertFrom-Json
foreach ($field in @('metamod_source_commit', 'khook_source_commit', 'counterstrikesharp_source_commit')) {
    if ([string]$hooks.$field -notmatch '^[0-9a-f]{40}$') { throw "Invalid hook source pin: $field" }
}
if ($hooks.backend -ne 'khook' -or $hooks.metamod_plugin_api -ne 18 -or $hooks.metamod_minimum_build -lt 1469) {
    throw 'Unsupported shared hook runtime contract'
}
if ($sdk.hl2sdk_repository -ne 'alliedmodders/hl2sdk' -or
    [string]$sdk.hl2sdk_commit -notmatch '^[0-9a-f]{40}$' -or
    $sdk.protoc -ne 'devtools/bin/protoc.exe' -or
    [string]$sdk.protoc_version -notmatch '^3\.21\.\d+$') {
    throw 'Invalid native toolchain source pin or protoc contract'
}
Write-Output 'Verified native toolchain source pins.'
