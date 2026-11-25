Set-StrictMode -Version Latest

function Get-OpenCvSha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-OpenCvBaselineManifest {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "OpenCV baseline manifest not found: $ManifestPath"
    }

    $baseline = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $modules = @($baseline.modules)
    if ($baseline.schema_version -ne 1 -or
        $null -eq $baseline.PSObject.Properties['store_channel_allowed'] -or
        [string]::IsNullOrWhiteSpace([string]$baseline.version) -or
        [string]::IsNullOrWhiteSpace([string]$baseline.artifact_id) -or
        -not [string]::Equals([string]$baseline.runtime, "win-x64", [StringComparison]::OrdinalIgnoreCase) -or
        $modules.Count -ne 2 -or
        $modules[0] -ne "core" -or
        $modules[1] -ne "imgproc" -or
        [long]$baseline.native.size -le 0 -or
        [string]$baseline.native.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $null -eq $baseline.PSObject.Properties['app_patch_excluded'] -or
        $baseline.app_patch_excluded -isnot [bool] -or
        [string]::IsNullOrWhiteSpace([string]$baseline.toolchain.msvc_version) -or
        [string]::IsNullOrWhiteSpace([string]$baseline.toolchain.windows_sdk_version) -or
        @($baseline.toolchain.linker_flags) -notcontains "/Brepro" -or
        [long]$baseline.opencvsharp.archive_size -le 0 -or
        [string]$baseline.opencvsharp.archive_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "OpenCV baseline manifest is incomplete or unsupported: $ManifestPath"
    }

    return $baseline
}

function Assert-OpenCvNativeDll {
    param(
        [Parameter(Mandatory = $true)][string]$DllPath,
        [Parameter(Mandatory = $true)]$Baseline
    )

    if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
        throw "OpenCV native DLL not found: $DllPath"
    }

    $file = Get-Item -LiteralPath $DllPath
    if ($file.Length -ne [long]$Baseline.native.size -or
        -not [string]::Equals(
            (Get-OpenCvSha256Lower -Path $DllPath),
            [string]$Baseline.native.sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "OpenCV native DLL size/SHA256 mismatch: $DllPath"
    }
}

function Resolve-OpenCvBaselineNativeDll {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$CacheDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$BuilderPath,
        [string]$SourcePath = ""
    )

    $cacheFullPath = [IO.Path]::GetFullPath($CacheDirectory)
    New-Item -ItemType Directory -Force -Path $cacheFullPath | Out-Null
    $cachedDll = Join-Path $cacheFullPath ([string]$Baseline.native.file)

    if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
        Assert-OpenCvNativeDll -DllPath $SourcePath -Baseline $Baseline
        if (-not [string]::Equals(
                [IO.Path]::GetFullPath($SourcePath),
                [IO.Path]::GetFullPath($cachedDll),
                [StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $SourcePath -Destination $cachedDll -Force
        }
    }
    else {
        $cacheValid = $false
        try {
            Assert-OpenCvNativeDll -DllPath $cachedDll -Baseline $Baseline
            $cacheValid = $true
        }
        catch {
            $cacheValid = $false
        }

        if (-not $cacheValid) {
            if (-not (Test-Path -LiteralPath $BuilderPath -PathType Leaf)) {
                throw "OpenCV minimal build script not found: $BuilderPath"
            }
            & pwsh -NoProfile -File $BuilderPath -RepositoryRoot $RepositoryRoot -OutputPath $cachedDll
            if ($LASTEXITCODE -ne 0) {
                throw "OpenCV minimal build failed with exit code $LASTEXITCODE"
            }
            Assert-OpenCvNativeDll -DllPath $cachedDll -Baseline $Baseline
        }
    }

    Assert-OpenCvNativeDll -DllPath $cachedDll -Baseline $Baseline
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
    Copy-Item -LiteralPath $cachedDll -Destination $DestinationPath -Force
    Assert-OpenCvNativeDll -DllPath $DestinationPath -Baseline $Baseline
    Write-Host "Pinned OpenCV native DLL prepared: $DestinationPath"
}
