# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$target = [IO.Path]::GetFullPath((Join-Path $repo $Destination))
$allowed = @('.external', 'tmp') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repo $_)) + [IO.Path]::DirectorySeparatorChar }
if (-not @($allowed | Where-Object { $target.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count) {
    throw 'Standalone export must be inside the ignored .external or tmp directory'
}
if (Test-Path (Join-Path $target 'BotRandomizer.cs')) {
    # An existing clean upstream checkout is allowed. Never overwrite edits.
    if (-not (Test-Path (Join-Path $target '.git'))) { throw 'Export destination already has source files' }
    $dirty = git -C $target status --porcelain
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Export destination must be a clean upstream checkout' }
} elseif ((Test-Path $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
    throw 'Export destination must be empty or a clean upstream checkout'
}
$provider = Join-Path $repo 'server/runtime/BotRandomizer'
$copies = [ordered]@{}
foreach ($name in @('BotRandomizer.cs', 'BotRandomizerApiFacade.cs', 'BotRandomizerIntro.cs', 'BotRandomizer.csproj', 'cosmetic_catalog.json', 'charm_placements.json', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'UPSTREAM.md', 'README.md', 'API.md')) {
    $copies[$name] = Join-Path $provider $name
}
foreach ($dir in @('Cosmetics', 'tests', 'tools')) {
    $paths = & rg --files (Join-Path $provider $dir) -g '*.cs' -g '*.csproj' -g '*.ps1' -g '!bin/**' -g '!obj/**' -g '!**/bin/**' -g '!**/obj/**'
    if ($LASTEXITCODE -gt 1) { throw 'Cannot enumerate provider source' }
    foreach ($path in $paths) { $copies[[IO.Path]::GetRelativePath($provider, $path)] = $path }
}
foreach ($name in @('BotRandomizerApi.csproj', 'IBotRandomizerApi.cs', 'UPSTREAM.md', '.gitignore')) {
    $copies['BotRandomizerApi/' + $name] = Join-Path $repo "server/vendor/BotRandomizerApi/$name"
}
$copies['BotRandomizerApi/LICENSE'] = Join-Path $provider 'LICENSE'
foreach ($name in @('package.json', 'package-lock.json', 'source.json', 'source.mjs', 'layout.json', 'randomizer-policy.json', 'generate-econ-index.mjs', 'generate-randomizer-catalog.mjs', 'catalog.test.mjs', 'update.ps1', 'README.md')) {
    $copies['tools/data/' + $name] = Join-Path $repo "tooling/cs2-lib-data/$name"
}
$copies['cs2-lib-econ-index.v1.json'] = Join-Path $repo 'shared/econ/cs2-lib-econ-index.v1.json'
$copies['NuGet.Config'] = Join-Path $repo 'NuGet.Config'
$copies['.github/workflows/ci.yml'] = Join-Path $provider 'tools/standalone-ci.yml'
$copies['.github/workflows/data.yml'] = Join-Path $provider 'tools/standalone-data.yml'
foreach ($path in $copies.Keys) {
    $to = Join-Path $target $path
    [IO.Directory]::CreateDirectory((Split-Path $to)) | Out-Null
    Copy-Item -LiteralPath $copies[$path] -Destination $to -Force
}
$project = Join-Path $target 'BotRandomizer.csproj'
$xml = (Get-Content $project -Raw).Replace('..\..\vendor\BotRandomizerApi\BotRandomizerApi.csproj', 'BotRandomizerApi\BotRandomizerApi.csproj').Replace('..\..\..\shared\econ\cs2-lib-econ-index.v1.json', 'cs2-lib-econ-index.v1.json').Replace('<Compile Remove="tests\**\*.cs" />', '<Compile Remove="tests\**\*.cs;BotRandomizerApi\**\*.cs" />')
[IO.File]::WriteAllText($project, $xml)
$testProject = Join-Path $target 'tests/BotRandomizer.SelfTest/BotRandomizer.SelfTest.csproj'
[IO.File]::WriteAllText($testProject, (Get-Content $testProject -Raw).Replace('..\..\..\..\vendor\BotRandomizerApi\BotRandomizerApi.csproj', '..\..\BotRandomizerApi\BotRandomizerApi.csproj'))
[IO.File]::WriteAllText((Join-Path $target 'tools/data/layout.json'), "{`n  `"catalog`": `"../../cosmetic_catalog.json`",`n  `"econ`": `"../../cs2-lib-econ-index.v1.json`"`n}`n")
$contract = Get-Content (Join-Path $repo 'shared/contracts/playback-contract.v1.json') -Raw | ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $target 'host-contract.json'), ($contract.hook_runtime | ConvertTo-Json) + "`n")
$upstreamPath = Join-Path $target 'UPSTREAM.md'
[IO.File]::WriteAllText($upstreamPath, (Get-Content $upstreamPath -Raw).Replace('../../../tooling/cs2-lib-data/README.md', 'tools/data/README.md').Replace('../../../docs/SIGNATURES.md', 'https://github.com/unicbm/demotracer/blob/main/docs/SIGNATURES.md'))
[IO.File]::WriteAllText((Join-Path $target '.gitignore'), "**/bin/`n**/obj/`n**/node_modules/`ndist/`n*.user`n*.dem`n*.dtr`n*.log`n")
# Export is a generated review checkout, not a second maintained implementation.
Write-Host "Standalone unified source: $target"
