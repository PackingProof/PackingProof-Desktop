$script:LauncherBaselineSchemaVersion = 1
$script:LauncherUpdateProtocolVersion = 1

function Get-LauncherBaselineTag {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$ArtifactPrefix = "PackingProof"
    )

    $normalizedVersion = $Version.Trim().TrimStart('v', 'V')
    if ([string]::IsNullOrWhiteSpace($ArtifactPrefix) -or
        [string]::Equals($ArtifactPrefix, "PackingProof", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "launcher-v$normalizedVersion"
    }
    return "launcher-$ArtifactPrefix-v$normalizedVersion"
}
# 启动器指纹的输入：启动器自己的源码与清单，加上它链接的整个 UpdateCore 项目的 .cs，
# 以及软件配置选择器。当前配置目录由 brand_profile_sha256 单独计算，避免把其它软件配置
# 或软件配置模板误算进公开发行基线。
# 这里原来是手写清单，漏了 SharedRuntimeLayout.cs（启动器启动前就用它处理共享运行时）以及后来
# 新增的 ResumableFileDownload.cs；只改这些文件时前置检查会以为基线还新鲜，发布包里就可能夹着
# 与源码不一致的启动器。改成按项目取全部 .cs，新增文件自动纳入。
# 清单里的路径是“逻辑路径”，必须保持稳定：启动器基线标签、已发布的 LauncherPatch 和
# launcher-baseline.json 都按这套名字记录。仓库目录调整时只改下面的物理路径映射，
# 逻辑指纹和基线清单都不变。
$script:LauncherFingerprintPathMap = [ordered]@{
    "Tools\" = "tools\"
    "ExpressPackingMonitoring.Launcher\" = "src\ExpressPackingMonitoring.Launcher\"
    "ExpressPackingMonitoring.UpdateCore\" = "src\ExpressPackingMonitoring.UpdateCore\"
    "ExpressPackingMonitoring\" = "src\ExpressPackingMonitoring\"
    "Branding\" = "branding\"
}

function Resolve-LauncherFingerprintPath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    foreach ($prefix in $script:LauncherFingerprintPathMap.Keys) {
        if ($RelativePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $script:LauncherFingerprintPathMap[$prefix] + $RelativePath.Substring($prefix.Length)
        }
    }
    return $RelativePath
}

