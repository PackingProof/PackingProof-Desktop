Set-StrictMode -Version Latest

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-FFmpegBaselineManifest {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "FFmpeg baseline manifest not found: $ManifestPath"
    }

    $baseline = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($baseline.schema_version -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$baseline.version) -or
        -not [string]::Equals([string]$baseline.runtime, "win-x64", [StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $baseline.package -or
        [string]::IsNullOrWhiteSpace([string]$baseline.package.file) -or
        [string]::IsNullOrWhiteSpace([string]$baseline.package.entry) -or
        [long]$baseline.package.size -le 0 -or
        [long]$baseline.package.executable_size -le 0 -or
        [string]$baseline.package.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$baseline.package.executable_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        @($baseline.package.urls).Count -lt 1 -or
        @($baseline.app_patch_compatible_executables).Count -lt 1) {
        throw "FFmpeg baseline manifest is incomplete or unsupported: $ManifestPath"
    }

    foreach ($compatibleExecutable in @($baseline.app_patch_compatible_executables)) {
        if ([string]::IsNullOrWhiteSpace([string]$compatibleExecutable.version) -or
            [long]$compatibleExecutable.size -le 0 -or
            [string]$compatibleExecutable.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "FFmpeg AppPatch compatibility entry is invalid: $ManifestPath"
        }
    }

    $packageFile = [string]$baseline.package.file
    if (-not [string]::Equals($packageFile, [IO.Path]::GetFileName($packageFile), [StringComparison]::Ordinal) -or
        $packageFile.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "FFmpeg baseline package file name is unsafe: $packageFile"
    }

    foreach ($urlText in @($baseline.package.urls)) {
        $url = $null
        if (-not [Uri]::TryCreate([string]$urlText, [UriKind]::Absolute, [ref]$url) -or
            -not [string]::Equals($url.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase) -or
            @('www.gyan.dev', 'github.com') -notcontains $url.Host.ToLowerInvariant()) {
            throw "FFmpeg baseline URL is not an approved HTTPS source: $urlText"
        }
    }

    return $baseline
}

function Assert-FFmpegExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)]$Baseline
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "FFmpeg executable not found: $ExecutablePath"
    }

    $file = Get-Item -LiteralPath $ExecutablePath
    if ($file.Length -ne [long]$Baseline.package.executable_size) {
        throw "FFmpeg executable size mismatch: $ExecutablePath"
    }

    $actualHash = Get-Sha256Lower -Path $ExecutablePath
    if (-not [string]::Equals(
            $actualHash,
            [string]$Baseline.package.executable_sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "FFmpeg executable SHA256 mismatch: $ExecutablePath"
    }
}

function Get-SafeArchiveEntries {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable
    )

    $listing = @(& $SevenZipExecutable l -slt -- $PackagePath)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect FFmpeg package: $PackagePath"
    }

    $entries = New-Object System.Collections.Generic.List[string]
    $inEntries = $false
    foreach ($line in $listing) {
        if (-not $inEntries) {
            if ($line -eq "----------") { $inEntries = $true }
            continue
        }
        if ($line -match '^Path = (.+)$') {
            $entry = $Matches[1].Replace('/', '\')
            if ([IO.Path]::IsPathRooted($entry) -or
                $entry.Split('\', [StringSplitOptions]::RemoveEmptyEntries) -contains '..') {
                throw "FFmpeg package contains an unsafe path: $entry"
            }
            $entries.Add($entry)
        }
    }

    if ($entries.Count -eq 0) {
        throw "FFmpeg package does not contain any entries: $PackagePath"
    }
    return $entries.ToArray()
}

function Assert-FFmpegPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "FFmpeg package not found: $PackagePath"
    }
    $package = Get-Item -LiteralPath $PackagePath
    if ($package.Length -ne [long]$Baseline.package.size) {
        throw "FFmpeg package size mismatch: $PackagePath"
    }
    $actualHash = Get-Sha256Lower -Path $PackagePath
    if (-not [string]::Equals(
            $actualHash,
            [string]$Baseline.package.sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "FFmpeg package SHA256 mismatch: $PackagePath"
    }

    $entries = @(Get-SafeArchiveEntries -PackagePath $PackagePath -SevenZipExecutable $SevenZipExecutable)
    $expectedEntry = ([string]$Baseline.package.entry).Replace('/', '\')
    $matches = @($entries | Where-Object {
        [string]::Equals($_, $expectedEntry, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) {
        throw "FFmpeg package must contain exactly one pinned executable entry: $expectedEntry"
    }
}

function Expand-FFmpegBaselinePackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable
    )

    Assert-FFmpegPackage -PackagePath $PackagePath -Baseline $Baseline -SevenZipExecutable $SevenZipExecutable
    $destinationFullPath = [IO.Path]::GetFullPath($DestinationPath)
    $destinationDirectory = Split-Path -Parent $destinationFullPath
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $temporaryDirectory = Join-Path $destinationDirectory ('.ffmpeg-extract-' + [Guid]::NewGuid().ToString('N'))
    $temporaryDestination = "$destinationFullPath.tmp"
    New-Item -ItemType Directory -Force -Path $temporaryDirectory | Out-Null
    try {
        $entry = [string]$Baseline.package.entry
        & $SevenZipExecutable e -y -bso0 -bsp0 "-o$temporaryDirectory" -- $PackagePath $entry
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to extract FFmpeg from package: $PackagePath"
        }
        $extractedFiles = @(Get-ChildItem -LiteralPath $temporaryDirectory -File)
        if ($extractedFiles.Count -ne 1 -or
            -not [string]::Equals($extractedFiles[0].Name, 'ffmpeg.exe', [StringComparison]::OrdinalIgnoreCase)) {
            throw "FFmpeg extraction produced unexpected files"
        }
        Assert-FFmpegExecutable -ExecutablePath $extractedFiles[0].FullName -Baseline $Baseline

        Copy-Item -LiteralPath $extractedFiles[0].FullName -Destination $temporaryDestination -Force
        Assert-FFmpegExecutable -ExecutablePath $temporaryDestination -Baseline $Baseline
        Move-Item -LiteralPath $temporaryDestination -Destination $destinationFullPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDestination) {
            Remove-Item -LiteralPath $temporaryDestination -Force
        }
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}

