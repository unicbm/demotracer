# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [string]$PlaybackVersion = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Read-Text([string]$RelativePath) {
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "release contract source not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

function Read-RegexValue([string]$RelativePath, [string]$Pattern, [string]$Label) {
    $match = [regex]::Match((Read-Text $RelativePath), $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $match.Success) {
        throw "could not read $Label from $RelativePath"
    }
    return $match.Groups[1].Value
}

function Assert-Equal([string]$Label, [string]$Actual, [string]$Expected) {
    if (-not [System.StringComparer]::Ordinal.Equals($Actual, $Expected)) {
        throw "$Label mismatch: expected '$Expected', found '$Actual'"
    }
}

function Assert-TextPresent([string]$RelativePath, [string]$Pattern, [string]$Label) {
    if (-not [regex]::IsMatch((Read-Text $RelativePath), $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        throw "$Label is missing from $RelativePath"
    }
}

function Read-CargoPackageVersion([string]$RelativePath, [string]$PackageName) {
    $escaped = [regex]::Escape($PackageName)
    return Read-RegexValue $RelativePath "(?ms)^\[\[package\]\]\s*\r?\nname = `"$escaped`"\s*\r?\nversion = `"([^`"]+)`"" "$PackageName lock version"
}

$contract = (Read-Text "shared\contracts\playback-contract.v1.json") | ConvertFrom-Json
Assert-Equal "inventory plan reader" (Read-RegexValue "server/plugins/DemoTracer/src/DemoTracer/Native/BotControllerNativeTypes.cs" 'CurrentSchemaVersion\s*=\s*(\d+)' "metadata schema") ([string]$contract.inventory_plan_schema)
Assert-Equal "inventory plan maximum reader" ([string]$contract.inventory_plan_reader.max) ([string]$contract.inventory_plan_schema)
Assert-Equal "inventory plan CSS adapter" (Read-RegexValue "server/plugins/DemoTracer/src/DemoTracer/Data/DtrReplayReaderValidation.cs" 'metadata\.SchemaVersion == (\d+)' "metadata adapter") ([string]$contract.inventory_plan_reader.min)
foreach ($id in @("dtr-controller", "dtr-hider")) {
    $root = "server/runtime/$id"
    $componentContract = if ($id -eq "dtr-controller") { $contract.bot_controller } else { $contract.bot_hider }
    Assert-Equal "$id native identity" $componentContract.native_library $id
    Assert-Equal "$id CMake output" (Read-RegexValue "$root/CMakeLists.txt" 'OUTPUT_NAME "([^"]+)"' "native output") $id
    Assert-Equal "$id Metamod alias" (Read-RegexValue "$root/cmake/$id.vdf.in" '"alias"\s+"([^"]+)"' "Metamod alias") $id
}
Assert-Equal "hider managed identity" $contract.bot_hider.managed_assembly "DtrHider"
Assert-Equal "hider API identity" $contract.bot_hider.managed_api "DtrHiderApi"
foreach ($assembly in @("DtrHider", "DtrHiderApi")) {
    Assert-Equal "hider $assembly output" (Read-RegexValue "server/runtime/dtr-hider/csharp/$assembly/$assembly.csproj" '<AssemblyName>([^<]+)</AssemblyName>' "assembly") $assembly
}
Assert-Equal "hider capability" (Read-RegexValue "server/runtime/dtr-hider/csharp/DtrHiderApi/IDtrHiderApi.cs" 'Capability = "([^"]+)"' "capability") $contract.bot_hider.capability
$sourceRegistry = (Read-Text "server\runtime\common\contracts\replay-source-fields.v1.json") | ConvertFrom-Json
$nativeSource = Read-Text "server\runtime\dtr-controller\src\BotRecorder\ReplaySourceState.h"
$managedSource = Read-Text "server\plugins\DemoTracer\src\DemoTracer\Data\DtrReplayReaderSourceState.cs"
$managedFields = [regex]::Matches($managedSource, 'SourceKind\.(\w+), // (\w+)')
$nativeFields = [regex]::Matches($nativeSource, '\{Target::(\w+), Kind::(\w+), ClockKind::(\w+), "([^"]+)", (\d+)\}')
Assert-Equal "source-state managed field count" $managedFields.Count $sourceRegistry.fields.Count
Assert-Equal "source-state native field count" $nativeFields.Count $sourceRegistry.fields.Count
for ($fieldIndex = 0; $fieldIndex -lt $sourceRegistry.fields.Count; $fieldIndex++) {
    $field = $sourceRegistry.fields[$fieldIndex]
    Assert-Equal "source-state field ID" $field.id $fieldIndex
    Assert-Equal "source-state managed name $fieldIndex" $managedFields[$fieldIndex].Groups[2].Value $field.name
    Assert-Equal "source-state managed type $fieldIndex" $managedFields[$fieldIndex].Groups[1].Value $field.kind
    $nativeField = $nativeFields[$fieldIndex]
    Assert-Equal "source-state native target $fieldIndex" $nativeField.Groups[1].Value $field.target
    Assert-Equal "source-state native type $fieldIndex" $nativeField.Groups[2].Value $field.kind
    Assert-Equal "source-state native clock $fieldIndex" $nativeField.Groups[3].Value $field.clock
    Assert-Equal "source-state native member $fieldIndex" $nativeField.Groups[4].Value $field.field
    $element = if ($null -ne $field.component) { [int]$field.component } else { [int]$field.native_element }
    Assert-Equal "source-state native element $fieldIndex" $nativeField.Groups[5].Value ($element * 4)
    $enumPattern = '\b' + [regex]::Escape($field.name) + '\s*=\s*' + $fieldIndex + '\s*,'
    if (-not [regex]::IsMatch($nativeSource, $enumPattern)) { throw "source-state native ID mismatch: $($field.name)" }
}

$playbackSourceVersion = Read-RegexValue "server/plugins/DemoTracer/src/DemoTracer/Lifecycle/DemoTracerPlugin.cs" 'ModuleVersion\s*=>\s*"([^"]+)"' "DemoTracer module version"
if ([string]::IsNullOrWhiteSpace($PlaybackVersion)) { $PlaybackVersion = $playbackSourceVersion }
Assert-Equal "DemoTracer ModuleVersion" $playbackSourceVersion $PlaybackVersion
Assert-Equal "stable DTR writer" ([string]$contract.dtr_writer) "12"
Assert-Equal "stable DTR reader minimum" ([string]$contract.dtr_reader.min) "12"
Assert-Equal "stable DTR reader maximum" ([string]$contract.dtr_reader.max) "12"

Assert-Equal "CSS manifest ABI" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\Lifecycle\DemoTracerPlugin.cs" 'ManifestAbiVersion\s*=\s*(\d+)' "manifest ABI") ([string]$contract.manifest_abi)
Assert-Equal "DTR section codec" ([string]$contract.dtr_section_writer_codec) "zstd"
Assert-TextPresent "server\plugins\DemoTracer\src\DemoTracer\DemoTracer.csproj" 'ZstdSharp\.Port" Version="0\.8\.8"' "managed Zstd decoder"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'Copy-RequiredFile[^\r\n]+ZstdSharp\.dll[^\r\n]+ZstdSharp\.dll' "packaged Zstd decoder"
Assert-Equal "CSS minimum DTR reader" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\Native\BotControllerNativeTypes.cs" 'MinRecFormatVersion\s*=\s*(\d+)' "minimum DTR reader") ([string]$contract.dtr_reader.min)
Assert-Equal "CSS maximum DTR reader" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\Native\BotControllerNativeTypes.cs" 'RecFormatVersion\s*=\s*(\d+)' "maximum DTR reader") ([string]$contract.dtr_reader.max)
Assert-Equal "CSS native ABI" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\Native\BotControllerNativeTypes.cs" 'ExpectedAbiVersion\s*=\s*(\d+)' "CSS native ABI") ([string]$contract.bot_controller.abi_major)
Assert-Equal "runtime native ABI" (Read-RegexValue "server\runtime\dtr-controller\src\common\exports.cpp" 'kBotControllerAbiMajor\s*=\s*(\d+)' "runtime native ABI") ([string]$contract.bot_controller.abi_major)
Assert-Equal "minimum native ABI minor" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\Native\DemoTracerRuntimeHealth.cs" 'MinimumBotControllerAbiMinor\s*=\s*(\d+)' "minimum native ABI minor") ([string]$contract.bot_controller.min_abi_minor)
Assert-Equal "BotController movement input contract" (Read-RegexValue "server\runtime\dtr-controller\src\common\exports.cpp" 'DtrController_GetMovementIntentContractVersion\(\)\s*\{\s*return\s+(\d+)' "movement input contract") ([string]$contract.bot_controller.movement_intent_version)
Assert-Equal "BotController replay tick size" (Read-RegexValue "server\runtime\dtr-controller\src\BotRecorder\MotionRecorder.h" 'sizeof\(ReplayTick\)\s*==\s*(\d+)' "native replay tick size") ([string]$contract.bot_controller.replay_tick_bytes)
Assert-Equal "BotController replay event tail" ([string]$contract.bot_controller.replay_tick_event_tail) "reserved_zero"

$runtimeMinor = [int](Read-RegexValue "server\runtime\dtr-controller\src\common\exports.cpp" 'kBotControllerAbiMinor\s*=\s*(\d+)' "runtime native ABI minor")
if ($runtimeMinor -lt [int]$contract.bot_controller.min_abi_minor) {
    throw "runtime native ABI minor $runtimeMinor is below required $($contract.bot_controller.min_abi_minor)"
}

Assert-Equal "DemoTracer companion API" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\Native\BotControllerNativeTypes.cs" 'DemoTracerApiVersion\s*=\s*(\d+)' "DemoTracer companion API") ([string]$contract.demotracer.companion_api)
Assert-Equal "BotHider API" (Read-RegexValue "server\runtime\dtr-hider\csharp\DtrHiderApi\IDtrHiderApi.cs" 'ApiVersion\s*=\s*(\d+)' "BotHider API") ([string]$contract.bot_hider.api)
Assert-Equal "BotHider clan tag limit" (Read-RegexValue "server\runtime\dtr-hider\csharp\DtrHiderApi\IDtrHiderApi.cs" 'MaxClanTagUtf8Bytes\s*=\s*(\d+)' "BotHider clan tag limit") ([string]$contract.bot_hider.clan_tag_max_utf8_bytes)
Assert-Equal "BotHider native ABI" (Read-RegexValue "server\runtime\dtr-hider\src\presentation_state.h" 'kNativePresentationAbi\s*=\s*(\d+)' "BotHider native ABI") ([string]$contract.bot_hider.native_abi)
Assert-Equal "BotHider managed native ABI" (Read-RegexValue "server\runtime\dtr-hider\csharp\DtrHider\NativePresentationClient.cs" 'NativeAbi\s*=\s*(\d+)' "BotHider managed native ABI") ([string]$contract.bot_hider.native_abi)
Assert-Equal "BotHider native slot bytes" (Read-RegexValue "server\runtime\dtr-hider\src\presentation_state.h" 'sizeof\(PresentationSlot\)\s*==\s*(\d+)' "BotHider native slot bytes") ([string]$contract.bot_hider.native_slot_bytes)
Assert-Equal "BotHider managed slot bytes" (Read-RegexValue "server\runtime\dtr-hider\csharp\DtrHider\NativePresentationClient.cs" 'SlotByteSize\s*=\s*(\d+)' "BotHider managed slot bytes") ([string]$contract.bot_hider.native_slot_bytes)
Assert-Equal "BotHider native version" (Read-RegexValue "server\runtime\dtr-hider\src\plugin.h" 'GetVersion\(\).*?return "([^"]+)"' "BotHider native version") ([string]$contract.bot_hider.native_provider_version)
Assert-Equal "BotHider managed version" (Read-RegexValue "server\runtime\dtr-hider\csharp\DtrHider\DtrHiderPlugin.cs" 'ModuleVersion\s*=>\s*"([^"]+)"' "BotHider managed version") ([string]$contract.bot_hider.managed_provider_version)
Assert-Equal "BotRandomizer API" (Read-RegexValue "server\runtime\BotRandomizer\BotRandomizerApi\IBotRandomizerApi.cs" 'ApiVersion\s*=\s*(\d+)' "BotRandomizer API") ([string]$contract.bot_randomizer.api)
Assert-Equal "BotRandomizer provider" (Read-RegexValue "server\runtime\BotRandomizer\BotRandomizer.cs" 'ModuleVersion\s*=>\s*"([^"]+)"' "BotRandomizer provider version") ([string]$contract.bot_randomizer.provider_version)
Assert-Equal "BotRandomizer assembly" (Read-RegexValue "server\runtime\BotRandomizer\BotRandomizer.csproj" '<Version>([^<]+)</Version>' "BotRandomizer assembly version") ([string]$contract.bot_randomizer.provider_version)
Assert-TextPresent "tooling\scripts\package-server.ps1" 'import-package\.ps1' "common Randomizer package import"
Assert-Equal "DemoTracer target framework" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\DemoTracer.csproj" '<TargetFramework>([^<]+)</TargetFramework>' "DemoTracer target framework") ([string]$contract.counterstrikesharp.target_framework)
Assert-Equal "CounterStrikeSharp minimum version" (Read-RegexValue "server\plugins\DemoTracer\src\DemoTracer\DemoTracer.csproj" 'CounterStrikeSharp\.API" Version="([^"]+)"' "CounterStrikeSharp version") ([string]$contract.counterstrikesharp.minimum_version)
Assert-Equal "native hook backend" ([string]$contract.hook_runtime.backend) "khook"
Assert-Equal "Metamod plugin API" ([string]$contract.hook_runtime.metamod_plugin_api) "18"
Assert-Equal "runtime native ABI minor" (Read-RegexValue "server\runtime\dtr-controller\src\common\exports.cpp" 'kBotControllerAbiMinor\s*=\s*(\d+)' "runtime native ABI minor") ([string]$contract.bot_controller.min_abi_minor)
foreach ($pin in @("metamod_source_commit", "khook_source_commit", "counterstrikesharp_source_commit")) {
    if ([string]$contract.hook_runtime.$pin -notmatch '^[0-9a-f]{40}$') { throw "Invalid hook runtime pin: $pin" }
}
$commonHooks = (Read-Text "server\runtime\common\contracts\hook-runtime.v1.json") | ConvertFrom-Json
foreach ($field in @("backend", "metamod_minimum_build", "metamod_plugin_api", "metamod_source_commit", "khook_source_commit", "counterstrikesharp_source_commit")) {
    Assert-Equal "common hook runtime $field" ([string]$commonHooks.$field) ([string]$contract.hook_runtime.$field)
}
foreach ($runtime in @("dtr-controller", "dtr-hider")) {
    Assert-TextPresent "server\runtime\$runtime\CMakeLists.txt" 'native/khook\.cmake' "$runtime shared KHook interface"
}


Assert-TextPresent "tooling\scripts\package-server.ps1" 'demotracer-css-v\$Version' "CSS release asset name"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'addons\\counterstrikesharp\\shared\\BotRandomizerApi' "packaged BotRandomizer API directory"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'Copy-RequiredFile[^\r\n]+BotRandomizerApi\.dll[^\r\n]+BotRandomizerApi\.dll' "packaged BotRandomizer API assembly"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'addons\\counterstrikesharp\\plugins\\BotRandomizer' "packaged BotRandomizer provider directory"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'Copy-RequiredFile[^\r\n]+BotRandomizer\.dll[^\r\n]+BotRandomizer\.dll' "packaged BotRandomizer provider assembly"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'Copy-RequiredFile[^\r\n]+cosmetic_catalog\.json[^\r\n]+cosmetic_catalog\.json' "packaged BotRandomizer cosmetic catalog"
Assert-TextPresent "tooling\scripts\package-server.ps1" 'Copy-RequiredFile[^\r\n]+cs2-lib-econ-index\.v1\.json[^\r\n]+cs2-lib-econ-index\.v1\.json' "packaged BotRandomizer replay econ index"
& (Join-Path $PSScriptRoot "check-first-party-headers.ps1") -RepoRoot $repoRoot -PlaybackOnly
Write-Host "Playback contract verified for v$PlaybackVersion (DTR v12)."
