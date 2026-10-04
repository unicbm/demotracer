param(
    [string]$Dotnet = 'dotnet',
    [string]$CMake = 'cmake',
    [string]$CTest = 'ctest',
    [string]$CommonDirectory,
    [string]$Generator,
    [switch]$NativeBuild,
    [switch]$SkipManagedBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $CommonDirectory) { $CommonDirectory = Join-Path $repo '../common' }
$common = (Resolve-Path -LiteralPath $CommonDirectory).Path

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}

$configure = @('-DCMAKE_BUILD_TYPE=Release', "-DDTR_COMMON_DIR=$common")
if ($Generator) { $configure += @('-G', $Generator) }
if ($IsWindows -and (-not $Generator -or $Generator -like 'Visual Studio*')) { $configure += @('-A', 'x64') }
$tests = Join-Path $repo '.build/tests'
Invoke-Checked $CMake (@('-S', (Join-Path $repo 'tests'), '-B', $tests) + $configure)
Invoke-Checked $CMake @('--build', $tests, '--config', 'Release')
Invoke-Checked $CTest @('--test-dir', $tests, '-C', 'Release', '--output-on-failure')
if (-not $SkipManagedBuild) {
    Invoke-Checked $Dotnet @('build', (Join-Path $repo 'csharp/DtrController/DtrController.csproj'), '-c', 'Release', "-p:DtrCommonRoot=$common")
}

if ($NativeBuild) {
    $native = Join-Path $repo '.build/native'
    Invoke-Checked $CMake (@('-S', $repo, '-B', $native) + $configure)
    Invoke-Checked $CMake @('--build', $native, '--config', 'Release', '--target', 'dot-controller')
}
