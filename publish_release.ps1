# BetterPetitPlanet Release Publish Script
[CmdletBinding()]
param(
    [string]$CondaPrefix = $env:CONDA_PREFIX,
    [string]$CustomOcrModelDirectory = $null
)

$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$ReleaseDir = Join-Path $ProjectRoot "release"
$ProjectPath = Join-Path $ProjectRoot "BetterPetitPlanet\BetterPetitPlanet.csproj"
$SongsSource = Join-Path $ProjectRoot "data\songs"
$SongsDest = Join-Path $ReleaseDir "data\songs"
$TargetExe = Join-Path $ReleaseDir "BetterPetitPlanet.exe"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Compiling and publishing BetterPetitPlanet (Win-x64 Release)..." -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 0. If files in release are locked by a running instance, rename them to .old
if (Test-Path $ReleaseDir) {
    @("BetterPetitPlanet.exe", "BetterPetitPlanet.dll", "BetterPetitPlanet.MusicOverlay.Gpu.dll", "BetterPetitPlanet.GameCapture.dll", "BetterPetitPlanet.HotkeyCapture.dll", "BetterPetitPlanet.WindowsInput.dll") | ForEach-Object {
        $filePath = Join-Path $ReleaseDir $PSItem
        if (Test-Path $filePath) {
            try {
                $stream = [System.IO.File]::Open($filePath, 'Open', 'Write')
                $stream.Close()
            } catch {
                $oldPath = "$filePath.old"
                Remove-Item $oldPath -Force -ErrorAction SilentlyContinue
                Move-Item $filePath $oldPath -Force -ErrorAction SilentlyContinue
                Write-Host "Renamed locked file for update: $PSItem -> $PSItem.old" -ForegroundColor Yellow
            }
        }
    }
}

# 1. Self-contained + ReadyToRun ahead-of-time machine code compilation
dotnet publish $ProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -o $ReleaseDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed with error code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 2. Copy selected songs (dao_xiang.json, one_last_kiss.json, and see_you_again.json for release)
$RecorderSongsDest = Join-Path $ReleaseDir "data\songs\recorder"
$GuitarSongsDest = Join-Path $ReleaseDir "data\songs\guitar"
if (Test-Path $SongsDest) {
    Remove-Item -Path $SongsDest -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $RecorderSongsDest -Force | Out-Null
New-Item -ItemType Directory -Path $GuitarSongsDest -Force | Out-Null

$ReleaseSongs = @("dao_xiang.json", "one_last_kiss.json", "see_you_again.json")
foreach ($songFile in $ReleaseSongs) {
    $srcRecorderSongPath = Join-Path $SongsSource "recorder\$songFile"
    if (Test-Path $srcRecorderSongPath) {
        Write-Host "Copying release recorder song: $songFile" -ForegroundColor Yellow
        Copy-Item -Path $srcRecorderSongPath -Destination (Join-Path $RecorderSongsDest $songFile) -Force
    } else {
        Write-Host "Warning: Release recorder song not found at $srcRecorderSongPath" -ForegroundColor DarkYellow
    }

    $srcGuitarSongPath = Join-Path $SongsSource "guitar\$songFile"
    if (Test-Path $srcGuitarSongPath) {
        Write-Host "Copying release guitar song: $songFile" -ForegroundColor Yellow
        Copy-Item -Path $srcGuitarSongPath -Destination (Join-Path $GuitarSongsDest $songFile) -Force
    } else {
        Write-Host "Warning: Release guitar song not found at $srcGuitarSongPath" -ForegroundColor DarkYellow
    }
}

# 2.1 Copy music skins
$SkinsSource = Join-Path $ProjectRoot "data\music-skins"
$SkinsDest = Join-Path $ReleaseDir "data\music-skins"
if (Test-Path $SkinsSource) {
    Write-Host "Copying music skins from $SkinsSource to $SkinsDest..." -ForegroundColor Yellow
    if (Test-Path $SkinsDest) {
        Remove-Item -Path $SkinsDest -Recurse -Force -ErrorAction SilentlyContinue
    }
    Copy-Item -Path $SkinsSource -Destination $SkinsDest -Recurse -Force
} else {
    Write-Host "Warning: Music skins directory not found at $SkinsSource" -ForegroundColor DarkYellow
}

# 3. Copy OCR models (recognition & detection)
$ModelDestDir = Join-Path $ReleaseDir "Assets\Models\Ocr"
New-Item -ItemType Directory -Path $ModelDestDir -Force | Out-Null

$ocrSearchDirs = @(
    (Join-Path $ProjectRoot "BetterPetitPlanet\Assets\Models\Ocr")
)
if ($CustomOcrModelDirectory -and (Test-Path $CustomOcrModelDirectory)) {
    $ocrSearchDirs += $CustomOcrModelDirectory
}
if ($CondaPrefix -and (Test-Path $CondaPrefix)) {
    $ocrSearchDirs += (Join-Path $CondaPrefix "Lib\site-packages\rapidocr_onnxruntime\models")
}
if ($env:USERPROFILE) {
    foreach ($cSub in @("miniconda3", "anaconda3", "miniforge3")) {
        $candidateConda = Join-Path $env:USERPROFILE "$cSub\Lib\site-packages\rapidocr_onnxruntime\models"
        if (Test-Path $candidateConda) {
            $ocrSearchDirs += $candidateConda
        }
    }
}

foreach ($modelName in @("ch_PP-OCRv3_rec_infer.onnx", "ch_PP-OCRv3_det_infer.onnx")) {
    $foundPath = $null
    foreach ($searchDir in $ocrSearchDirs) {
        $candidatePath = Join-Path $searchDir $modelName
        if (Test-Path $candidatePath) {
            $foundPath = $candidatePath
            break
        }
    }
    if ($foundPath) {
        Write-Host "Copying OCR model: $modelName from $foundPath" -ForegroundColor Yellow
        Copy-Item -Path $foundPath -Destination (Join-Path $ModelDestDir $modelName) -Force
    } else {
        Write-Host "Warning: OCR model $modelName not found in search paths; skipping." -ForegroundColor DarkYellow
    }
}

Write-Host "==================================================" -ForegroundColor Green
Write-Host "Publish successful! Output directory: $ReleaseDir" -ForegroundColor Green
Write-Host "Direct executable: $TargetExe" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
