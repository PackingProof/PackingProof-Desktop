param(
    [string]$SourcePath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "VisionModelBaseline.Common.ps1")

$manifestPath = Join-Path $PSScriptRoot "vision-model-baseline.json"
$baseline = Read-VisionModelBaselineManifest -ManifestPath $manifestPath
$cacheDirectory = Join-Path $repoRoot "package\dependency-cache\vision-models"
$destinationPath = Join-Path $cacheDirectory ([string]$baseline.file_name)
$resolvedPath = Resolve-VisionModelBaseline `
    -Baseline $baseline `
    -CacheDirectory $cacheDirectory `
    -DestinationPath $destinationPath `
    -SourcePath $SourcePath

Write-Host "Pinned vision model prepared: $resolvedPath"
Write-Host "Model: $($baseline.id), training run: $($baseline.training_run), SHA256: $($baseline.sha256)"
