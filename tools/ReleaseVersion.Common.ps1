# 发布版本号与产物名的统一规则。
#
# 打包脚本 tools\Publish-CleanPackage.ps1 与发布脚本 tools\Publish-Releases.ps1
# 必须用同一套归一化，否则就会出现「有时带 v、有时不带 v」的错配：
#   - tag 可能写成 v0.0.67、0.0.67，或带后缀的 v0.0.67-rc1
#   - 产物名固定是：目录、补丁包、本地校验清单用 v<纯版本号>；
#     Setup 与更新清单用不带 v 的纯版本号
# 任何一边自己用 tag 拼名字，都会在 tag 形式不同的时候找不到文件。

function Test-GiteeAttachmentFits {
    param([Parameter(Mandatory = $true)][long]$Size)

    # Gitee 单附件上限按 100MB 计算；需求是“小于 100MB 才直接传 Setup”。
    return $Size -lt 100MB
}

function Get-NormalizedReleaseVersion {
    param([string]$RawVersion)

    $value = "$RawVersion".Trim()
    if ($value.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    $suffixIndex = $value.IndexOfAny(@('+', '-'))
    if ($suffixIndex -ge 0) {
        $value = $value.Substring(0, $suffixIndex)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return "0.0.0"
    }

    return $value
}

# 由精确 tag 推断本地产物名，返回：
#   NormalizedVersion / ReleaseTag / PackageRoot
#   SetupFileName / UpdateJsonFileName / AppPatchFileName / LauncherPatchFileName
function Get-ReleaseArtifactNames {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $normalizedVersion = Get-NormalizedReleaseVersion $Tag
    if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "tag $Tag 的版本号不是 X.Y.Z 形式，无法推断产物名"
    }

    $releaseTag = "v$normalizedVersion"
    return @{
        NormalizedVersion = $normalizedVersion
        ReleaseTag = $releaseTag
        PackageRoot = Join-Path $RepoRoot "package\PackingProof+$releaseTag"
        SetupFileName = "PackingProof_Setup_v$normalizedVersion.exe"
        UpdateJsonFileName = "update_v$normalizedVersion.json"
        AppPatchFileName = "PackingProof_AppPatch_$releaseTag.zip"
        LauncherPatchFileName = "PackingProof_LauncherPatch_$releaseTag.zip"
        StorePackageRoot = Join-Path $RepoRoot "package\store\PackingProof_Store_v$normalizedVersion"
        StorePackageFileName = "PackingProof_Store_v$normalizedVersion.msix"
        StoreSymbolFileName = "PackingProof_Store_v$normalizedVersion.appxsym"
        StoreManifestFileName = "store_package_$releaseTag.json"
    }
}

# Setup 与 Store MSIX 必须来自同一份应用负载。这里用“相对路径 + SHA256”的
# 稳定清单做指纹；pdb/dbg/xml 是 Store 打包时会主动剔除的文件，不参与比较。
function Get-AppPayloadManifestFiles {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    if (-not (Test-Path -LiteralPath $AppDir -PathType Container)) {
        throw "找不到应用负载目录：$AppDir"
    }

    return @(
        Get-ChildItem -LiteralPath $AppDir -Recurse -File |
            Where-Object { $_.Extension.ToLowerInvariant() -notin @(".pdb", ".dbg", ".xml") } |
            Sort-Object -Property FullName
    )
}