function Resolve-FFmpegBaselineExecutable {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$CacheDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable,
        [scriptblock]$DownloadFile,
        [int]$MaxAttemptsPerUrl = 3,
        [int]$RetryDelaySeconds = 2
    )

    $cacheFullPath = [IO.Path]::GetFullPath($CacheDirectory)
    New-Item -ItemType Directory -Force -Path $cacheFullPath | Out-Null
    $cachedExecutable = Join-Path $cacheFullPath 'ffmpeg.exe'
    try {
        Assert-FFmpegExecutable -ExecutablePath $cachedExecutable -Baseline $Baseline
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
        Copy-Item -LiteralPath $cachedExecutable -Destination $DestinationPath -Force
        Assert-FFmpegExecutable -ExecutablePath $DestinationPath -Baseline $Baseline
        Write-Host "Pinned FFmpeg reused from verified cache: $cachedExecutable"
        return
    }
    catch {
        if (Test-Path -LiteralPath $cachedExecutable -PathType Leaf) {
            Remove-Item -LiteralPath $cachedExecutable -Force
        }
    }

    $cachedPackage = Join-Path $cacheFullPath ([string]$Baseline.package.file)
    $packageReady = $false
    try {
        Assert-FFmpegPackage -PackagePath $cachedPackage -Baseline $Baseline -SevenZipExecutable $SevenZipExecutable
        $packageReady = $true
    }
    catch {
        if (Test-Path -LiteralPath $cachedPackage -PathType Leaf) {
            Remove-Item -LiteralPath $cachedPackage -Force
        }
    }

    if (-not $packageReady) {
        if ($null -eq $DownloadFile) {
            $DownloadFile = { param($Url, $Path) Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing }
        }
        $lastError = $null
        foreach ($url in @($Baseline.package.urls)) {
            $attempt = 0
            while ($attempt -lt $MaxAttemptsPerUrl) {
                $attempt++
                $temporaryPackage = "$cachedPackage.download"
                try {
                    if (Test-Path -LiteralPath $temporaryPackage) {
                        Remove-Item -LiteralPath $temporaryPackage -Force
                    }
                    & $DownloadFile ([string]$url) $temporaryPackage
                    Assert-FFmpegPackage -PackagePath $temporaryPackage -Baseline $Baseline -SevenZipExecutable $SevenZipExecutable
                    Move-Item -LiteralPath $temporaryPackage -Destination $cachedPackage -Force
                    $packageReady = $true
                    break
                }
                catch {
                    $lastError = $_
                    if (Test-Path -LiteralPath $temporaryPackage) {
                        Remove-Item -LiteralPath $temporaryPackage -Force
                    }
                    if ($attempt -lt $MaxAttemptsPerUrl) {
                        Write-Warning "FFmpeg dependency download failed (attempt $attempt/$MaxAttemptsPerUrl), retrying: $url"
                        if ($RetryDelaySeconds -gt 0) {
                            Start-Sleep -Seconds $RetryDelaySeconds
                        }
                    }
                    else {
                        Write-Warning "FFmpeg dependency download failed after $MaxAttemptsPerUrl attempts, trying next source: $url"
                    }
                }
            }
            if ($packageReady) {
                break
            }
        }
        if (-not $packageReady) {
            throw "Unable to obtain verified FFmpeg dependency: $lastError"
        }
    }

    Expand-FFmpegBaselinePackage `
        -PackagePath $cachedPackage `
        -DestinationPath $cachedExecutable `
        -Baseline $Baseline `
        -SevenZipExecutable $SevenZipExecutable
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
    Copy-Item -LiteralPath $cachedExecutable -Destination $DestinationPath -Force
    Assert-FFmpegExecutable -ExecutablePath $DestinationPath -Baseline $Baseline
    Write-Host "Pinned FFmpeg prepared: $DestinationPath"
}
