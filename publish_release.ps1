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

# 2. Copy selected songs (dao_xiang.json, one_last_kiss.json, and see_you_again.json for release)
$RecorderSongsDest = Join-Path $ReleaseDir "data\songs\recorder"
if (Test-Path $SongsDest) {
    Remove-Item -Path $SongsDest -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $RecorderSongsDest -Force | Out-Null

$ReleaseSongs = @("dao_xiang.json", "one_last_kiss.json", "see_you_again.json")
foreach ($songFile in $ReleaseSongs) {
    $srcSongPath = Join-Path $SongsSource "recorder\$songFile"
    if (Test-Path $srcSongPath) {
        Write-Host "Copying release song: $songFile" -ForegroundColor Yellow
        Copy-Item -Path $srcSongPath -Destination (Join-Path $RecorderSongsDest $songFile) -Force
    } else {
        Write-Host "Warning: Release song not found at $srcSongPath" -ForegroundColor DarkYellow
    }
}

# 3. Copy OCR models (recognition & detection)
$ModelDestDir = Join-Path $ReleaseDir "Assets\Models\Ocr"
New-Item -ItemType Directory -Path $ModelDestDir -Force | Out-Null

$RecCandidate1 = Join-Path $ProjectRoot "BetterPetitPlanet\Assets\Models\Ocr\ch_PP-OCRv3_rec_infer.onnx"
$RecCandidate2 = Join-Path $env:USERPROFILE "miniconda3\Lib\site-packages\rapidocr_onnxruntime\models\ch_PP-OCRv3_rec_infer.onnx"

if (Test-Path $RecCandidate1) {
    Copy-Item -Path $RecCandidate1 -Destination (Join-Path $ModelDestDir "ch_PP-OCRv3_rec_infer.onnx") -Force
} elseif (Test-Path $RecCandidate2) {
    Copy-Item -Path $RecCandidate2 -Destination (Join-Path $ModelDestDir "ch_PP-OCRv3_rec_infer.onnx") -Force
}

$DetCandidate1 = Join-Path $ProjectRoot "BetterPetitPlanet\Assets\Models\Ocr\ch_PP-OCRv3_det_infer.onnx"
$DetCandidate2 = Join-Path $env:USERPROFILE "miniconda3\Lib\site-packages\rapidocr_onnxruntime\models\ch_PP-OCRv3_det_infer.onnx"

if (Test-Path $DetCandidate1) {
    Copy-Item -Path $DetCandidate1 -Destination (Join-Path $ModelDestDir "ch_PP-OCRv3_det_infer.onnx") -Force
} elseif (Test-Path $DetCandidate2) {
    Copy-Item -Path $DetCandidate2 -Destination (Join-Path $ModelDestDir "ch_PP-OCRv3_det_infer.onnx") -Force
}

Write-Host "==================================================" -ForegroundColor Green
Write-Host "Publish successful! Output directory: $ReleaseDir" -ForegroundColor Green
Write-Host "Direct executable: $TargetExe" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
