# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [string]$RuntimePackage = "server\runtime\dtr-controller\build\package",
    [string]$RuntimeBuild = "server\runtime\dtr-controller\build",
    [string]$BotHiderRuntimePackage = "server\runtime\dtr-hider\build\package",
    [string]$BotHiderRuntimeBuild = "server\runtime\dtr-hider\build",
    [string]$BotRandomizerPackage = "",
    [string]$DotnetPath = "",
    [switch]$BuildRuntime,
    [switch]$BuildBotHiderRuntime,
    [switch]$SkipCssBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRootPath = Join-Path $repoRoot $OutputRoot
$packageName = "demotracer-css-v$Version"
$stageRoot = Join-Path $outputRootPath $packageName
$zipPath = Join-Path $outputRootPath "$packageName.zip"
$runtimeRoot = if ([System.IO.Path]::IsPathRooted($RuntimePackage)) {
    $RuntimePackage
} else {
    Join-Path $repoRoot $RuntimePackage
}
$botHiderRuntimeRoot = if ([System.IO.Path]::IsPathRooted($BotHiderRuntimePackage)) {
    $BotHiderRuntimePackage
} else {
    Join-Path $repoRoot $BotHiderRuntimePackage
}
$cssOut = Join-Path $repoRoot "server\plugins\DemoTracer\src\DemoTracer\bin\$Configuration\net10.0"
$commonRoot = Join-Path $repoRoot "server\runtime\common"
$apiOut = Join-Path $commonRoot "csharp\DemoTracerApi\bin\$Configuration\net10.0"
$botHiderCssOut = Join-Path $repoRoot "server\runtime\dtr-hider\csharp\DtrHider\bin\$Configuration\net10.0"
$botHiderApiOut = Join-Path $repoRoot "server\runtime\dtr-hider\csharp\DtrHiderApi\bin\$Configuration\net10.0"
$playbackContractPath = Join-Path $repoRoot "shared\contracts\playback-contract.v1.json"
$nugetConfigPath = Join-Path $repoRoot "NuGet.Config"
$componentArguments = @(
    "-p:DtrCommonRoot=$commonRoot",
    "-p:DtrHiderRoot=$(Join-Path $repoRoot 'server/runtime/dtr-hider')",
    "-p:DtrRandomizerRoot=$(Join-Path $repoRoot 'server/runtime/BotRandomizer')"
)

& (Join-Path $PSScriptRoot "assert-clean-worktree.ps1") -RepoRoot $repoRoot
& (Join-Path $PSScriptRoot "check-playback-contract.ps1") -PlaybackVersion $Version

