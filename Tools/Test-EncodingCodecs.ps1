param(
    [string]$Configuration = "Release",
    [string]$ArtifactsPath = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "ExpressPackingMonitoring.EncodingIntegrationTests\ExpressPackingMonitoring.EncodingIntegrationTests.csproj"
$baselineManifest = Join-Path $repoRoot "Tools\ffmpeg-baseline.json"
$baselineCommon = Join-Path $repoRoot "Tools\FFmpegBaseline.Common.ps1"
. $baselineCommon

if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "Encoder integration test project not found: $testProject"
}

$baseline = Read-FFmpegBaselineManifest -ManifestPath $baselineManifest
$cacheDirectory = Join-Path $repoRoot "package\dependency-cache\ffmpeg\$($baseline.version)"
$ffmpegPath = Join-Path $cacheDirectory "ffmpeg.exe"
try {
    Assert-FFmpegExecutable -ExecutablePath $ffmpegPath -Baseline $baseline
}
catch {
    $sevenZipCandidates = @(
        $env:SEVEN_ZIP_EXE,
        (Get-Command 7z.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $sevenZip = $sevenZipCandidates | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($sevenZip)) {
        throw "7-Zip is required to prepare the pinned FFmpeg dependency."
    }

    $dependencyOutput = Join-Path $repoRoot "TestResults\EncoderDependencies\$($baseline.version)\ffmpeg.exe"
    Resolve-FFmpegBaselineExecutable `
        -Baseline $baseline `
        -CacheDirectory $cacheDirectory `
        -DestinationPath $dependencyOutput `
        -SevenZipExecutable $sevenZip
    $ffmpegPath = $dependencyOutput
}

$adapters = @()
try {
    $adapters = @(Get-CimInstance Win32_VideoController | Where-Object {
        [string]$_.PNPDeviceID -like "PCI\*"
    })
}
catch {
    throw "Unable to detect physical video controllers: $($_.Exception.Message)"
}

$requiredEncoders = [System.Collections.Generic.List[string]]::new()
$optionalEncoders = [System.Collections.Generic.List[string]]::new()
function Add-UniqueEncoder {
    param(
        [System.Collections.Generic.List[string]]$List,
        [string]$Encoder
    )
    if (-not $List.Contains($Encoder)) {
        $List.Add($Encoder)
    }
}

Add-UniqueEncoder -List $requiredEncoders -Encoder "libx264"
Add-UniqueEncoder -List $requiredEncoders -Encoder "libx265"

$hasNvidia = @($adapters | Where-Object {
    [string]$_.PNPDeviceID -match "VEN_10DE" -or [string]$_.Name -match "NVIDIA"
}).Count -gt 0
$hasAmd = @($adapters | Where-Object {
    [string]$_.PNPDeviceID -match "VEN_1002" -or [string]$_.Name -match "AMD|Radeon"
}).Count -gt 0
$hasIntel = @($adapters | Where-Object {
    [string]$_.PNPDeviceID -match "VEN_8086" -or [string]$_.Name -match "Intel"
}).Count -gt 0

if ($hasNvidia) {
    Add-UniqueEncoder -List $requiredEncoders -Encoder "h264_nvenc"
    Add-UniqueEncoder -List $requiredEncoders -Encoder "hevc_nvenc"
    Add-UniqueEncoder -List $optionalEncoders -Encoder "av1_nvenc"
}
if ($hasAmd) {
    Add-UniqueEncoder -List $requiredEncoders -Encoder "h264_amf"
    Add-UniqueEncoder -List $requiredEncoders -Encoder "hevc_amf"
    Add-UniqueEncoder -List $optionalEncoders -Encoder "av1_amf"
}
if ($hasIntel) {
    Add-UniqueEncoder -List $requiredEncoders -Encoder "h264_qsv"
    Add-UniqueEncoder -List $requiredEncoders -Encoder "hevc_qsv"
    Add-UniqueEncoder -List $optionalEncoders -Encoder "av1_qsv"
}

$adapterNames = @($adapters | ForEach-Object { $_.Name })
Write-Host "Physical video controllers: $($adapterNames -join '; ')"
Write-Host "Required encoder round trips: $($requiredEncoders -join ', ')"
Write-Host "Hardware-conditional AV1 round trips: $($optionalEncoders -join ', ')"

if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $repoRoot "TestResults\EncoderRoundTrip\$Configuration"
}

$oldFfmpegPath = [Environment]::GetEnvironmentVariable("EPM_FFMPEG_PATH", "Process")
$oldRequiredEncoders = [Environment]::GetEnvironmentVariable("EPM_REQUIRED_ENCODERS", "Process")
$oldOptionalEncoders = [Environment]::GetEnvironmentVariable("EPM_OPTIONAL_ENCODERS", "Process")
try {
    [Environment]::SetEnvironmentVariable("EPM_FFMPEG_PATH", $ffmpegPath, "Process")
    [Environment]::SetEnvironmentVariable("EPM_REQUIRED_ENCODERS", ($requiredEncoders -join ','), "Process")
    [Environment]::SetEnvironmentVariable("EPM_OPTIONAL_ENCODERS", ($optionalEncoders -join ','), "Process")

    $arguments = @(
        "test",
        $testProject,
        "-c", $Configuration,
        "--nologo",
        "--artifacts-path", $ArtifactsPath
    )
    if ($NoBuild) {
        $arguments += @("--no-build", "--no-restore")
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Encoder round-trip tests failed with exit code $LASTEXITCODE"
    }
}
finally {
    [Environment]::SetEnvironmentVariable("EPM_FFMPEG_PATH", $oldFfmpegPath, "Process")
    [Environment]::SetEnvironmentVariable("EPM_REQUIRED_ENCODERS", $oldRequiredEncoders, "Process")
    [Environment]::SetEnvironmentVariable("EPM_OPTIONAL_ENCODERS", $oldOptionalEncoders, "Process")
}
