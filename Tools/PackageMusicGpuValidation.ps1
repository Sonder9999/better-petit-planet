[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$OcrModelDirectory,
    [Parameter(Mandatory = $true)][string]$RecorderScorePath,
    [string]$ReferenceAssetsDirectory = $null,
    [switch]$NoBuild
)

# Produces a separate validation build. Never modifies an existing package or its configuration.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$sourcePrefix = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if ($destinationRoot.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    $destinationRoot.TrimEnd('\', '/') -eq $sourcePrefix.TrimEnd('\', '/')) {
    throw 'Validation packages must be outside the source worktree.'
}
if (Test-Path -LiteralPath $destinationRoot) {
    throw "Destination already exists; choose a new directory to preserve user configuration: $destinationRoot"
}

$nativeAssets = Join-Path $projectRoot 'BetterPetitPlanet\Assets\MusicOverlay\NativeDefault'
$assetManifest = Get-Content -LiteralPath (Join-Path $nativeAssets 'manifest.json') -Raw | ConvertFrom-Json
if ($assetManifest.schemaVersion -ne 1 -or -not $assetManifest.files -or $assetManifest.files.Count -lt 1) {
    throw 'Unsupported native default resource manifest.'
}
foreach ($entry in $assetManifest.files) {
    if ([System.IO.Path]::IsPathRooted($entry.path) -or $entry.path.Contains('..')) {
        throw "Invalid resource path: $($entry.path)"
    }
    $assetFile = Get-Item -LiteralPath (Join-Path $nativeAssets $entry.path)
    if ($assetFile.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $assetFile.FullName -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Native skin asset failed integrity validation: $($entry.path)"
    }
}
$models = @('ch_PP-OCRv3_rec_infer.onnx', 'ch_PP-OCRv3_det_infer.onnx')
foreach ($model in $models) { Get-Item -LiteralPath (Join-Path $OcrModelDirectory $model) | Out-Null }
Get-Content -LiteralPath $RecorderScorePath -Raw -Encoding utf8 | ConvertFrom-Json | Out-Null

$project = Join-Path $projectRoot 'BetterPetitPlanet\BetterPetitPlanet.csproj'
if (!$NoBuild) {
    & dotnet build $project -c Debug -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
}
& dotnet publish $project -c Debug -p:Platform=x64 --no-build --no-restore -o $destinationRoot --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }

$modelDestination = Join-Path $destinationRoot 'Assets\Models\Ocr'
$scoreDestination = Join-Path $destinationRoot 'data\songs\recorder'
New-Item -ItemType Directory -Path $modelDestination, $scoreDestination -Force | Out-Null
foreach ($model in $models) {
    Copy-Item -LiteralPath (Join-Path $OcrModelDirectory $model) -Destination (Join-Path $modelDestination $model)
}
Copy-Item -LiteralPath $RecorderScorePath -Destination (Join-Path $scoreDestination 'dao_xiang.json')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\technical\gpu-music-overlay.md') -Destination (Join-Path $destinationRoot 'README-GPU.md')
if ($ReferenceAssetsDirectory -and (Test-Path -LiteralPath $ReferenceAssetsDirectory)) {
    $refDestination = Join-Path $destinationRoot 'Assets\MusicOverlay\Reference'
    New-Item -ItemType Directory -Path $refDestination -Force | Out-Null
    $refAssetsSource = Join-Path $ReferenceAssetsDirectory 'assets'
    $refFramesSource = Join-Path $ReferenceAssetsDirectory 'frames'
    if (Test-Path -LiteralPath $refAssetsSource) {
        Copy-Item -LiteralPath $refAssetsSource -Destination $refDestination -Recurse -Force
    }
    if (Test-Path -LiteralPath $refFramesSource) {
        Copy-Item -LiteralPath $refFramesSource -Destination $refDestination -Recurse -Force
    }
    Write-Output "Injected reference skin assets from $ReferenceAssetsDirectory into package."
}
if (Test-Path -LiteralPath (Join-Path $destinationRoot 'config\config.json')) {
    throw 'A validation package must not contain an existing user configuration.'
}

$required = @(
    'BetterPetitPlanet.exe', 'BetterPetitPlanet.dll', 'BetterPetitPlanet.MusicOverlay.Gpu.dll',
    'BetterPetitPlanet.deps.json', 'BetterPetitPlanet.runtimeconfig.json',
    'Vortice.Direct3D11.dll', 'Vortice.Direct2D1.dll', 'Vortice.DirectComposition.dll', 'Vortice.DXGI.dll',
    'SharpGen.Runtime.dll', 'SharpGen.Runtime.COM.dll', 'onnxruntime.dll', 'DirectML.dll',
    'runtimes/win-x64/native/OpenCvSharpExtern.dll', 'Assets/Instruments/Recorder/template.png',
    'Assets/MusicOverlay/NativeDefault/manifest.json', 'data/songs/recorder/dao_xiang.json', 'README-GPU.md'
) + @($assetManifest.files | ForEach-Object { 'Assets/MusicOverlay/NativeDefault/' + $_.path }) +
    @($models | ForEach-Object { 'Assets/Models/Ocr/' + $_ }) +
    @(Get-ChildItem -LiteralPath (Join-Path $destinationRoot 'Assets/Instruments/Recorder/targets') -Filter *.png |
        ForEach-Object { 'Assets/Instruments/Recorder/targets/' + $_.Name })

$packageManifest = foreach ($relative in $required) {
    $file = Get-Item -LiteralPath (Join-Path $destinationRoot $relative)
    [pscustomobject]@{ Path = $relative; Length = $file.Length; SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
foreach ($entry in $assetManifest.files) {
    $target = Join-Path $destinationRoot ('Assets/MusicOverlay/NativeDefault/' + $entry.path)
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Published skin asset differs from canonical source: $($entry.path)"
    }
}
$packageManifest | Export-Csv -LiteralPath (Join-Path $destinationRoot 'validation-manifest.csv') -NoTypeInformation -Encoding utf8
Write-Output "Validation package ready: $destinationRoot"
Write-Output "Verified $($assetManifest.files.Count) canonical skin assets and $($packageManifest.Count) package files. WPF remains the default."