function Require-Path([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
}

function Test-SameFullPath([string]$Left, [string]$Right) {
    $leftPath = [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/')
    $rightPath = [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/')
    return [System.StringComparer]::OrdinalIgnoreCase.Equals($leftPath, $rightPath)
}

function Assert-ExternalRuntimeReceipt(
    [string]$Root,
    [string[]]$RequiredPaths,
    [string]$Component,
    [string]$Label,
    [object]$ExpectedContract
) {
    $receiptPath = Join-Path $Root "addons\demotracer-install.v1.json"
    Require-Path $receiptPath "$Label source receipt"
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.schema_version -ne 1 -or
        $receipt.product -ne $ExpectedContract.product -or
        $receipt.platform -ne $ExpectedContract.platform -or
        $receipt.compatibility.bot_controller.abi_major -ne $ExpectedContract.bot_controller.abi_major -or
        $receipt.compatibility.bot_controller.min_abi_minor -lt $ExpectedContract.bot_controller.min_abi_minor -or
        $receipt.compatibility.bot_controller.required_capabilities_hex -ne $ExpectedContract.bot_controller.required_capabilities_hex -or
        $receipt.compatibility.bot_hider.api -ne $ExpectedContract.bot_hider.api -or
        $receipt.compatibility.demotracer.companion_api -ne $ExpectedContract.demotracer.companion_api) {
        throw "$Label source receipt does not match the current DemoTracer playback contract: $receiptPath"
    }
    foreach ($field in @("backend", "metamod_minimum_build", "metamod_plugin_api", "metamod_source_commit", "khook_source_commit", "counterstrikesharp_source_commit")) {
        if ($receipt.compatibility.hook_runtime.$field -ne $ExpectedContract.hook_runtime.$field) {
            throw "$Label source receipt has incompatible hook runtime field '$field': $receiptPath"
        }
    }

    $entries = @($receipt.files | Where-Object { $_.component -eq $Component })
    if ($entries.Count -eq 0) {
        throw "$Label source receipt does not contain component $Component"
    }
    foreach ($requiredPath in $RequiredPaths) {
        $receiptRequiredPath = $requiredPath.Replace('\', '/')
        if (-not ($entries | Where-Object {
            [System.StringComparer]::OrdinalIgnoreCase.Equals([string]$_.path, $receiptRequiredPath)
        } | Select-Object -First 1)) {
            throw "$Label source receipt does not contain required file $receiptRequiredPath"
        }
    }

    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        $relativePath = ([string]$entry.path).Replace('\', '/')
        $segments = @($relativePath.Split('/'))
        $unsafeSegment = $segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':')
        } | Select-Object -First 1
        if ($segments.Count -lt 2 -or
            $segments[0] -ne 'addons' -or
            $null -ne $unsafeSegment) {
            throw "$Label source receipt contains an unsafe path: $relativePath"
        }
        if (-not $seenPaths.Add($relativePath)) {
            throw "$Label source receipt contains a duplicate path: $relativePath"
        }
        $filePath = Join-Path $Root $relativePath.Replace('/', '\')
        Require-Path $filePath "$Label receipt file"
        $actual = Get-Item -LiteralPath $filePath
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actual.Length -ne [long]$entry.size -or
            -not [System.StringComparer]::OrdinalIgnoreCase.Equals($actualHash, [string]$entry.sha256)) {
            throw "$Label source component no longer matches its DemoTracer receipt: $filePath"
        }
    }
}

function Assert-BinaryContainsExport([string]$Path, [string]$ExportName) {
    Require-Path $Path "native runtime DLL"
    $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Path))
    if ($ascii.IndexOf($ExportName + [char]0, [System.StringComparison]::Ordinal) -lt 0) {
        throw "BotController runtime is older than the packaged ABI metadata (missing export $ExportName): $Path. Rebuild it with -BuildRuntime or pass a current runtime package."
    }
}

function Copy-RequiredFile([string]$Source, [string]$Destination) {
    Require-Path $Source "required file"
    $destinationDir = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

function Test-DotnetHasSdk([string]$Command) {
    try {
        $sdks = & $Command --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $false
        }
        return $null -ne ($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)
    } catch {
        return $false
    }
}

function Resolve-DotnetPath([string]$PreferredPath) {
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        if (Test-DotnetHasSdk $PreferredPath) {
            return $PreferredPath
        }
        throw "dotnet SDK not found at preferred path: $PreferredPath"
    }

    $candidates = @()
    $candidates += (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
    if ($env:DOTNET_ROOT_X64) {
        $candidates += (Join-Path $env:DOTNET_ROOT_X64 "dotnet.exe")
    }
    if ($env:DOTNET_ROOT) {
        $candidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe")
    }
    $candidates += "C:\Program Files\dotnet\dotnet.exe"
    $candidates += "C:\Program Files (x86)\dotnet\dotnet.exe"

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-DotnetHasSdk $candidate) {
            return $candidate
        }
    }

    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command -and (Test-DotnetHasSdk $command.Source)) {
        return $command.Source
    }
    throw "dotnet 10 SDK not found. Install .NET 10 SDK or pass -DotnetPath to a dotnet.exe that provides it."
}

