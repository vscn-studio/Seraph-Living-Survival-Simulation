[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$VsGamePath = '',
    [string]$VsImGuiPath = '',
    [string]$RealSmokePath = '',
    [switch]$RunTests,
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
if ([string]::IsNullOrWhiteSpace($VsGamePath)) {
    $VsGamePath = Join-Path $root '..\vs_client_linux-x64_1.22.7\vintagestory'
}
if ([string]::IsNullOrWhiteSpace($VsImGuiPath)) {
    $VsImGuiPath = Join-Path $root '..\vsimgui_1.2.7'
}

function Resolve-InputPath([string]$value, [string]$name) {
    $candidate = if ([IO.Path]::IsPathRooted($value)) { $value } else { Join-Path $root $value }
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "$name does not exist: $candidate"
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

$VsGamePath = Resolve-InputPath $VsGamePath 'VsGamePath'
$VsImGuiPath = Resolve-InputPath $VsImGuiPath 'VsImGuiPath'
if ([string]::IsNullOrWhiteSpace($RealSmokePath)) {
    $installed = @(Get-ChildItem -Path (Join-Path $env:APPDATA 'MVL/Modpack/测试服务器/Cache/unpack/Real Smoke*/RealSmoke.dll') -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    if ($installed.Count) { $RealSmokePath = $installed[0].FullName }
}
if ($RealSmokePath) {
    if (-not (Test-Path -LiteralPath $RealSmokePath -PathType Leaf)) { throw "RealSmokePath does not exist: $RealSmokePath" }
    $RealSmokePath = (Resolve-Path -LiteralPath $RealSmokePath).Path
}
$solution = Join-Path $root 'Seraph.slnx'
$packages = Join-Path $root 'artifacts'
$testProjects = @(
    (Join-Path $root 'tests\Seraph.Core.Tests\Seraph.Core.Tests.csproj'),
    (Join-Path $root 'tests\Seraph.Application.Tests\Seraph.Application.Tests.csproj')
)
$requiredInputs = @(
    (Join-Path $VsGamePath 'VintagestoryAPI.dll'),
    (Join-Path $VsGamePath 'Lib\protobuf-net.dll'),
    (Join-Path $VsGamePath 'Lib\OpenTK.Graphics.dll'),
    (Join-Path $VsGamePath 'Lib\OpenTK.Mathematics.dll'),
    (Join-Path $VsGamePath 'Lib\cairo-sharp.dll'),
    (Join-Path $VsGamePath 'Lib\0Harmony.dll'),
    (Join-Path $VsGamePath 'Mods\VSSurvivalMod.dll'),
    (Join-Path $VsGamePath 'Mods\VSEssentials.dll'),
    (Join-Path $VsImGuiPath 'VSImGui.dll'),
    (Join-Path $VsImGuiPath 'ImGui.NET.dll')
)
if ($RealSmokePath) { $requiredInputs += $RealSmokePath }
foreach ($input in $requiredInputs) {
    if (-not (Test-Path -LiteralPath $input -PathType Leaf)) {
        throw "Required local reference is missing: $input"
    }
}
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) { throw "Solution not found: $solution" }
& (Join-Path $root 'scripts/Test-Localization.ps1')
& (Join-Path $root 'scripts/Test-Handbook.ps1')

function Invoke-Dotnet([string[]]$arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$msbuildProperties = @("-p:VsGamePath=$VsGamePath", "-p:VsImGuiPath=$VsImGuiPath")
if ($RealSmokePath) { $msbuildProperties += "-p:RealSmokePath=$RealSmokePath" }
Invoke-Dotnet (@('build', $solution, '--configuration', $Configuration, '--nologo') + $msbuildProperties)
foreach ($testProject in $(if ($RunTests) { $testProjects } else { @() })) {
    Invoke-Dotnet @('run', '--project', $testProject, '--configuration', $Configuration, '--no-build', '--no-restore')
}
if ($RunTests) {
    Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests\Seraph.GameBridge.Tests\Seraph.GameBridge.Tests.csproj'),
        '--configuration', $Configuration, '--no-build', '--no-restore', '--', $VsGamePath, '--performance')
    Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests\Seraph.GameBridge.Tests\Seraph.GameBridge.Tests.csproj'),
        '--configuration', $Configuration, '--no-build', '--no-restore', '--', $VsGamePath, '--well-structure')
    Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests\Seraph.GameBridge.Tests\Seraph.GameBridge.Tests.csproj'),
        '--configuration', $Configuration, '--no-build', '--no-restore', '--', $VsGamePath, '--hot-water')
    foreach ($feature in @('--food', '--world-interactions', '--metrics', '--water', '--recipes', '--pollution', '--water-immersion')) {
        Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests\Seraph.GameBridge.Tests\Seraph.GameBridge.Tests.csproj'),
            '--configuration', $Configuration, '--no-build', '--no-restore', '--', $VsGamePath, $feature)
    }
    $butchering = @(Get-ChildItem -Path (Join-Path $env:APPDATA 'MVL/Modpack/测试服务器/Cache/unpack/Butchering*/Butchering.dll') -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    if ($butchering.Count) {
        Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests/Seraph.GameBridge.Tests/Seraph.GameBridge.Tests.csproj'),
            '--configuration', $Configuration, '--no-build', '--no-restore', '--', $VsGamePath, '--butchering-food', $butchering[0].FullName)
    }
    if ($RealSmokePath) {
        $scent = @(Get-ChildItem -Path (Join-Path $env:APPDATA 'MVL/Modpack/测试服务器/Cache/unpack/Smoke*/SmokeScent.dll') -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
        if ($scent.Count) {
            Invoke-Dotnet @('run', '--project', (Join-Path $root 'tests/Seraph.GameBridge.Tests/Seraph.GameBridge.Tests.csproj'),
                '--configuration', $Configuration, '--no-build', '--no-restore', '--', $VsGamePath, '--smoke-native', $RealSmokePath, $scent[0].FullName)
        }
    }
}

