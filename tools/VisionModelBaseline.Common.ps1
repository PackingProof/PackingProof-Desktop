Set-StrictMode -Version Latest

function Get-VisionModelSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-VisionModelBaselineManifest {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "视觉模型锁定清单不存在：$ManifestPath"
    }

    try {
        $baseline = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "视觉模型锁定清单读取失败：$ManifestPath（$($_.Exception.Message)）"
    }

    if ([int]$baseline.schema_version -ne 1) {
        throw "视觉模型锁定清单 schema_version 必须为 1：$ManifestPath"
    }
    foreach ($property in @("id", "version", "training_run", "file_name", "installed_name", "sha256")) {
        $value = [string]$baseline.$property
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "视觉模型锁定清单缺少 $property：$ManifestPath"
        }
    }
    if ([long]$baseline.size -le 0) {
        throw "视觉模型锁定清单的 size 必须大于 0：$ManifestPath"
    }
    if ([string]$baseline.sha256 -notmatch "^[0-9a-fA-F]{64}$") {
        throw "视觉模型锁定清单的 sha256 格式无效：$ManifestPath"
    }
    if ([string]$baseline.file_name -ne [System.IO.Path]::GetFileName([string]$baseline.file_name) -or
        [string]$baseline.installed_name -ne [System.IO.Path]::GetFileName([string]$baseline.installed_name)) {
        throw "视觉模型文件名只能是 leaf name：$ManifestPath"
    }
    if ([string]::IsNullOrWhiteSpace([string]$baseline.source.release_tag) -or
        @($baseline.source.urls).Count -eq 0) {
        throw "视觉模型锁定清单缺少 source.release_tag 或 source.urls：$ManifestPath"
    }
    foreach ($url in @($baseline.source.urls)) {
        if (-not [System.Uri]::IsWellFormedUriString([string]$url, [System.UriKind]::Absolute) -or
            -not ([string]$url).StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "视觉模型下载地址必须是 HTTPS：$url"
        }
    }

    return $baseline
}

function Assert-VisionModelFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Baseline,
        [string]$Description = "视觉模型"
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description 不存在：$Path"
    }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne [long]$Baseline.size) {
        throw "$Description 大小不匹配：期望 $([long]$Baseline.size)，实际 $($file.Length)：$Path"
    }
    $actualSha256 = Get-VisionModelSha256 -Path $Path
    if (-not [string]::Equals(
            $actualSha256,
            [string]$Baseline.sha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description SHA256 不匹配：期望 $($Baseline.sha256)，实际 $actualSha256：$Path"
    }
}

function Resolve-VisionModelBaseline {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$CacheDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [string]$SourcePath = ""
    )

    $cachePath = Join-Path $CacheDirectory ([string]$Baseline.file_name)
    $knownCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
        $knownCandidates += [System.IO.Path]::GetFullPath($SourcePath)
    }
    $knownCandidates += $cachePath

    $resolvedSource = $null
    foreach ($candidate in $knownCandidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        Assert-VisionModelFile `
            -Path $candidate `
            -Baseline $Baseline `
            -Description "候选视觉模型"
        $resolvedSource = $candidate
        break
    }

    New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
    if ($null -eq $resolvedSource) {
        $temporaryPath = Join-Path $CacheDirectory (
            ".vision-model-download-" + [Guid]::NewGuid().ToString("N") + ".tmp")
        try {
            $downloaded = $false
            $errors = @()
            foreach ($url in @($Baseline.source.urls)) {
                try {
                    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
                    Write-Host "Downloading locked vision model: $url"
                    Invoke-WebRequest `
                        -Uri ([string]$url) `
                        -OutFile $temporaryPath `
                        -UseBasicParsing `
                        -TimeoutSec 120 `
                        -MaximumRedirection 5
                    Assert-VisionModelFile `
                        -Path $temporaryPath `
                        -Baseline $Baseline `
                        -Description "下载的视觉模型"
                    $downloaded = $true
                    break
                }
                catch {
                    $errors += "$url -> $($_.Exception.Message)"
                }
            }
            if (-not $downloaded) {
                throw (
                    "锁定视觉模型下载失败：" + [Environment]::NewLine +
                    ($errors -join [Environment]::NewLine))
            }
            if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
                Remove-Item -LiteralPath $cachePath -Force
            }
            Move-Item -LiteralPath $temporaryPath -Destination $cachePath
            $resolvedSource = $cachePath
        }
        finally {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
    elseif (-not [string]::Equals(
            [System.IO.Path]::GetFullPath($resolvedSource),
            [System.IO.Path]::GetFullPath($cachePath),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $resolvedSource -Destination $cachePath -Force
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
    if (-not [string]::Equals(
            [System.IO.Path]::GetFullPath($cachePath),
            [System.IO.Path]::GetFullPath($DestinationPath),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $cachePath -Destination $DestinationPath -Force
    }
    Assert-VisionModelFile -Path $DestinationPath -Baseline $Baseline
    return $DestinationPath
}

function Assert-VisionModelRuntimeFiles {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir,
        [Parameter(Mandatory = $true)]$Baseline
    )

    Assert-VisionModelFile `
        -Path (Join-Path $AppDir ("models\" + [string]$Baseline.installed_name)) `
        -Baseline $Baseline `
        -Description "发布包视觉模型"
    foreach ($runtimeFile in @("onnxruntime.dll", "DirectML.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $AppDir $runtimeFile) -PathType Leaf)) {
            throw "发布包缺少视觉推理运行时：$runtimeFile"
        }
    }
}