$script:LauncherFingerprintFiles = @(
    "ExpressPackingMonitoring.Launcher\Program.cs",
    "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj",
    "ExpressPackingMonitoring.UpdateCore\ExpressPackingMonitoring.UpdateCore.csproj",
    "Tools\Install-LauncherPatch.cmd",
    "Tools\Apply-LauncherPatch.ps1",
    "Branding\Brand.props"
) + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "..\src\ExpressPackingMonitoring.UpdateCore") `
        -Filter "*.cs" -File |
    Sort-Object -Property Name |
    ForEach-Object { "ExpressPackingMonitoring.UpdateCore\$($_.Name)" })
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
        [Parameter(Mandatory = $true)][string]$UpdateCheckUrl,
        [string]$BrandProfileId = "",
        [string]$BrandProfileContentSha256 = ""
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($relativePath in $script:LauncherFingerprintFiles) {
            $fullPath = Join-Path $RepositoryRoot (Resolve-LauncherFingerprintPath $relativePath)
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
        if (-not [string]::IsNullOrWhiteSpace($BrandProfileId)) {
            Add-LauncherFingerprintValue -Hasher $sha -Value "brand_profile=$($BrandProfileId.Trim())"
        }
        if (-not [string]::IsNullOrWhiteSpace($BrandProfileContentSha256)) {
            Add-LauncherFingerprintValue -Hasher $sha -Value "brand_profile_sha256=$($BrandProfileContentSha256.Trim().ToLowerInvariant())"
        }
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
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [string]$ArtifactPrefix = ""
    )

    $text = [System.IO.File]::ReadAllText($SourcePath, [System.Text.Encoding]::UTF8)
    if (-not [string]::IsNullOrWhiteSpace($ArtifactPrefix)) {
        $text = $text.Replace("__BRAND_ARTIFACT_PREFIX__", $ArtifactPrefix.Trim())
    }
    if ($text.Contains("__BRAND_", [System.StringComparison]::Ordinal)) {
        throw "Command wrapper still contains an unresolved brand token: $SourcePath"
    }
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
    $artifactPrefix = if ($null -ne $Manifest.PSObject.Properties["artifact_prefix"]) {
        [string]$Manifest.artifact_prefix
    }
    else {
        "PackingProof"
    }
    if (([int]$Manifest.schema_version) -ne $script:LauncherBaselineSchemaVersion -or
        ([int]$Manifest.protocol_version) -ne $script:LauncherUpdateProtocolVersion -or
        [string]::IsNullOrWhiteSpace($version) -or
        ([string]$Manifest.tag) -ne (Get-LauncherBaselineTag -Version $version -ArtifactPrefix $artifactPrefix) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.release_tag) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.runtime) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.update_check_url) -or
        -not (Test-LauncherSha256 ([string]$Manifest.source_fingerprint)) -or
        -not (Test-LauncherSha256 ([string]$Manifest.brand_profile_sha256)) -or
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

function Test-LauncherBaselineSourceUnchanged {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)]$Baseline,
        [string]$BrandProfileContentSha256 = ""
    )

    $baselineBrandProfileHash = [string]$Baseline.brand_profile_sha256
    if (-not [string]::IsNullOrWhiteSpace($baselineBrandProfileHash) -and
        -not [string]::Equals(
            $baselineBrandProfileHash,
            $BrandProfileContentSha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $logicalFiles = @(Get-LauncherFingerprintFiles)
    $baselineFiles = @($Baseline.fingerprint_files | ForEach-Object { ([string]$_).Replace('/', '\') })
    if ($logicalFiles.Count -ne $baselineFiles.Count) {
        return $false
    }
    for ($index = 0; $index -lt $logicalFiles.Count; $index++) {
        if (-not [string]::Equals($logicalFiles[$index], $baselineFiles[$index], [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    $tagCommit = & git -C $RepositoryRoot rev-parse --verify --quiet "$Tag^{commit}" 2>$null |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($tagCommit)) {
        return $false
    }
    $tagCommit = $tagCommit.Trim()

    # 目录调整会让文件改名，但内容没变；按“标签里的逻辑路径 + HEAD 里的物理路径”
    # 比较 blob，仍能发现真正的源码改动、增删文件。
    foreach ($logicalPath in $logicalFiles) {
        $tagPath = $logicalPath.Replace('\', '/')
        $headPath = (Resolve-LauncherFingerprintPath $logicalPath).Replace('\', '/')
        # 旧基线标签可能仍在逻辑目录（例如 ExpressPackingMonitoring.Launcher\Program.cs），
        # 新基线标签已经在当前物理目录（src\...）。两种标签都要能比较，否则目录调整后
        # 新建的 launcher-vX.Y.Z 会永远被误判为“源码已变化”。
        $tagBlob = ""
        foreach ($candidateTagPath in @($tagPath, $headPath) | Select-Object -Unique) {
            $candidateBlob = & git -C $RepositoryRoot rev-parse --verify --quiet "${tagCommit}:$candidateTagPath" 2>$null |
                Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($candidateBlob)) {
                $tagBlob = $candidateBlob.Trim()
                break
            }
        }
        $headBlob = & git -C $RepositoryRoot rev-parse --verify --quiet "HEAD:$headPath" 2>$null |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($tagBlob) -or
            [string]::IsNullOrWhiteSpace($headBlob) -or
            -not [string]::Equals($tagBlob, $headBlob.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

function Get-LauncherPackageEntries {
    return @($script:LauncherPackageEntries)
}