function Get-AppPayloadFileHashes {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    $map = [ordered]@{}
    foreach ($file in (Get-AppPayloadManifestFiles -AppDir $AppDir)) {
        $relativePath = [System.IO.Path]::GetRelativePath($AppDir, $file.FullName).Replace('\', '/')
        $map[$relativePath] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Get-AppPayloadFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    $map = Get-AppPayloadFileHashes -AppDir $AppDir
    $builder = [System.Text.StringBuilder]::new()
    foreach ($entry in $map.GetEnumerator()) {
        [void]$builder.Append([string]$entry.Key).Append("`n")
        [void]$builder.Append([string]$entry.Value).Append("`n")
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ArchiveTextEntry {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName.Replace('\', '/') -eq $EntryName } |
            Select-Object -First 1
        if ($null -eq $entry) {
            throw "压缩包缺少文件：$EntryName（$ArchivePath）"
        }

        $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-AllowedStoreArchiveEntry {
    param([Parameter(Mandatory = $true)][string]$Name)

    if ($Name -in @(
        "AppxManifest.xml",
        "AppxBlockMap.xml",
        "AppxSignature.p7x",
        "[Content_Types].xml",
        "PackingProof.PackageEdition.json"
    )) {
        return $true
    }
    if ($Name.StartsWith("Assets/", [System.StringComparison]::Ordinal) -or
        $Name.StartsWith("AppxMetadata/", [System.StringComparison]::Ordinal)) {
        return $true
    }
    return $Name -match '^resources(?:\..+)?\.pri$'
}

function Assert-AppPayloadMatchesArchive {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir,
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [string[]]$ExcludeRelativePrefixes = @()
    )

    $expectedFiles = Get-AppPayloadFileHashes -AppDir $AppDir
    if ($ExcludeRelativePrefixes.Count -gt 0) {
        $filtered = [ordered]@{}
        foreach ($expected in $expectedFiles.GetEnumerator()) {
            $relativePath = [string]$expected.Key
            $excluded = $false
            foreach ($prefix in $ExcludeRelativePrefixes) {
                if (-not [string]::IsNullOrWhiteSpace($prefix) -and
                    $relativePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $excluded = $true
                    break
                }
            }
            if (-not $excluded) {
                $filtered[$relativePath] = $expected.Value
            }
        }
        $expectedFiles = $filtered
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $entries[$entry.FullName.Replace('\', '/')] = $entry
        }

        foreach ($expected in $expectedFiles.GetEnumerator()) {
            $relativePath = [string]$expected.Key
            if (-not $entries.ContainsKey($relativePath)) {
                throw "MSIX 缺少 Setup 负载文件：$relativePath"
            }

            $stream = $entries[$relativePath].Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $actualHash = ([System.BitConverter]::ToString(
                            $sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                }
                finally {
                    $sha.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            if (-not [string]::Equals(
                    $actualHash,
                    [string]$expected.Value,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "MSIX 与 Setup 负载文件不一致：$relativePath"
            }
        }

        foreach ($entryName in $entries.Keys) {
            if ($entryName.EndsWith("/", [System.StringComparison]::Ordinal)) {
                continue
            }
            if ($expectedFiles.Contains($entryName)) {
                continue
            }
            if (Test-AllowedStoreArchiveEntry -Name $entryName) {
                continue
            }
            throw "MSIX 包含 Setup 负载之外的文件：$entryName"
        }

        return $expectedFiles.Count
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-StorePayloadManifest {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    if ([string]::IsNullOrWhiteSpace([string]$Manifest.app_payload_sha256)) {
        throw "Store 包清单缺少 app_payload_sha256，无法证明与本次 Setup 同源；请重新出包"
    }

    $actualFingerprint = Get-AppPayloadFingerprint -AppDir $AppDir
    if (-not [string]::Equals(
            [string]$Manifest.app_payload_sha256,
            $actualFingerprint,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Store 包清单记录的应用负载与本次 Setup app\ 不一致（旧 MSIX 或旧清单），拒绝发布"
    }

    $fileCount = @(Get-AppPayloadManifestFiles -AppDir $AppDir).Count
    if ($null -eq $Manifest.app_payload_file_count -or
        [int]$Manifest.app_payload_file_count -ne $fileCount) {
        throw "Store 包清单记录的应用负载文件数与本次 Setup 不一致，拒绝发布"
    }

    if ([string]::IsNullOrWhiteSpace([string]$Manifest.app_release_version) -or
        [string]$Manifest.app_release_version -ne $Version) {
        throw "Store 包清单版本与发布版本不一致：$($Manifest.app_release_version) / $Version"
    }
}

function Assert-SetupPayloadIntegrity {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$SetupPath,
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    if ([string]::IsNullOrWhiteSpace([string]$Manifest.app_payload_sha256) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.setup_sha256)) {
        throw "Setup 完整性清单缺少应用负载或安装包哈希；请重新出包"
    }
    if ([string]$Manifest.app_release_version -ne $Version) {
        throw "Setup 完整性清单版本与发布版本不一致：$($Manifest.app_release_version) / $Version"
    }
    if ([string]$Manifest.setup_file -ne (Split-Path -Leaf $SetupPath)) {
        throw "Setup 完整性清单文件名与安装包不一致：$($Manifest.setup_file)"
    }

    $actualSetupHash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualSetupSize = (Get-Item -LiteralPath $SetupPath).Length
    if (-not [string]::Equals(
            [string]$Manifest.setup_sha256,
            $actualSetupHash,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [long]$Manifest.setup_size -ne $actualSetupSize) {
        throw "Setup 安装包在出包后被替换或损坏，拒绝发布"
    }

    $actualFingerprint = Get-AppPayloadFingerprint -AppDir $AppDir
    if (-not [string]::Equals(
            [string]$Manifest.app_payload_sha256,
            $actualFingerprint,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Setup 完整性清单记录的 app 负载与本次 exact app 不一致，拒绝发布"
    }
    $fileCount = @(Get-AppPayloadManifestFiles -AppDir $AppDir).Count
    if ($null -eq $Manifest.app_payload_file_count -or
        [int]$Manifest.app_payload_file_count -ne $fileCount) {
        throw "Setup 完整性清单记录的应用负载文件数与本次 exact app 不一致，拒绝发布"
    }
}

# Setup 与 Store 同源校验只认本次构建目录。没有 ExpectedFingerprint 时
# （AppPatch 产物守卫）才允许退回同版本最新 sibling，避免旧目录被误当成本次负载。
function Resolve-ReleasePayloadAppDir {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$ExpectedFingerprint = "",
        [switch]$RequireExact
    )

    $exact = Join-Path $PackageRoot ("PackingProof+v" + $Version + "\app")
    if (-not [string]::IsNullOrWhiteSpace($ExpectedFingerprint)) {
        if (-not (Test-Path -LiteralPath (Join-Path $exact "ExpressPackingMonitoring.exe") -PathType Leaf)) {
            throw "本次 Setup 的 app 目录不存在：$exact；不能用旧 sibling 目录替代"
        }
        $actualFingerprint = Get-AppPayloadFingerprint -AppDir $exact
        if (-not [string]::Equals(
                $actualFingerprint,
                $ExpectedFingerprint,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "本次 Setup 的 app 目录与 Store 清单不一致（旧 MSIX 或旧清单），拒绝发布：$exact"
        }
        return $exact
    }

    if ($RequireExact) {
        if (-not (Test-Path -LiteralPath (Join-Path $exact "ExpressPackingMonitoring.exe") -PathType Leaf)) {
            throw "本次 Setup 的 app 目录不存在：$exact；不能用旧 sibling 目录替代"
        }
        return $exact
    }

    $candidateAppDirs = @()
    if (Test-Path -LiteralPath (Join-Path $exact "ExpressPackingMonitoring.exe") -PathType Leaf) {
        $candidateAppDirs += $exact
    }
    $candidateAppDirs += @(
        Get-ChildItem -LiteralPath $PackageRoot -Directory -Filter ("PackingProof+v" + $Version + "*") `
            -ErrorAction SilentlyContinue |
            Where-Object {
                Test-Path -LiteralPath (Join-Path $_.FullName "app\ExpressPackingMonitoring.exe") `
                    -PathType Leaf
            } |
            ForEach-Object { Join-Path $_.FullName "app" }
    )
    $candidateAppDirs = @(
        $candidateAppDirs |
            Sort-Object -Unique |
            Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending
    )
    if ($candidateAppDirs.Count -eq 0) {
        throw "找不到与版本 $Version 对应的 Setup app\ 负载：$PackageRoot"
    }
    return $candidateAppDirs[0]
}
