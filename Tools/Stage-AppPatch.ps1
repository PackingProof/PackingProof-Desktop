[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$UserDataDirectory = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Get-FileSha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-StreamSha256 {
    param([System.IO.Stream]$Stream)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Stream)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-VersionNumber {
    param([string]$Value)

    if ($Value -match "(\d+\.\d+\.\d+(?:\.\d+)?)") {
        return [Version]$Matches[1]
    }
    throw "无法识别版本号：$Value"
}

function Get-JsonProperty {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Test-SafeRelativePath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or [System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    $segments = @($Path.Replace("\", "/").Split("/"))
    return -not ($segments -contains "." -or $segments -contains ".." -or $segments -contains "")
}

function Read-ZipJson {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "增量补丁缺少 $EntryName"
    }

    $stream = $entry.Open()
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
    try {
        return $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Test-PatchArchive {
    param(
        [string]$PatchZipPath,
        [object]$UpdateManifest
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stream = [System.IO.File]::OpenRead($PatchZipPath)
    $archive = New-Object System.IO.Compression.ZipArchive(
        $stream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $false)
    try {
        $patchManifest = Read-ZipJson -Archive $archive -EntryName "patch_manifest.json"
        if (-not [string]::Equals(
            [string](Get-JsonProperty -Object $patchManifest -Name "type"),
            "baseline_patch",
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "不支持的增量补丁类型"
        }

        $latestVersion = [string](Get-JsonProperty -Object $patchManifest -Name "latest_version")
        $baselineVersion = [string](Get-JsonProperty -Object $patchManifest -Name "patch_baseline_version")
        if ((Get-VersionNumber $latestVersion) -ne
            (Get-VersionNumber ([string](Get-JsonProperty -Object $UpdateManifest -Name "latest_version")))) {
            throw "补丁版本与更新清单不一致"
        }
        if ((Get-VersionNumber $baselineVersion) -ne
            (Get-VersionNumber ([string](Get-JsonProperty -Object $UpdateManifest -Name "patch_baseline_version")))) {
            throw "补丁基线与更新清单不一致"
        }

        $files = @((Get-JsonProperty -Object $patchManifest -Name "files"))
        if ($files.Count -eq 0) {
            throw "补丁清单没有可安装文件"
        }

        $seenPaths = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $files) {
            $relativePath = ([string](Get-JsonProperty -Object $file -Name "path")).Replace("\", "/")
            if (-not (Test-SafeRelativePath -Path $relativePath)) {
                throw "补丁清单包含不安全路径：$relativePath"
            }
            if (-not $seenPaths.Add($relativePath)) {
                throw "补丁清单包含重复文件：$relativePath"
            }

            $entry = $archive.GetEntry("files/$relativePath")
            if ($null -eq $entry) {
                throw "补丁文件不存在：$relativePath"
            }

            $expectedSize = [long](Get-JsonProperty -Object $file -Name "size")
            if ($entry.Length -ne $expectedSize) {
                throw "补丁文件大小校验失败：$relativePath"
            }

            $entryStream = $entry.Open()
            try {
                $actualHash = Get-StreamSha256 -Stream $entryStream
            }
            finally {
                $entryStream.Dispose()
            }

            $expectedHash = ([string](Get-JsonProperty -Object $file -Name "sha256")).Trim().ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($expectedHash) -or $actualHash -ne $expectedHash) {
                throw "补丁文件 SHA256 校验失败：$relativePath"
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function Write-UpdateLog {
    param(
        [string]$LogPath,
        [string]$Message
    )

    try {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
        Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value (
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}" -f [DateTime]::Now, $Message)
    }
    catch {
    }
}

if ([string]::IsNullOrWhiteSpace($UserDataDirectory)) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = $env:LOCALAPPDATA
    }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw "无法定位 Windows 用户数据目录"
    }
    $UserDataDirectory = Join-Path $localAppData "ExpressPackingMonitoring"
}

$PackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
$UserDataDirectory = [System.IO.Path]::GetFullPath($UserDataDirectory)
$updatesDirectory = Join-Path $UserDataDirectory "cache\updates"
$pendingDirectory = Join-Path $updatesDirectory "pending"
$manifestPath = Join-Path $PackageRoot "update_manifest.json"
$logPath = Join-Path $UserDataDirectory "log\manual_update.log"
$mutex = [System.Threading.Mutex]::new($false, "Local\ExpressPackingMonitoring.Launcher.Update")
$ownsMutex = $false
$importingDirectory = ""
$previousPendingDirectory = ""
$exitCode = 0

try {
    try {
        $ownsMutex = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        throw "启动器正在处理更新，请稍后重试"
    }

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "手动更新包缺少 update_manifest.json"
    }

    $manifestText = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
    $updateManifest = $manifestText | ConvertFrom-Json
    if (-not [bool](Get-JsonProperty -Object $updateManifest -Name "patch_supported")) {
        throw "此版本不支持增量更新"
    }

    $patchPackage = Get-JsonProperty -Object $updateManifest -Name "patch_package"
    if ($null -eq $patchPackage -or
        -not [string]::Equals(
            [string](Get-JsonProperty -Object $patchPackage -Name "type"),
            "baseline_patch",
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "更新清单中的补丁信息无效"
    }

    $patchCandidates = @(
        Get-ChildItem -LiteralPath $PackageRoot -File |
            Where-Object {
                $_.Name -like "PackingProof_AppPatch_v*.zip" -or
                $_.Name -like "ExpressPackingMonitoring_AppPatch_v*.zip"
            }
    )
    if ($patchCandidates.Count -ne 1) {
        throw "手动更新包中应当只包含一个 AppPatch ZIP"
    }

    $patchZipPath = $patchCandidates[0].FullName
    $patchZipName = $patchCandidates[0].Name
    $expectedSize = [long](Get-JsonProperty -Object $patchPackage -Name "size")
    if ($patchCandidates[0].Length -ne $expectedSize) {
        throw "AppPatch 文件大小校验失败"
    }

    $expectedHash = ([string](Get-JsonProperty -Object $patchPackage -Name "sha256")).Trim().ToLowerInvariant()
    $actualHash = Get-FileSha256 -Path $patchZipPath
    if ([string]::IsNullOrWhiteSpace($expectedHash) -or $actualHash -ne $expectedHash) {
        throw "AppPatch SHA256 校验失败"
    }

    Write-Host "正在校验增量更新包..." -ForegroundColor Cyan
    Test-PatchArchive -PatchZipPath $patchZipPath -UpdateManifest $updateManifest

    $incomingVersion = Get-VersionNumber ([string](Get-JsonProperty -Object $updateManifest -Name "latest_version"))
    $pendingManifestPath = Join-Path $pendingDirectory "update_manifest.json"
    if (Test-Path -LiteralPath $pendingManifestPath -PathType Leaf) {
        try {
            $pendingManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $pendingManifestPath | ConvertFrom-Json
            $pendingVersion = Get-VersionNumber ([string](Get-JsonProperty -Object $pendingManifest -Name "latest_version"))
            if ($pendingVersion -gt $incomingVersion) {
                throw "已有更高版本 v$pendingVersion 等待安装，不能被当前补丁覆盖"
            }
            if ($pendingVersion -eq $incomingVersion) {
                Write-Host ""
                Write-Host "v$incomingVersion 增量更新已经准备完成，无需重复导入" -ForegroundColor Green
                return
            }
        }
        catch {
            if ($_.Exception.Message -like "已有更高版本*") {
                throw
            }
        }
    }

    New-Item -ItemType Directory -Force -Path $updatesDirectory | Out-Null
    $operationId = [Guid]::NewGuid().ToString("N")
    $importingDirectory = Join-Path $updatesDirectory "importing-$operationId"
    $previousPendingDirectory = Join-Path $updatesDirectory "pending-previous-$operationId"
    New-Item -ItemType Directory -Path $importingDirectory | Out-Null

    Copy-Item -LiteralPath $patchZipPath -Destination (Join-Path $importingDirectory $patchZipName)
    [System.IO.File]::WriteAllText(
        (Join-Path $importingDirectory "update_manifest.json"),
        $manifestText,
        (New-Object System.Text.UTF8Encoding($false)))

    if ((Get-FileSha256 -Path (Join-Path $importingDirectory $patchZipName)) -ne $expectedHash) {
        throw "复制到更新缓存后的 AppPatch 校验失败"
    }

    if (Test-Path -LiteralPath $pendingDirectory) {
        [System.IO.Directory]::Move($pendingDirectory, $previousPendingDirectory)
    }

    try {
        [System.IO.Directory]::Move($importingDirectory, $pendingDirectory)
        $importingDirectory = ""
    }
    catch {
        if ((Test-Path -LiteralPath $previousPendingDirectory) -and
            -not (Test-Path -LiteralPath $pendingDirectory)) {
            [System.IO.Directory]::Move($previousPendingDirectory, $pendingDirectory)
        }
        throw
    }

    if (Test-Path -LiteralPath $previousPendingDirectory) {
        Remove-Item -LiteralPath $previousPendingDirectory -Recurse -Force
    }
    $previousPendingDirectory = ""

    Write-UpdateLog -LogPath $logPath -Message "Manual patch staged: latest=$incomingVersion, file=$patchZipName"
    Write-Host ""
    Write-Host "增量更新已准备完成" -ForegroundColor Green
    Write-Host "请关闭并重新打开快递打包监控，启动器将在启动前完成安装"
}
catch {
    $failure = $_.Exception.Message
    Write-UpdateLog -LogPath $logPath -Message "Manual patch staging failed: $failure"
    Write-Host ""
    Write-Host "准备增量更新失败：$failure" -ForegroundColor Red
    Write-Host "现有程序和已经准备好的更新均未修改"
    $exitCode = 1
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($importingDirectory) -and
        (Test-Path -LiteralPath $importingDirectory)) {
        Remove-Item -LiteralPath $importingDirectory -Recurse -Force
    }
    if (-not [string]::IsNullOrWhiteSpace($previousPendingDirectory) -and
        (Test-Path -LiteralPath $previousPendingDirectory) -and
        -not (Test-Path -LiteralPath $pendingDirectory)) {
        [System.IO.Directory]::Move($previousPendingDirectory, $pendingDirectory)
    }
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}

exit $exitCode
