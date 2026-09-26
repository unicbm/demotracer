# Copyright (c) 2026 unicbm. Licensed under AGPL-3.0-only; see ../LICENSE.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
function Invoke-Cargo {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & cargo @Arguments
    if ($LASTEXITCODE -ne 0) { throw "cargo $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}

foreach ($dependency in @('.deps/demoparser/src/parser/Cargo.toml',
    '.deps/common/contracts/replay-source-fields.v1.json',
    '.deps/common/econ/cs2-lib-econ-index.v1.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $dependency) -PathType Leaf)) {
        throw "Missing $dependency. Initialize the repository's recursive Git submodules before checking."
    }
}
Push-Location $repoRoot
try {
    Invoke-Cargo fmt '--' --check
    Invoke-Cargo check --all-targets --release --locked
    Invoke-Cargo test --release --locked
    Invoke-Cargo test --release --locked --no-default-features
}
finally { Pop-Location }
