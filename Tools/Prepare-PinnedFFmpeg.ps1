param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "FFmpegBaseline.Common.ps1")

$baseline = Read-FFmpegBaselineManifest -ManifestPath (Join-Path $PSScriptRoot "ffmpeg-baseline.json")
$cacheDirectory = Join-Path $repoRoot "package\dependency-cache\ffmpeg\$($baseline.version)"
$destinationPath = Join-Path $cacheDirectory "ffmpeg.exe"

if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
    try {
        Assert-FFmpegExecutable -ExecutablePath $destinationPath -Baseline $baseline
        Write-Host "Pinned FFmpeg already prepared: $destinationPath"
        return
    }
    catch {
        Remove-Item -LiteralPath $destinationPath -Force
    }
}

$sevenZipCandidates = @(
    $env:SEVEN_ZIP_EXE,
    (Get-Command 7z.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
$sevenZip = $sevenZipCandidates | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($sevenZip)) {
    throw "7-Zip 未找到，无法准备钉死的 FFmpeg 依赖"
}

$dependencyOutput = Join-Path $repoRoot "TestResults\PinnedDependencies\ffmpeg.exe"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dependencyOutput) | Out-Null
Resolve-FFmpegBaselineExecutable `
    -Baseline $baseline `
    -CacheDirectory $cacheDirectory `
    -DestinationPath $dependencyOutput `
    -SevenZipExecutable $sevenZip
Copy-Item -LiteralPath $dependencyOutput -Destination $destinationPath -Force
Assert-FFmpegExecutable -ExecutablePath $destinationPath -Baseline $baseline
Write-Host "Pinned FFmpeg prepared: $destinationPath"