$mainMetadata = Get-Content -LiteralPath (Join-Path $root 'modinfo.json') -Raw | ConvertFrom-Json
$debugMetadata = Get-Content -LiteralPath (Join-Path $root 'src\Seraph.DebugUI\modinfo.json') -Raw | ConvertFrom-Json
$mainId = $mainMetadata.modid
$debugId = $debugMetadata.modid
$version = $mainMetadata.version
$debugVersion = $debugMetadata.version
if ($mainId -cnotmatch '^[a-z][a-z0-9]*$' -or $debugId -cnotmatch '^[a-z][a-z0-9]*$') { throw 'Invalid package mod ID.' }
if ($debugMetadata.dependencies.$mainId -ne $version) { throw 'Debug package dependency does not match the main mod.' }
if ($version -ne $debugVersion) { throw "Main and debug mod versions differ: $version vs $debugVersion" }
$assemblyVersion = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -cnotmatch '^\d+\.\d+\.\d+$' -or $version -cne $assemblyVersion) { throw 'Package and assembly versions must match.' }

$buildRoot = [IO.Path]::GetTempPath()
$stageRoot = Join-Path $buildRoot ("Seraph-build-{0}" -f [Guid]::NewGuid().ToString('N'))
$mainStage = Join-Path $stageRoot $mainId
$debugStage = Join-Path $stageRoot $debugId
$mainZip = Join-Path $packages "$mainId-$version.zip"
$debugZip = Join-Path $packages "$debugId-$debugVersion.zip"
$manifestPath = Join-Path $packages 'sha256-manifest.txt'
$buildManifestPath = Join-Path $packages 'build-manifest.json'

function Copy-RequiredFile([string]$source, [string]$destinationDirectory, [string]$packageName) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing $packageName input: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $destinationDirectory $packageName)
}

