# BetterPetitPlanet Release Publish Script
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
    @("BetterPetitPlanet.exe", "BetterPetitPlanet.dll", "BetterPetitPlanet.GameCapture.dll", "BetterPetitPlanet.HotkeyCapture.dll", "BetterPetitPlanet.WindowsInput.dll") | ForEach-Object {
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

# 2. Copy songs library
if (Test-Path $SongsSource) {
    Write-Host "Copying songs library..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path (Join-Path $ReleaseDir "data") -Force | Out-Null
    Copy-Item -Path $SongsSource -Destination $SongsDest -Recurse -Force
}

# 3. Copy OCR models
$ModelDestDir = Join-Path $ReleaseDir "Assets\Models\Ocr"
New-Item -ItemType Directory -Path $ModelDestDir -Force | Out-Null

$ModelCandidate1 = Join-Path $ProjectRoot "BetterPetitPlanet\Assets\Models\Ocr\ch_PP-OCRv3_rec_infer.onnx"
$ModelCandidate2 = Join-Path $env:USERPROFILE "miniconda3\Lib\site-packages\rapidocr_onnxruntime\models\ch_PP-OCRv3_rec_infer.onnx"

if (Test-Path $ModelCandidate1) {
    Copy-Item -Path $ModelCandidate1 -Destination (Join-Path $ModelDestDir "ch_PP-OCRv3_rec_infer.onnx") -Force
} elseif (Test-Path $ModelCandidate2) {
    Copy-Item -Path $ModelCandidate2 -Destination (Join-Path $ModelDestDir "ch_PP-OCRv3_rec_infer.onnx") -Force
}

Write-Host "==================================================" -ForegroundColor Green
Write-Host "Publish successful! Output directory: $ReleaseDir" -ForegroundColor Green
Write-Host "Direct executable: $TargetExe" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