if ($BuildRuntime) {
    Invoke-Checked "cmake" @("--build", (Join-Path $repoRoot $RuntimeBuild), "--config", $Configuration, "--target", "dtr-controller")
}

if ($BuildBotHiderRuntime) {
    Invoke-Checked "cmake" @("--build", (Join-Path $repoRoot $BotHiderRuntimeBuild), "--config", $Configuration, "--target", "dtr-hider")
}

if (-not $SkipCssBuild) {
    $resolvedDotnetPath = Resolve-DotnetPath $DotnetPath
    # Keep build-machine paths out of assembly debug records and optional symbols.
    $sourcePathMap = "-p:PathMap=$repoRoot=/_/demotracer"
    $demoTracerProject = Join-Path $repoRoot "server\plugins\DemoTracer\src\DemoTracer\DemoTracer.csproj"
    $botHiderProject = Join-Path $repoRoot "server\runtime\dtr-hider\csharp\DtrHider\DtrHider.csproj"
    Invoke-Checked $resolvedDotnetPath (@("restore", $demoTracerProject, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false") + $componentArguments)
    Invoke-Checked $resolvedDotnetPath (@("restore", $botHiderProject, "--configfile", $nugetConfigPath, "-m:1", "-nodeReuse:false", "-p:NuGetAudit=false") + $componentArguments)
    Invoke-Checked $resolvedDotnetPath (@("build", $demoTracerProject, "-c", $Configuration, "--no-restore", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", $sourcePathMap) + $componentArguments)
    Invoke-Checked $resolvedDotnetPath (@("build", $botHiderProject, "-c", $Configuration, "--no-restore", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", $sourcePathMap) + $componentArguments)
}

Require-Path $playbackContractPath "playback compatibility contract"
$playbackContract = Get-Content -LiteralPath $playbackContractPath -Raw -Encoding UTF8 | ConvertFrom-Json
$randomizerTools = Join-Path $repoRoot "server\runtime\BotRandomizer\tools"
if (-not $BotRandomizerPackage) {
    $randomizerOutput = Join-Path $outputRootPath "randomizer"
    $randomizerDotnet = Resolve-DotnetPath $DotnetPath
    & (Join-Path $randomizerTools "package.ps1") -OutputDirectory $randomizerOutput -DotnetPath $randomizerDotnet -CommonRoot $commonRoot -SkipBuild:$SkipCssBuild | Out-Host
    $BotRandomizerPackage = Join-Path $randomizerOutput "BotRandomizer-v$($playbackContract.bot_randomizer.provider_version).zip"
}
# Consume the same public package ordinary bot servers install. No replay-only build.
if (-not [IO.Path]::IsPathRooted($BotRandomizerPackage)) { $BotRandomizerPackage = Join-Path $repoRoot $BotRandomizerPackage }
$randomizerStage = Join-Path $outputRootPath ("randomizer-import-" + [guid]::NewGuid().ToString("N"))
& (Join-Path $randomizerTools "import-package.ps1") -Package $BotRandomizerPackage -DestinationRoot $randomizerStage -ExpectedContractPath $playbackContractPath | Out-Null
$botRandomizerOut = Join-Path $randomizerStage "addons\counterstrikesharp\plugins\BotRandomizer"
$botRandomizerApiOut = Join-Path $randomizerStage "addons\counterstrikesharp\shared\BotRandomizerApi"
if ((Get-FileHash (Join-Path $botRandomizerOut "cs2-lib-econ-index.v1.json")).Hash -ne
    (Get-FileHash (Join-Path $commonRoot "econ\cs2-lib-econ-index.v1.json")).Hash) {
    throw "Common Randomizer package and playback consumers must use the same econ catalog"
}
$defaultRuntimeRoot = Join-Path $repoRoot "server\runtime\dtr-controller\build\package"
$defaultBotHiderRuntimeRoot = Join-Path $repoRoot "server\runtime\dtr-hider\build\package"
if (-not (Test-SameFullPath $runtimeRoot $defaultRuntimeRoot)) {
    Assert-ExternalRuntimeReceipt `
        -Root $runtimeRoot `
        -RequiredPaths @(
            "addons\dtr-controller\bin\win64\dtr-controller.dll",
            "addons\dtr-controller\gamedata.json",
            "addons\metamod\dtr-controller.vdf"
        ) `
        -Component "bot_controller" `
        -Label "BotController" `
        -ExpectedContract $playbackContract
}
if (-not (Test-SameFullPath $botHiderRuntimeRoot $defaultBotHiderRuntimeRoot)) {
    Assert-ExternalRuntimeReceipt `
        -Root $botHiderRuntimeRoot `
        -RequiredPaths @(
            "addons\dtr-hider\bin\win64\dtr-hider.dll",
            "addons\dtr-hider\gamedata.json",
            "addons\dtr-hider\map_whitelist.json"
        ) `
        -Component "bot_hider_native" `
        -Label "BotHider" `
        -ExpectedContract $playbackContract
}

$runtimeDll = Join-Path $runtimeRoot "addons\dtr-controller\bin\win64\dtr-controller.dll"
Require-Path $runtimeDll "BotController runtime DLL"
Assert-BinaryContainsExport $runtimeDll "DtrController_GetAbiInfo"
Assert-BinaryContainsExport $runtimeDll "DtrController_GetCapabilities"
Assert-BinaryContainsExport $runtimeDll "DtrController_GetBuyStatus"
Assert-BinaryContainsExport $runtimeDll "DtrController_RequestEquipBestWeapon"
Assert-BinaryContainsExport $runtimeDll "DtrController_GetBuildId"
Assert-BinaryContainsExport $runtimeDll "DtrController_ReleaseReplayBuffer"
Assert-BinaryContainsExport $runtimeDll "DtrController_SetReplayPawnEquipment"
Assert-BinaryContainsExport $runtimeDll "DtrController_GetReplayPawnEquipmentState"
Require-Path (Join-Path $runtimeRoot "addons\dtr-controller\gamedata.json") "BotController gamedata"
Require-Path (Join-Path $runtimeRoot "addons\metamod\dtr-controller.vdf") "BotController Metamod VDF"
Require-Path (Join-Path $botHiderRuntimeRoot "addons\dtr-hider\bin\win64\dtr-hider.dll") "DemoTracer BotHider runtime DLL"
foreach ($export in @("DtrHider_GetNativeAbi", "DtrHider_GetSession", "DtrHider_ReadSlot", "DtrHider_ReadSignature", "DtrHider_PublishIdentity", "DtrHider_SetOption")) {
    Assert-BinaryContainsExport (Join-Path $botHiderRuntimeRoot "addons\dtr-hider\bin\win64\dtr-hider.dll") $export
}
Require-Path (Join-Path $botHiderRuntimeRoot "addons\dtr-hider\gamedata.json") "DemoTracer BotHider gamedata"
Require-Path (Join-Path $cssOut "DemoTracer.dll") "DemoTracer CSS plugin"
Require-Path (Join-Path $apiOut "DemoTracerApi.dll") "DemoTracer API assembly"
Require-Path (Join-Path $botRandomizerApiOut "BotRandomizerApi.dll") "BotRandomizer API assembly"
Require-Path (Join-Path $botRandomizerOut "BotRandomizer.dll") "BotRandomizer provider assembly"
Require-Path (Join-Path $botRandomizerOut "BotRandomizer.deps.json") "BotRandomizer provider dependency manifest"
Require-Path (Join-Path $botRandomizerOut "cosmetic_catalog.json") "BotRandomizer cosmetic catalog"
Require-Path (Join-Path $botRandomizerOut "cs2-lib-econ-index.v1.json") "BotRandomizer replay econ index"
Require-Path (Join-Path $botRandomizerOut "charm_placements.json") "BotRandomizer charm placements"
Require-Path (Join-Path $botHiderCssOut "DtrHider.dll") "DemoTracer BotHider CSS plugin"
Require-Path (Join-Path $botHiderApiOut "DtrHiderApi.dll") "DemoTracer BotHider API assembly"

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$addonsOut = Join-Path $stageRoot "addons"
New-Item -ItemType Directory -Force -Path $addonsOut | Out-Null
$botControllerOut = Join-Path $addonsOut "dtr-controller"
Copy-RequiredFile (Join-Path $runtimeRoot "addons\dtr-controller\bin\win64\dtr-controller.dll") `
    (Join-Path $botControllerOut "bin\win64\dtr-controller.dll")
Copy-RequiredFile (Join-Path $runtimeRoot "addons\dtr-controller\gamedata.json") `
    (Join-Path $botControllerOut "gamedata.json")
Copy-RequiredFile (Join-Path $runtimeRoot "addons\metamod\dtr-controller.vdf") `
    (Join-Path $addonsOut "metamod\dtr-controller.vdf")
$botHiderOut = Join-Path $addonsOut "dtr-hider"
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\dtr-hider\bin\win64\dtr-hider.dll") `
    (Join-Path $botHiderOut "bin\win64\dtr-hider.dll")
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\dtr-hider\gamedata.json") `
    (Join-Path $botHiderOut "gamedata.json")
Copy-RequiredFile (Join-Path $botHiderRuntimeRoot "addons\dtr-hider\map_whitelist.json") `
    (Join-Path $botHiderOut "map_whitelist.json")

$pluginOut = Join-Path $stageRoot "addons\counterstrikesharp\plugins\DemoTracer"
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.deps.json") (Join-Path $pluginOut "DemoTracer.deps.json")
Copy-RequiredFile (Join-Path $cssOut "DemoTracer.dll") (Join-Path $pluginOut "DemoTracer.dll")
Copy-RequiredFile (Join-Path $cssOut "ZstdSharp.dll") (Join-Path $pluginOut "ZstdSharp.dll")
Copy-RequiredFile (Join-Path $cssOut "cs2-lib-econ-index.v1.json") (Join-Path $pluginOut "cs2-lib-econ-index.v1.json")
Copy-RequiredFile (Join-Path $cssOut "demotracer-native.json") (Join-Path $pluginOut "demotracer-native.json")
$demoTracerApiSharedOut = Join-Path $stageRoot "addons\counterstrikesharp\shared\DemoTracerApi"
Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.dll") (Join-Path $demoTracerApiSharedOut "DemoTracerApi.dll")
$botRandomizerApiSharedOut = Join-Path $stageRoot "addons\counterstrikesharp\shared\BotRandomizerApi"
Copy-RequiredFile (Join-Path $botRandomizerApiOut "BotRandomizerApi.dll") (Join-Path $botRandomizerApiSharedOut "BotRandomizerApi.dll")
$botRandomizerPluginOut = Join-Path $stageRoot "addons\counterstrikesharp\plugins\BotRandomizer"
Copy-RequiredFile (Join-Path $botRandomizerOut "BotRandomizer.deps.json") (Join-Path $botRandomizerPluginOut "BotRandomizer.deps.json")
Copy-RequiredFile (Join-Path $botRandomizerOut "BotRandomizer.dll") (Join-Path $botRandomizerPluginOut "BotRandomizer.dll")
Copy-RequiredFile (Join-Path $botRandomizerOut "cosmetic_catalog.json") (Join-Path $botRandomizerPluginOut "cosmetic_catalog.json")
Copy-RequiredFile (Join-Path $botRandomizerOut "cs2-lib-econ-index.v1.json") (Join-Path $botRandomizerPluginOut "cs2-lib-econ-index.v1.json")
Copy-RequiredFile (Join-Path $botRandomizerOut "charm_placements.json") (Join-Path $botRandomizerPluginOut "charm_placements.json")

$botHiderPluginOut = Join-Path $stageRoot "addons\counterstrikesharp\plugins\DtrHider"
Copy-RequiredFile (Join-Path $botHiderCssOut "DtrHider.deps.json") (Join-Path $botHiderPluginOut "DtrHider.deps.json")
Copy-RequiredFile (Join-Path $botHiderCssOut "DtrHider.dll") (Join-Path $botHiderPluginOut "DtrHider.dll")
$botHiderSharedOut = Join-Path $stageRoot "addons\counterstrikesharp\shared\DtrHiderApi"
Copy-RequiredFile (Join-Path $botHiderApiOut "DtrHiderApi.dll") (Join-Path $botHiderSharedOut "DtrHiderApi.dll")
$harmonySource = Join-Path $botHiderCssOut "shared\0Harmony\0Harmony.dll"
Copy-RequiredFile $harmonySource (Join-Path $stageRoot "addons\counterstrikesharp\shared\0Harmony\0Harmony.dll")
if ($IncludeSymbols) {
    Copy-RequiredFile (Join-Path $cssOut "DemoTracer.pdb") (Join-Path $pluginOut "DemoTracer.pdb")
    Copy-RequiredFile (Join-Path $apiOut "DemoTracerApi.pdb") (Join-Path $demoTracerApiSharedOut "DemoTracerApi.pdb")
    # The common Randomizer package is identical across consumers and omits symbols.
    Copy-RequiredFile (Join-Path $botHiderCssOut "DtrHider.pdb") (Join-Path $botHiderPluginOut "DtrHider.pdb")
    Copy-RequiredFile (Join-Path $botHiderApiOut "DtrHiderApi.pdb") (Join-Path $botHiderSharedOut "DtrHiderApi.pdb")
}


$gitCommit = (git -C $repoRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommit)) {
    throw "Unable to resolve the release Git commit."
}

$receiptFiles = @(
    Get-ChildItem -LiteralPath $addonsOut -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($stageRoot, $_.FullName).Replace('\', '/')
            $component = if ($relativePath -like "addons/dtr-controller/*" -or $relativePath -eq "addons/metamod/dtr-controller.vdf") {
                "bot_controller"
            } elseif ($relativePath -like "addons/dtr-hider/*" -or $relativePath -eq "addons/metamod/dtr-hider.vdf") {
                "bot_hider_native"
            } elseif ($relativePath -like "addons/counterstrikesharp/plugins/DtrHider/*" -or
                      $relativePath -like "addons/counterstrikesharp/shared/DtrHiderApi/*") {
                "bot_hider_managed"
            } elseif ($relativePath -like "addons/counterstrikesharp/plugins/DemoTracer/*" -or
                      $relativePath -like "addons/counterstrikesharp/shared/DemoTracerApi/*") {
                "demotracer"
            } elseif ($relativePath -like "addons/counterstrikesharp/plugins/BotRandomizer/*" -or
                      $relativePath -like "addons/counterstrikesharp/shared/BotRandomizerApi/*") {
                "bot_randomizer_managed"
            } else {
                "shared_dependency"
            }
            [ordered]@{
                path = $relativePath
                component = $component
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$installReceipt = [ordered]@{
    schema_version = 1
    product = "CS2 DemoTracer Playback Bundle"
    bundle_version = $Version
    git_commit = $gitCommit
    platform = "windows-x64"
    compatibility = $playbackContract
    files = $receiptFiles
}
$installReceiptPath = Join-Path $addonsOut "demotracer-install.v1.json"
$installReceipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $installReceiptPath -Encoding UTF8

$allowedExtensions = @('.dll', '.json', '.vdf')
if ($IncludeSymbols) { $allowedExtensions += '.pdb' }
$unexpected = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File | Where-Object {
    $_.Extension -notin $allowedExtensions -or $_.Name -like '*.example.json'
})
if ($unexpected.Count -gt 0) {
    throw "Playback bundle contains non-runtime files: $($unexpected.Name -join ', ')"
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -Force

Write-Host "Wrote $zipPath"