function Assert-ZipAllowlist([string]$archivePath, [string[]]$allowed) {
    $allowed = @($allowed | ForEach-Object { $_.Replace('/', '\') })
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('/', '\') })
    }
    finally { $archive.Dispose() }
    $unexpected = @($entries | Where-Object { $_ -notin $allowed })
    $missing = @($allowed | Where-Object { $_ -notin $entries })
    $duplicates = @($entries | Group-Object | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0 -or $duplicates.Count -gt 0) {
        throw "Package allowlist violation in $archivePath. Unexpected: $($unexpected -join ', '); Missing: $($missing -join ', '); Duplicate: $($duplicates -join ', ')"
    }
}

function Write-ReproducibleZip([string]$stageDirectory, [string]$archivePath, [string[]]$entries) {
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entryName in $entries) {
            $source = Join-Path $stageDirectory $entryName
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing staged package file: $source" }
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($source)
            $output = $entry.Open()
            try { $input.CopyTo($output) }
            finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $mainStage, $debugStage, $packages -Force | Out-Null

    Copy-RequiredFile (Join-Path $root "src\Seraph\bin\$Configuration\net10.0\vsslssmod.dll") $mainStage 'vsslssmod.dll'
    Copy-RequiredFile (Join-Path $root "src\Seraph.Application\bin\$Configuration\net10.0\vsslssmod.Application.dll") $mainStage 'vsslssmod.Application.dll'
    Copy-RequiredFile (Join-Path $root "src\Seraph.Core\bin\$Configuration\net10.0\vsslssmod.Core.dll") $mainStage 'vsslssmod.Core.dll'
    Copy-RequiredFile (Join-Path $root 'modinfo.json') $mainStage 'modinfo.json'
    Copy-RequiredFile (Join-Path $root 'LICENSE') $mainStage 'LICENSE'
    New-Item -ItemType Directory -Path (Join-Path $mainStage 'THIRD-PARTY-NOTICES') -Force | Out-Null
    # Credits are legal notices, not player documentation. Ship a text copy;
    # README and Markdown documentation remain in the source repository.
    Copy-RequiredFile (Join-Path $root 'ASSET-CREDITS.md') $mainStage 'THIRD-PARTY-NOTICES/ASSET-CREDITS.txt'
    $legalNotices = @('ProspectTogether-MIT.txt', 'Authorized-Models.txt', 'Status-Icons-MIT.txt', 'CC0-1.0.txt')
    foreach ($notice in $legalNotices) {
        Copy-RequiredFile (Join-Path $root "THIRD-PARTY-NOTICES/$notice") $mainStage "THIRD-PARTY-NOTICES/$notice"
    }

    Copy-RequiredFile (Join-Path $root "src\Seraph.DebugUI\bin\$Configuration\net10.0\vsslssmod.DebugUI.dll") $debugStage 'vsslssmod.DebugUI.dll'
    Copy-RequiredFile (Join-Path $root 'src\Seraph.DebugUI\modinfo.json') $debugStage 'modinfo.json'
    Copy-RequiredFile (Join-Path $root 'LICENSE') $debugStage 'LICENSE'

    foreach ($languagePackage in @(
        @{ Domain = 'vsslssmod'; Stage = $mainStage },
        @{ Domain = 'vsslssmoddebug'; Stage = $debugStage }
    )) {
        $relativeDirectory = 'assets/' + $languagePackage.Domain + '/lang'
        $languageStage = Join-Path $languagePackage.Stage $relativeDirectory
        New-Item -ItemType Directory -Path $languageStage -Force | Out-Null
        foreach ($locale in @('en', 'zh-cn')) {
            Copy-RequiredFile (Join-Path $root "$relativeDirectory/$locale.json") $languageStage "$locale.json"
        }
    }

    Copy-RequiredFile (Join-Path $root 'assets/vsslssmod/config/compatibility.json') (New-Item -ItemType Directory -Path (Join-Path $mainStage 'assets/vsslssmod/config') -Force).FullName 'compatibility.json'
    $bodyStage = New-Item -ItemType Directory -Path (Join-Path $mainStage 'assets/vsslssmod/textures/gui/body-player') -Force
    foreach ($bodyAsset in @('player-front-outline-white.png', 'COPYRIGHT.txt')) {
        Copy-RequiredFile (Join-Path $root "assets/vsslssmod/textures/gui/body-player/$bodyAsset") $bodyStage.FullName $bodyAsset
    }
    $mainAllowlist = @('vsslssmod.dll', 'vsslssmod.Application.dll', 'vsslssmod.Core.dll', 'modinfo.json', 'LICENSE', 'THIRD-PARTY-NOTICES/ASSET-CREDITS.txt') + @($legalNotices | ForEach-Object { "THIRD-PARTY-NOTICES/$_" }) + @(
        'assets/vsslssmod/lang/en.json', 'assets/vsslssmod/lang/zh-cn.json', 'assets/vsslssmod/config/compatibility.json',
        'assets/vsslssmod/textures/gui/body-player/player-front-outline-white.png', 'assets/vsslssmod/textures/gui/body-player/COPYRIGHT.txt')
    $debugAllowlist = @('vsslssmod.DebugUI.dll', 'modinfo.json', 'LICENSE',
        'assets/vsslssmoddebug/lang/en.json', 'assets/vsslssmoddebug/lang/zh-cn.json')
    $additionalMainFiles = @(
        'assets/vsslssmod/sounds/body/heartbeat.ogg',
        'assets/vsslssmod/config/handbook/01-start.json',
        'assets/vsslssmod/config/handbook/02-interface.json',
        'assets/vsslssmod/config/handbook/03-water.json',
        'assets/vsslssmod/config/handbook/04-filter.json',
        'assets/vsslssmod/config/handbook/05-wells.json',
        'assets/vsslssmod/config/handbook/06-distillation.json',
        'assets/vsslssmod/config/handbook/07-food.json',
        'assets/vsslssmod/config/handbook/08-stamina.json',
        'assets/vsslssmod/config/handbook/09-sleep.json',
        'assets/vsslssmod/config/handbook/10-firstaid.json',
        'assets/vsslssmod/config/handbook/11-blood.json',
        'assets/vsslssmod/config/handbook/12-disease.json',
        'assets/vsslssmod/config/handbook/13-washing.json',
        'assets/vsslssmod/config/handbook/14-sanitation.json',
        'assets/vsslssmod/config/handbook/15-thermal.json',
        'assets/vsslssmod/config/handbook/16-smoke.json',
        'assets/vsslssmod/config/handbook/17-soap.json',
        'assets/vsslssmod/config/handbook/18-troubleshooting.json',
        'assets/vsslssmod/blocktypes/drainpot.json',
        'assets/vsslssmod/blocktypes/filterpot.json',
        'assets/vsslssmod/blocktypes/watertreatment.json',
        'assets/vsslssmod/recipes/grid/drainpot.json',
        'assets/vsslssmod/recipes/grid/filterpot.json',
        'assets/vsslssmod/shapes/block/drainpot-inventory.json',
        'assets/vsslssmod/shapes/block/drainpot-pipe.json',
        'assets/vsslssmod/shapes/block/drainpot.json',
        'assets/vsslssmod/shapes/block/filterpot-inventory.json',
        'assets/vsslssmod/shapes/block/filterpot.json',
        'assets/vsslssmod/patches/player-squat.json',
        'assets/vsslssmod/patches/player-closed-eye-rest.json',
        'assets/vsslssmod/patches/realsmoke-chimney.json',
        'assets/vsslssmod/patches/expanded-matter-soap.json',
        'assets/vsslssmod/patches/water-distillation.json',
        'assets/vsslssmod/patches/food-calibration.json',
        'assets/vsslssmod/patches/player-vomit.json',
        'assets/game/lang/en.json',
        'assets/game/lang/zh-cn.json',
        'assets/vsslssmod/config/foods.json',
        'assets/vsslssmod/textures/gui/status/ch4ver/blood-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/sleep-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/defecation-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/bcomplex-deficiency-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/vitamina-deficiency-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/vitaminc-deficiency-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/bloodtoxicity-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/bug-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/soiled-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/cold.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/dehydration-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/dysentery-icon.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/hot.png',
        'assets/vsslssmod/textures/gui/status/hansjack/ketosis.png',
        'assets/vsslssmod/textures/gui/status/hansjack/bone-weakness.png',
        'assets/vsslssmod/textures/gui/status/hansjack/anemia.png',
        'assets/vsslssmod/textures/gui/status/ch4ver/COPYRIGHT.txt',
        'assets/vsslssmod/textures/gui/status/ch4ver/attribution.json',
        'assets/vsslssmod/textures/gui/status/hansjack/dark-adaptation-close.png',
        'assets/vsslssmod/textures/gui/status/hansjack/dark-adaptation-open.png',
        'assets/vsslssmod/textures/gui/status/hansjack/COPYRIGHT.txt',
        'assets/vsslssmod/textures/gui/status/hansjack/attribution.json',

        'assets/vsslssmod/itemtypes/manure.json',
        'assets/vsslssmod/itemtypes/ash.json',
        'assets/vsslssmod/itemtypes/lye.json',
        'assets/vsslssmod/itemtypes/soap.json',
        'assets/vsslssmod/itemtypes/soap-powder.json',
        'assets/vsslssmod/itemtypes/seasalt.json',
        'assets/vsslssmod/blocktypes/compostdung-1.json',
        'assets/vsslssmod/blocktypes/compostdung-2.json',
        'assets/vsslssmod/blocktypes/compostdung-3.json',
        'assets/vsslssmod/blocktypes/compostdung-4.json',
        'assets/vsslssmod/blocktypes/smallpoop-1.json',
        'assets/vsslssmod/blocktypes/smallpoop-2.json',
        'assets/vsslssmod/blocktypes/smallpoop-3.json',
        'assets/vsslssmod/blocktypes/smallpoop-4.json',
        'assets/vsslssmod/blocktypes/wellwall.json',
        'assets/vsslssmod/blocktypes/wellhead.json',
        'assets/vsslssmod/blocktypes/chimney.json',
        'assets/vsslssmod/blocktypes/chimney-middle.json',
        'assets/vsslssmod/blocktypes/chimney-top.json',
        'assets/vsslssmod/blocktypes/bathtub.json',
        'assets/vsslssmod/blocktypes/bathtub-part.json',
        'assets/vsslssmod/blocktypes/copperbathtub.json',
        'assets/vsslssmod/blocktypes/copperbathtub-part.json',
        'assets/vsslssmod/blocktypes/copperbathtub-end.json',
        'assets/vsslssmod/recipes/grid/copperbathtub.json',
        'assets/vsslssmod/blocktypes/latrine.json',
        'assets/vsslssmod/blocktypes/stonefirepit.json',
        'assets/vsslssmod/shapes/block/stonefirepit.json',
        'assets/vsslssmod/shapes/block/stonefirepit-ring.json',
        'assets/vsslssmod/shapes/block/wellwall.json',
        'assets/vsslssmod/shapes/block/wellwall-inventory.json',
        'assets/vsslssmod/shapes/block/wellhead.json',
        'assets/vsslssmod/shapes/block/wellhead-inventory.json',
        'assets/vsslssmod/shapes/block/bathtub.json',
        'assets/vsslssmod/shapes/block/bathtub-part.json',
        'assets/vsslssmod/shapes/block/bathtub-inventory.json',
        'assets/vsslssmod/shapes/block/bathtub-water.json',
        'assets/vsslssmod/shapes/block/copperbathtub.json',
        'assets/vsslssmod/shapes/block/copperbathtub-part.json',
        'assets/vsslssmod/shapes/block/copperbathtub-end.json',
        'assets/vsslssmod/shapes/block/copperbathtub-inventory.json',
        'assets/vsslssmod/shapes/block/latrine.json',
        'assets/vsslssmod/shapes/block/well-rope.json',
        'assets/vsslssmod/shapes/block/well-wall-water.json',
        'assets/vsslssmod/shapes/block/chimney.json',
        'assets/vsslssmod/shapes/block/chimney-base.json',
        'assets/vsslssmod/shapes/block/chimney-middle.json',
        'assets/vsslssmod/shapes/block/chimney-top.json',
        'assets/vsslssmod/shapes/item/ash.json',
        'assets/vsslssmod/shapes/item/soap.json',
        'assets/vsslssmod/textures/item/soap.png',
        'assets/vsslssmod/textures/item/ash.png',
        'assets/vsslssmod/textures/item/seasalt.png',
        'assets/vsslssmod/textures/block/liquid/lye.png',
        'assets/vsslssmod/recipes/grid/wellwall.json',
        'assets/vsslssmod/recipes/grid/wellhead.json',
        'assets/vsslssmod/recipes/grid/chimney.json',
        'assets/vsslssmod/recipes/grid/soap.json',
        'assets/vsslssmod/recipes/barrel/lye.json',
        'assets/vsslssmod/recipes/barrel/soap.json',
        'assets/vsslssmod/recipes/barrel/seasalt-curedmeat.json',
        'assets/vsslssmod/recipes/grid/bathtub.json',
        'assets/vsslssmod/recipes/grid/latrine.json',
        'assets/vsslssmod/textures/gui/body-player/provenance.json',
        'assets/vsslssmod/textures/gui/body-player/body-regions.json',
        'assets/vsslssmod/textures/gui/body-player/masks/front/head-neck.png',
        'assets/vsslssmod/textures/gui/body-player/masks/front/torso.png',
        'assets/vsslssmod/textures/gui/body-player/masks/front/right-arm.png',
        'assets/vsslssmod/textures/gui/body-player/masks/front/left-arm.png',
        'assets/vsslssmod/textures/gui/body-player/masks/front/right-leg.png',
        'assets/vsslssmod/textures/gui/body-player/masks/front/left-leg.png'
    )
    foreach ($relative in $additionalMainFiles) {
        $destination = Join-Path $mainStage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
    }
    $mainAllowlist += $additionalMainFiles
    foreach ($entry in @($mainAllowlist) + @($debugAllowlist)) {
        if ($entry -match '(^|/)(docs?|readme[^/]*)(/|$)' -or $entry -match '\.(md|markdown|rst|pdf|docx?)$') {
            throw "Documentation must not be included in a release package: $entry"
        }
    }
    # Model tests on source files cannot catch an omitted shipping asset.
    foreach ($shapeFile in Get-ChildItem -LiteralPath (Join-Path $root 'assets/vsslssmod/shapes/block') -Recurse -File -Filter '*.json') {
        $relativeShape = [IO.Path]::GetRelativePath($root, $shapeFile.FullName).Replace('\', '/')
        if ($relativeShape -notin $mainAllowlist) { throw "Shape missing from package allowlist: $relativeShape" }
    }
    foreach ($patchFile in Get-ChildItem -LiteralPath (Join-Path $root 'assets/vsslssmod/patches') -Recurse -File -Filter '*.json') {
        $relativePatch = [IO.Path]::GetRelativePath($root, $patchFile.FullName).Replace('\', '/')
        if ($relativePatch -notin $mainAllowlist) { throw "Patch missing from package allowlist: $relativePatch" }
    }
    foreach ($textureFolder in @('item', 'block')) {
        foreach ($textureFile in Get-ChildItem -LiteralPath (Join-Path $root "assets/vsslssmod/textures/$textureFolder") -Recurse -File -Filter '*.png') {
            $relativeTexture = [IO.Path]::GetRelativePath($root, $textureFile.FullName).Replace('\', '/')
            if ($relativeTexture -notin $mainAllowlist) { throw "Item/block texture missing from package allowlist: $relativeTexture" }
        }
    }
    Write-ReproducibleZip $mainStage $mainZip $mainAllowlist
    Write-ReproducibleZip $debugStage $debugZip $debugAllowlist

    Assert-ZipAllowlist $mainZip $mainAllowlist
    Assert-ZipAllowlist $debugZip $debugAllowlist

    $referenceRecords = foreach ($reference in $requiredInputs) {
        $item = Get-Item -LiteralPath $reference
        $fileVersion = $item.VersionInfo.FileVersion
        $productVersion = $item.VersionInfo.ProductVersion
        [ordered]@{
            path = $reference
            file = $item.Name
            length = $item.Length
            sha256 = (Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash.ToLowerInvariant()
            fileVersion = if ($null -eq $fileVersion) { '' } else { $fileVersion }
            productVersion = if ($null -eq $productVersion) { '' } else { $productVersion }
        }
    }
    $buildManifest = [ordered]@{
        modid = $mainId
        name = $mainMetadata.name
        debugModid = $debugId
        packageVersion = $version
        configuration = $Configuration
        targetFramework = 'net10.0'
        gameReferenceRoot = $VsGamePath
        imguiReferenceRoot = $VsImGuiPath
        references = @($referenceRecords)
        packages = @(
            [ordered]@{ file = [IO.Path]::GetFileName($mainZip); sha256 = (Get-FileHash -LiteralPath $mainZip -Algorithm SHA256).Hash.ToLowerInvariant() },
            [ordered]@{ file = [IO.Path]::GetFileName($debugZip); sha256 = (Get-FileHash -LiteralPath $debugZip -Algorithm SHA256).Hash.ToLowerInvariant() }
        )
    }
    $buildManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $buildManifestPath -Encoding utf8
    @(
        "$(($buildManifest.packages[0].sha256))  $([IO.Path]::GetFileName($mainZip))",
        "$(($buildManifest.packages[1].sha256))  $([IO.Path]::GetFileName($debugZip))"
    ) | Set-Content -LiteralPath $manifestPath -Encoding ascii
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        $resolvedStage = (Resolve-Path -LiteralPath $stageRoot).Path
        $resolvedBuildRoot = (Resolve-Path -LiteralPath $buildRoot).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not $resolvedStage.StartsWith($resolvedBuildRoot + [IO.Path]::DirectorySeparatorChar + 'Seraph-build-', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove staging path outside the temporary directory: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

Write-Host "Built and verified: $mainZip"
Write-Host "Built and verified: $debugZip"
Write-Host "SHA256 manifest: $manifestPath"
Write-Host "Build manifest: $buildManifestPath"
if (-not $NoDeploy) { & (Join-Path $root 'scripts/Deploy-Local.ps1') }

