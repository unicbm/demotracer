# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
param([string]$DotnetPath = 'dotnet', [string]$Generator = '')
$ErrorActionPreference = 'Stop'
$componentRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'check-native-toolchain.ps1')
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
Push-Location (Join-Path $PSScriptRoot 'cs2-lib-data')
try {
    $npm = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
    Invoke-Checked $npm @('ci', '--ignore-scripts')
    Invoke-Checked $npm @('run', 'check')
    Invoke-Checked $npm @('test')
} finally { Pop-Location }
Invoke-Checked $DotnetPath @('build', (Join-Path $componentRoot 'csharp/DemoTracerApi/DemoTracerApi.csproj'), '-c', 'Release')
$build = Join-Path $componentRoot '.build/native-tests'
$configure = @('-S', (Join-Path $componentRoot 'native/tests'), '-B', $build, '-DCMAKE_BUILD_TYPE=Release')
if ($Generator) { $configure += @('-G', $Generator) }
if ($IsWindows -and (-not $Generator -or $Generator -like 'Visual Studio*')) { $configure += @('-A', 'x64') }
Invoke-Checked cmake $configure
Invoke-Checked cmake @('--build', $build, '--config', 'Release')
Invoke-Checked ctest @('--test-dir', $build, '-C', 'Release', '--output-on-failure')
