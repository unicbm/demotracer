#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Windows,
    [switch]$Linux,
    [switch]$CSharp,
    [switch]$Clean,
    [string]$Config = "Release",
    [string]$WslDistro = "Ubuntu-24.04",
    [string]$Generator = "Visual Studio 18 2026"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$DistWin = Join-Path $Root "dist/windows"
$DistLin = Join-Path $Root "dist/linux"

if (-not ($Windows -or $Linux -or $CSharp)) {
    $Windows = $true; $CSharp = $true
}

function Write-Step([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host "  $msg" -ForegroundColor Green }

function ConvertTo-WslPath([string]$p) {
    $full = (Resolve-Path -LiteralPath $p).Path
    $drive = $full.Substring(0, 1).ToLower()
    $rest = $full.Substring(2) -replace '\\', '/'
    return "/mnt/$drive$rest"
}

# --- Clean ---------------------------------------------------------------
if ($Clean) {
    Write-Step "Clean"
    foreach ($d in @("build", "dist")) {
        $path = Join-Path $Root $d
        if (Test-Path $path) { Remove-Item -Recurse -Force $path; Write-Ok "removed $d/" }
    }
    if ($Linux) {
        wsl.exe -d $WslDistro -e bash -lc "rm -rf ~/bh-build" | Out-Null
        Write-Ok "removed ~/bh-build (WSL)"
    }
}

# --- Windows ------------------------------------------
function Build-Windows {
    Write-Step "Windows"
    $build = Join-Path $Root "build"
    cmake -B $build -G $Generator -A x64 -S $Root | Out-Host
    if ($LASTEXITCODE) { throw "cmake configure (windows) failed" }
    cmake --build $build --config $Config | Out-Host
    if ($LASTEXITCODE) { throw "cmake build (windows) failed" }
    $pkg = Join-Path $build "package"
    if (-not (Test-Path "$pkg/addons/BotHider/bin/win64/BotHider.dll")) {
        throw "windows build produced no BotHider.dll"
    }
    Write-Ok "BotHider.dll built"
    return $pkg
}

# --- Linux ------------------------------------------
function Build-Linux {
    Write-Step "Linux (WSL: $WslDistro)"
    $srcWsl = ConvertTo-WslPath $Root
    $hl2Wsl = ConvertTo-WslPath $env:HL2SDKCS2
    $mmsWsl = ConvertTo-WslPath $env:MMSOURCE_DEV
    $protoWsl = ConvertTo-WslPath $env:CSGO_PROTO
    $bash = @"
set -e
export HL2SDKCS2='$hl2Wsl'
export MMSOURCE_DEV='$mmsWsl'
export CSGO_PROTO='$protoWsl'
cmake -S '$srcWsl' -B ~/bh-build -DCMAKE_BUILD_TYPE=$Config
cmake --build ~/bh-build -j`$(nproc)
test -f ~/bh-build/package/addons/BotHider/bin/linuxsteamrt64/BotHider.so
echo "BUILD_OK"
"@
    $bash = $bash -replace "`r`n", "`n"
    wsl.exe -d $WslDistro -e bash -lc $bash | Out-Host
    if ($LASTEXITCODE) { throw "WSL linux build failed" }
    # Copy
    $stage = Join-Path $Root "build/linux-package"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force $stage | Out-Null
    $stageWsl = ConvertTo-WslPath $stage
    wsl.exe -d $WslDistro -e bash -lc "cp -r ~/bh-build/package/. '$stageWsl/'" | Out-Host
    if ($LASTEXITCODE) { throw "copying WSL package out failed" }
    Write-Ok "BotHider.so built and staged to build/linux-package/"
    return $stage
}

# --- C# ------------------------------------------
function Build-CSharp {
    Write-Step "C# plugin (dotnet $Config)"
    $proj = Join-Path $Root "csharp/BotHiderImpl/BotHiderImpl.csproj"
    $out = Join-Path $Root "build/csharp"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet build $proj -c $Config -o $out | Out-Host
    if ($LASTEXITCODE) { throw "dotnet build failed" }
    if (-not (Test-Path "$out/DemoTracerBotHider.dll")) { throw "C# build produced no DemoTracerBotHider.dll" }
    Write-Ok "DemoTracerBotHider.dll built"
    return $out
}

# --- Dist ------------------------------------------
function Build-Dist([string]$nativePkg, [string]$csharpOut, [string]$destRoot) {
    if (Test-Path $destRoot) { Remove-Item -Recurse -Force $destRoot }
    New-Item -ItemType Directory -Force $destRoot | Out-Null
    if ($nativePkg) {
        Copy-Item -Recurse -Force "$nativePkg/addons" $destRoot
    }
    if ($csharpOut) {
        $cssDir = Join-Path $destRoot "addons/counterstrikesharp"
        $plugDir = Join-Path $cssDir "plugins/DemoTracerBotHider"
        $sharedDir = Join-Path $cssDir "shared"
        $apiDir = Join-Path $sharedDir "DemoTracerBotHiderApi"
        New-Item -ItemType Directory -Force $plugDir | Out-Null
        New-Item -ItemType Directory -Force $apiDir | Out-Null
        Get-ChildItem "$csharpOut/DemoTracerBotHider.*" -File |
        Copy-Item -Destination $plugDir -Force
        Get-ChildItem "$csharpOut/DemoTracerBotHiderApi.*" -File |
        Copy-Item -Destination $apiDir -Force
        if (Test-Path "$csharpOut/shared") {
            Copy-Item -Recurse -Force "$csharpOut/shared/*" $sharedDir
        }
    }
    Write-Ok "assembled $((Resolve-Path $destRoot).Path)"
}

# --- Main ----------------------------------------------------------------
$winPkg = $null; $linPkg = $null; $csOut = $null
if ($Windows) { $winPkg = Build-Windows }
if ($Linux) { $linPkg = Build-Linux }
if ($CSharp) { $csOut = Build-CSharp }

Write-Step "Dist"
if ($Windows) { Build-Dist $winPkg $csOut $DistWin }
if ($Linux) { Build-Dist $linPkg $csOut $DistLin }

Write-Step "Done"
if ($Windows) { Write-Ok "Windows -> dist/windows/" }
if ($Linux) { Write-Ok "Linux   -> dist/linux/" }
if ($CSharp -and -not ($Windows -or $Linux)) { Write-Ok "C#      -> build/csharp/" }
exit 0
