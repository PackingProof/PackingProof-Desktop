$script:LauncherBaselineSchemaVersion = 1
$script:LauncherUpdateProtocolVersion = 1
$script:LauncherFingerprintFiles = @(
    "ExpressPackingMonitoring.Launcher\Program.cs",
    "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj",
    "ExpressPackingMonitoring.UpdateCore\ExpressPackingMonitoring.UpdateCore.csproj",
    "ExpressPackingMonitoring.UpdateCore\UpdateEndpointPolicy.cs",
    "ExpressPackingMonitoring.UpdateCore\UpdateMetadataClient.cs",
    "ExpressPackingMonitoring.UpdateCore\PackageDownloadRoutePolicy.cs",
    "ExpressPackingMonitoring\app.ico",
    "Tools\Install-LauncherPatch.cmd",
    "Tools\Apply-LauncherPatch.ps1"
)
$script:LauncherPackageEntries = @(
    "ExpressPackingMonitoring.exe",
    "双击更新启动器.cmd",
    "apply_launcher_patch.ps1",
    "launcher_patch_manifest.json",
    "启动器更新说明.txt"
)

function Get-LauncherLogicalFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$UpdateCheckUrl
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($relativePath in $script:LauncherFingerprintFiles) {
            $fullPath = Join-Path $RepositoryRoot $relativePath
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                throw "Launcher fingerprint file missing: $relativePath"
            }

            Add-LauncherFingerprintValue -Hasher $sha -Value $relativePath.Replace('\', '/')
            $extension = [System.IO.Path]::GetExtension($fullPath)
            $content = if ($extension -in @(".cs", ".csproj", ".cmd", ".ps1")) {
                $normalizedText = [System.IO.File]::ReadAllText($fullPath, [System.Text.Encoding]::UTF8).
                    Replace("`r`n", "`n").
                    Replace("`r", "`n")
                [System.Text.Encoding]::UTF8.GetBytes($normalizedText)
            }
            else {
                [System.IO.File]::ReadAllBytes($fullPath)
            }
            $sha.TransformBlock($content, 0, $content.Length, $null, 0) | Out-Null
            Add-LauncherFingerprintSeparator -Hasher $sha
        }

        Add-LauncherFingerprintValue -Hasher $sha -Value "runtime=$($Runtime.Trim().ToLowerInvariant())"
        Add-LauncherFingerprintValue -Hasher $sha -Value "update_check_url=$($UpdateCheckUrl.Trim())"
        Add-LauncherFingerprintValue -Hasher $sha -Value "protocol=$script:LauncherUpdateProtocolVersion"
        $empty = [byte[]]::new(0)
        $sha.TransformFinalBlock($empty, 0, 0) | Out-Null
        return [System.BitConverter]::ToString($sha.Hash).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Add-LauncherFingerprintValue {
    param(
        [Parameter(Mandatory = $true)]$Hasher,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $Hasher.TransformBlock($bytes, 0, $bytes.Length, $null, 0) | Out-Null
    Add-LauncherFingerprintSeparator -Hasher $Hasher
}

function Add-LauncherFingerprintSeparator {
    param([Parameter(Mandatory = $true)]$Hasher)

    $separator = [byte[]](0)
    $Hasher.TransformBlock($separator, 0, $separator.Length, $null, 0) | Out-Null
}

function Copy-NormalizedCommandFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $text = [System.IO.File]::ReadAllText($SourcePath, [System.Text.Encoding]::UTF8)
    if ($null -ne ($text.ToCharArray() | Where-Object { [int]$_ -gt 127 } | Select-Object -First 1)) {
        throw "Command wrapper must contain ASCII characters only: $SourcePath"
    }
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText($DestinationPath, $normalized, [System.Text.Encoding]::ASCII)
}

function Read-LauncherBaselineManifest {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Launcher baseline manifest not found: $ManifestPath"
    }

    try {
        $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $ManifestPath | ConvertFrom-Json
    }
    catch {
        throw "Launcher baseline manifest is invalid JSON: $ManifestPath"
    }

    Assert-LauncherBaselineManifest -Manifest $manifest
    return $manifest
}

function Assert-LauncherBaselineManifest {
    param([Parameter(Mandatory = $true)]$Manifest)

    $version = [string]$Manifest.version
    $package = $Manifest.package
    if (([int]$Manifest.schema_version) -ne $script:LauncherBaselineSchemaVersion -or
        ([int]$Manifest.protocol_version) -ne $script:LauncherUpdateProtocolVersion -or
        [string]::IsNullOrWhiteSpace($version) -or
        ([string]$Manifest.tag) -ne "launcher-v$version" -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.release_tag) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.runtime) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.update_check_url) -or
        -not (Test-LauncherSha256 ([string]$Manifest.source_fingerprint)) -or
        $null -eq $Manifest.package -or
        [string]::IsNullOrWhiteSpace([string]$package.file) -or
        ([long]$package.size) -le 0 -or
        -not (Test-LauncherSha256 ([string]$package.sha256)) -or
        ([long]$package.executable_size) -le 0 -or
        -not (Test-LauncherSha256 ([string]$package.executable_sha256))) {
        throw "Launcher baseline manifest is incomplete or unsupported"
    }
}

function Test-LauncherSha256 {
    param([string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[0-9a-fA-F]{64}$'
}

function Assert-LauncherFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedSize,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description not found: $Path"
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $ExpectedSize) {
        throw "$Description size mismatch: $Path"
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description SHA256 mismatch: $Path"
    }
}

function Assert-LauncherPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)]$Baseline
    )

    Assert-LauncherFile `
        -Path $PackagePath `
        -ExpectedSize ([long]$Baseline.package.size) `
        -ExpectedSha256 ([string]$Baseline.package.sha256) `
        -Description "LauncherPatch"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        $normalizedEntries = @($entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($entries.Count -ne $script:LauncherPackageEntries.Count) {
            throw "LauncherPatch contains unexpected entries"
        }
        foreach ($expected in $script:LauncherPackageEntries) {
            if ($normalizedEntries -cnotcontains $expected) {
                throw "LauncherPatch is missing required entry: $expected"
            }
        }

        $launcherEntries = @($entries | Where-Object { $_.FullName.Replace('\', '/') -ceq "ExpressPackingMonitoring.exe" })
        if ($launcherEntries.Count -ne 1) {
            throw "LauncherPatch must contain exactly one launcher executable"
        }
        $launcherEntry = $launcherEntries[0]
        if ($launcherEntry.Length -ne [long]$Baseline.package.executable_size) {
            throw "LauncherPatch executable size mismatch"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Expand-LauncherBaselinePackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)]$Baseline
    )

    Assert-LauncherPackage -PackagePath $PackagePath -Baseline $Baseline
    $destinationDirectory = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $temporaryPath = Join-Path $destinationDirectory (".launcher-" + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq "ExpressPackingMonitoring.exe" })
            if ($entries.Count -ne 1) {
                throw "LauncherPatch must contain exactly one launcher executable"
            }
            $entry = $entries[0]
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $temporaryPath, $false)
        }
        finally {
            $archive.Dispose()
        }

        Assert-LauncherFile `
            -Path $temporaryPath `
            -ExpectedSize ([long]$Baseline.package.executable_size) `
            -ExpectedSha256 ([string]$Baseline.package.executable_sha256) `
            -Description "Launcher executable"
        Move-Item -LiteralPath $temporaryPath -Destination $DestinationPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-LauncherFingerprintFiles {
    return @($script:LauncherFingerprintFiles)
}

function Get-LauncherPackageEntries {
    return @($script:LauncherPackageEntries)
}
