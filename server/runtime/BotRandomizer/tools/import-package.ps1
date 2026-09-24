# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [Parameter(Mandatory)][string]$ExpectedContractPath
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($DestinationRoot)
if (Test-Path -LiteralPath $destination) { throw 'Package import requires a new staging directory' }
$contract = Get-Content -LiteralPath $ExpectedContractPath -Raw | ConvertFrom-Json
$plugin = 'addons/counterstrikesharp/plugins/BotRandomizer/'
$required = @('BotRandomizer.dll', 'BotRandomizer.deps.json', 'cosmetic_catalog.json', 'charm_placements.json', 'cs2-lib-econ-index.v1.json', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md', 'API.md', 'UPSTREAM.md') | ForEach-Object { $plugin + $_ }
$required += 'addons/counterstrikesharp/shared/BotRandomizerApi/BotRandomizerApi.dll'
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
try {
    $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $size = 0L
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName
        if ($path.EndsWith('/')) {
            if (-not @($required | Where-Object { $_.StartsWith($path, [StringComparison]::Ordinal) }).Count) { throw "Unexpected archive directory: $path" }
            continue
        }
        if (($path -cne 'botrandomizer-package.v1.json' -and $path -cnotin $required) -or $entries.ContainsKey($path)) {
            throw "Unexpected or duplicate package path: $path"
        }
        $size += $entry.Length
        if ($size -gt 64MB) { throw 'Randomizer package exceeds 64 MiB' }
        $entries.Add($path, $entry)
    }
    if ($entries.Count -ne $required.Count + 1 -or -not $entries.ContainsKey('botrandomizer-package.v1.json')) { throw 'Incomplete Randomizer package' }
    $reader = [IO.StreamReader]::new($entries['botrandomizer-package.v1.json'].Open())
    try { $receipt = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($receipt.schema_version -ne 1 -or $receipt.product -cne 'BotRandomizer' -or
        $receipt.provider_version -cne $contract.bot_randomizer.provider_version -or
        $receipt.api -ne $contract.bot_randomizer.api -or $receipt.hook_runtime.backend -cne 'khook') {
        throw 'Randomizer package version/API does not match the playback contract'
    }
    foreach ($field in $contract.hook_runtime.PSObject.Properties.Name) {
        if ($receipt.hook_runtime.$field -cne $contract.hook_runtime.$field) { throw "Randomizer hook runtime mismatch: $field" }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $validated = [Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::Ordinal)
    foreach ($file in $receipt.files) {
        $path = [string]$file.path
        if ($path -cnotin $required -or -not $seen.Add($path) -or -not $entries.ContainsKey($path)) { throw "Invalid manifest path: $path" }
        if ($entries[$path].Length -ne $file.size -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid manifest size/hash: $path" }
        $stream = $entries[$path].Open()
        $buffer = [IO.MemoryStream]::new()
        try { $stream.CopyTo($buffer); $bytes = $buffer.ToArray() } finally { $stream.Dispose(); $buffer.Dispose() }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($hash -cne $file.sha256) { throw "Randomizer package hash mismatch: $path" }
        $validated.Add($path, $bytes)
    }
    if ($seen.Count -ne $required.Count) { throw 'Incomplete Randomizer manifest' }
    # Nothing is written until every file and the compatibility contract passes.
    foreach ($path in $required) {
        $target = Join-Path $destination $path
        [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null
        [IO.File]::WriteAllBytes($target, $validated[$path])
    }
    [IO.File]::WriteAllText((Join-Path $destination 'botrandomizer-package.v1.json'), ($receipt | ConvertTo-Json -Depth 12) + "`n")
} finally { $zip.Dispose() }
Write-Output $destination
