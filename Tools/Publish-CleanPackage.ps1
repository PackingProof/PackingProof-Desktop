param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$Version = "",
    [string]$BaselineAppDir = "",
    [string]$BaselineLauncherPath = "",
    [string]$BaselineLauncherManifestPath = "",
    [string]$LauncherBaselineManifestPath = "",
    [string]$LauncherBaselinePackagePath = "",
    [string]$SevenZipPath = "",
    [ValidateRange(1, 9)]
    [int]$SevenZipCompressionLevel = 5,
    [ValidateSet("Optimal", "SmallestSize")]
    [string]$ZipCompressionLevel = "Optimal",
    [ValidateSet("lzma2/normal", "lzma2/max", "lzma2/ultra64")]
    [string]$InstallerCompression = "lzma2/ultra64",
    [string]$PatchBaselineVersion = "0.0.18",
    [switch]$SkipTtsCacheGeneration,
    [switch]$ConfirmManualCoreChecks,
    [switch]$ReuseExistingLauncherBaseline,
    [switch]$DisablePatch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "LauncherBaseline.Common.ps1")
. (Join-Path $PSScriptRoot "FFmpegBaseline.Common.ps1")
. (Join-Path $PSScriptRoot "AppPatchRuntimeCompatibility.Common.ps1")
$appProject = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$releaseValidationScript = Join-Path $repoRoot "Tools\Test-Release.ps1"
$installerBuildScript = Join-Path $repoRoot "Tools\Build-Installer.ps1"
$ttsCacheBuilderProject = Join-Path $repoRoot "Tools\ExpressPackingMonitoring.TtsCacheBuilder\ExpressPackingMonitoring.TtsCacheBuilder.csproj"
$appPatchCmdSource = Join-Path $repoRoot "Tools\Install-AppPatch.cmd"
$appPatchScriptSource = Join-Path $repoRoot "Tools\Apply-AppPatch.ps1"
$appPatchInstallerCmdName = "双击更新主程序.cmd"
$appPatchInstallerScriptName = "apply_app_patch.ps1"
$appPatchNoticeName = "主程序更新说明.txt"
$launcherPatchCmdSource = Join-Path $repoRoot "Tools\Install-LauncherPatch.cmd"
$launcherPatchScriptSource = Join-Path $repoRoot "Tools\Apply-LauncherPatch.ps1"
$launcherPatchInstallerCmdName = "双击更新启动器.cmd"
$launcherPatchInstallerScriptName = "apply_launcher_patch.ps1"
$launcherPatchManifestName = "launcher_patch_manifest.json"
$launcherPatchNoticeName = "启动器更新说明.txt"
$ffmpegBaselineManifestPath = Join-Path $PSScriptRoot "ffmpeg-baseline.json"

function Invoke-CoreRegressionTests {
    if (-not (Test-Path $releaseValidationScript)) {
        throw "Release validation script not found: $releaseValidationScript"
    }

    & $releaseValidationScript -Configuration $Configuration
}

function Invoke-DotNetPublish {
    param([string[]]$Arguments)

    & dotnet publish @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

# 结束从仓库内 Release/构建产物目录启动的遗留主程序实例，避免其锁定构建输出。
# 只处理路径位于仓库开发构建目录（ExpressPackingMonitoring\bin\Release 或 package 下 .build-artifacts）
# 的进程，绝不触碰 %LOCALAPPDATA% 等已安装版本。
function Stop-StaleReleaseAppInstances {
    $releaseBinRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $repoRoot "ExpressPackingMonitoring\bin\Release"))
    $packageRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "package"))
    $stopped = @()
    foreach ($process in @(Get-Process -Name "ExpressPackingMonitoring" -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $process.Path
        }
        catch {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($processPath)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath($processPath)
        $isReleaseBinInstance = $fullPath.StartsWith(
            $releaseBinRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
        $isBuildArtifactInstance = $fullPath.StartsWith(
                $packageRoot + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            $fullPath.IndexOf(
                ".build-artifacts",
                [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        if (-not ($isReleaseBinInstance -or $isBuildArtifactInstance)) {
            continue
        }

        Write-Host "Stopping stale release app instance (PID $($process.Id)): $fullPath"
        try {
            $null = $process.CloseMainWindow()
        }
        catch {
        }
        if (-not $process.WaitForExit(5000)) {
            try {
                $process.Kill()
            }
            catch {
            }
            try {
                $process.WaitForExit(3000)
            }
            catch {
            }
        }
        $stopped += "$($process.Id)"
    }

    if ($stopped.Count -gt 0) {
        Write-Host "Stopped stale release app instances: $($stopped -join ', ')"
    }
}

function New-DefaultTtsCache {
    Stop-StaleReleaseAppInstances

    $targetDir = Join-Path $repoRoot "package\tts_cache"
    if ($SkipTtsCacheGeneration) {
        Write-Host "Default TTS cache generation skipped by option."
        return
    }

    if (-not (Test-Path $ttsCacheBuilderProject)) {
        throw "TTS cache builder project not found: $ttsCacheBuilderProject"
    }

    $tempDir = Join-Path $repoRoot "package\.tts_cache_generation"
    if (Test-Path $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    if (Test-Path $targetDir) {
        Copy-Item -Path (Join-Path $targetDir "*") -Destination $tempDir -Recurse -Force
    }

    try {
        Write-Host "Generating default TTS cache..."
        dotnet run --project $ttsCacheBuilderProject -c $Configuration -- $tempDir
        if ($LASTEXITCODE -ne 0) {
            throw "Default TTS cache generation failed with exit code $LASTEXITCODE"
        }

        $cacheFiles = @(Get-ChildItem -LiteralPath $tempDir -File |
            Where-Object { $_.Extension -in ".mp3", ".wav" })
        if ($cacheFiles.Count -eq 0) {
            throw "Default TTS cache generation produced no audio files."
        }

        if (Test-Path $targetDir) {
            Remove-Item -LiteralPath $targetDir -Recurse -Force
        }
        Move-Item -LiteralPath $tempDir -Destination $targetDir
        Write-Host "Default TTS cache generated: $($cacheFiles.Count) files"
    }
    finally {
        if (Test-Path $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force
        }
    }
}

function Get-PackageVersion {
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        return $Version.Trim()
    }

    $tagsAtHead = @(& git -C $repoRoot tag --points-at HEAD)
    if ($LASTEXITCODE -eq 0) {
        $tag = $tagsAtHead |
            Where-Object { $_ -match '^v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$' } |
            Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($tag)) {
            return $tag.Trim()
        }
    }

    $description = (& git -C $repoRoot describe --tags --match "v[0-9]*" --always --dirty 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($description)) {
        return $description.Trim()
    }

    return "0.0.0-local"
}

function Get-GitCommitId {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($commit)) {
        return $commit.Trim()
    }

    return ""
}

function ConvertTo-SafePathName {
    param([string]$Name)

    $safeName = $Name
    foreach ($char in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safeName = $safeName.Replace($char, "_")
    }

    return $safeName
}

function Test-IsStrictDescendantPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if ([string]::Equals($fullPath, $fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

$packageVersion = Get-PackageVersion
$packageName = ConvertTo-SafePathName "PackingProof+$packageVersion"
$defaultPackageVersionRoot = Join-Path $repoRoot "package\$packageName"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $defaultPackageVersionRoot $packageName
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$zipFullPath = if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    [System.IO.Path]::GetFullPath((Join-Path $defaultPackageVersionRoot "$packageName.zip"))
} else {
    [System.IO.Path]::GetFullPath($ZipPath)
}
$sevenZipFullPath = [System.IO.Path]::ChangeExtension($zipFullPath, ".7z")
$packageArtifactRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $zipFullPath))
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
if (-not (Test-IsStrictDescendantPath -Path $outputFullPath -Root $repoFullPath)) {
    throw "OutputDir must be inside the repository: $outputFullPath"
}
if (-not (Test-IsStrictDescendantPath -Path $zipFullPath -Root $repoFullPath)) {
    throw "ZipPath must be inside the repository: $zipFullPath"
}
if ([string]::Equals($packageArtifactRoot, $outputFullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
    $packageArtifactRoot.StartsWith(($outputFullPath.TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ZipPath must not be inside OutputDir, otherwise the package may include itself: $zipFullPath"
}

Invoke-CoreRegressionTests
if (-not $ConfirmManualCoreChecks) {
    Write-Warning "Manual core business and recovery checks are not confirmed. Packaging will continue; review RELEASE_CHECKLIST.md and report any unverified real-device scenarios with the release."
}

if (Test-Path $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
if (Test-Path $zipFullPath) {
    Remove-Item -LiteralPath $zipFullPath -Force
}
if (Test-Path $sevenZipFullPath) {
    Remove-Item -LiteralPath $sevenZipFullPath -Force
}

function Remove-PackageRuntimeState {
    param([string]$AppDir)

    $filePatterns = @(
        "config.json",
        "videos.db",
        "videos.db-*",
        "orderinfo_cache.json",
        "*.log",
        "*.audio.log",
        "audio_probe*.wav",
        "audio_probe*.mkv",
        "audio_probe*.mp4",
        "audio_probe*_decoded.wav",
        "*.mkv",
        "*.mp4"
    )

    foreach ($pattern in $filePatterns) {
        Get-ChildItem -LiteralPath $AppDir -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    $directories = @("tts_cache", "transcache", "Videos")
    foreach ($dir in $directories) {
        Get-ChildItem -LiteralPath $AppDir -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $dir } |
            Remove-Item -Recurse -Force
    }
}

function Copy-PackageTtsCache {
    param([string]$AppDir)

    $sourceDir = Join-Path $repoRoot "package\tts_cache"
    if (-not (Test-Path $sourceDir)) {
        Write-Host "Package tts_cache not found, skipped: $sourceDir"
        return
    }

    $targetDir = Join-Path $AppDir "tts_cache"
    if (Test-Path $targetDir) {
        Remove-Item -LiteralPath $targetDir -Recurse -Force
    }

    Copy-Item -LiteralPath $sourceDir -Destination $targetDir -Recurse -Force
    Write-Host "Package tts_cache copied: $targetDir"
}

function Compress-PackageWithRetry {
    param(
        [string]$SourceDir,
        [string]$DestinationZip,
        [ValidateSet("Optimal", "SmallestSize")]
        [string]$CompressionLevel
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $compression = [System.IO.Compression.CompressionLevel]::$CompressionLevel
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastError = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            if (Test-Path $DestinationZip) {
                Remove-Item -LiteralPath $DestinationZip -Force
            }

            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $SourceDir,
                $DestinationZip,
                $compression,
                $false,
                [System.Text.Encoding]::UTF8)
            $stopwatch.Stop()
            $archiveSizeMiB = [Math]::Round((Get-Item -LiteralPath $DestinationZip).Length / 1MB, 2)
            Write-Host "ZIP created: $DestinationZip ($archiveSizeMiB MiB, $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)) s, $CompressionLevel)"
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }

    throw $lastError
}

function Resolve-SevenZipExecutable {
    if (-not [string]::IsNullOrWhiteSpace($SevenZipPath)) {
        $candidate = [System.IO.Path]::GetFullPath($SevenZipPath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
        throw "SevenZipPath does not point to 7z.exe: $candidate"
    }

    $environmentPath = $env:SEVEN_ZIP_EXE
    if (-not [string]::IsNullOrWhiteSpace($environmentPath)) {
        $candidate = [System.IO.Path]::GetFullPath($environmentPath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
        throw "SEVEN_ZIP_EXE does not point to 7z.exe: $candidate"
    }

    $command = Get-Command "7z.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    throw "7-Zip was not found. Install it with: winget install --id 7zip.7zip -e -s winget"
}

function Compress-Package7zWithRetry {
    param(
        [string]$SourceDir,
        [string]$DestinationArchive,
        [string]$SevenZipExecutable,
        [int]$CompressionLevel
    )

    $compressionArguments = @(
        "a"
        "-t7z"
        "-mx=$CompressionLevel"
        "-m0=lzma2"
    )
    if ($CompressionLevel -ge 9) {
        $compressionArguments += "-md=128m"
        $compressionArguments += "-mfb=273"
    }
    $compressionArguments += @(
        "-ms=on"
        "-mmt=on"
        "-bso0"
        "-bsp0"
        "--"
        $DestinationArchive
        ".\*"
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (Test-Path -LiteralPath $DestinationArchive) {
                Remove-Item -LiteralPath $DestinationArchive -Force
            }

            Push-Location $SourceDir
            try {
                & $SevenZipExecutable @compressionArguments
                if ($LASTEXITCODE -ne 0) {
                    throw "7-Zip creation failed with exit code $LASTEXITCODE"
                }
            }
            finally {
                Pop-Location
            }

            & $SevenZipExecutable t -bso0 -bsp0 -- $DestinationArchive
            if ($LASTEXITCODE -ne 0) {
                throw "7-Zip integrity test failed with exit code $LASTEXITCODE"
            }
            $stopwatch.Stop()
            $archiveSizeMiB = [Math]::Round((Get-Item -LiteralPath $DestinationArchive).Length / 1MB, 2)
            Write-Host "7z created: $DestinationArchive ($archiveSizeMiB MiB, $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)) s, level $CompressionLevel)"
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }

    throw $lastError
}

function Test-SevenZipContainsEntry {
    param(
        [string]$ArchivePath,
        [string]$EntryName,
        [string]$SevenZipExecutable
    )

    $listing = @(& $SevenZipExecutable l -slt -ba -- $ArchivePath)
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip listing failed with exit code $LASTEXITCODE"
    }
    $expectedLine = "Path = " + $EntryName.Replace("/", "\")
    return $listing -contains $expectedLine
}

function Get-NormalizedReleaseVersion {
    param([string]$RawVersion)

    $value = $RawVersion.Trim()
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

function Test-ZipContainsEntry {
    param(
        [string]$ZipFile,
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::Equals($entry.FullName.Replace('\', '/'), $EntryName, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }
    finally {
        $zip.Dispose()
    }
}

function ConvertFrom-Utf8Base64 {
    param([string]$Value)

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Value))
}

function Get-DotEnvValue {
    param([string]$Key)

    $paths = @(
        (Join-Path $repoRoot ".env"),
        (Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\.env"),
        (Join-Path $repoRoot "ExpressPackingMonitoring\.env")
    )

    foreach ($path in $paths) {
        if (-not (Test-Path $path)) {
            continue
        }

        foreach ($line in [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)) {
            $trimmed = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
                continue
            }

            $separatorIndex = $trimmed.IndexOf("=")
            if ($separatorIndex -le 0) {
                continue
            }

            $name = $trimmed.Substring(0, $separatorIndex).Trim()
            if (-not [string]::Equals($name, $Key, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $value = $trimmed.Substring($separatorIndex + 1).Trim()
            if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                ($value.StartsWith("'") -and $value.EndsWith("'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }

            return $value
        }
    }

    return ""
}

function Get-ConfiguredValue {
    param(
        [string]$Key,
        [string]$DefaultValue
    )

    $envValue = [Environment]::GetEnvironmentVariable($Key)
    if (-not [string]::IsNullOrWhiteSpace($envValue)) {
        return $envValue.Trim()
    }

    $dotEnvValue = Get-DotEnvValue -Key $Key
    if (-not [string]::IsNullOrWhiteSpace($dotEnvValue)) {
        return $dotEnvValue.Trim()
    }

    return $DefaultValue
}

function Get-ReleaseUrlBase {
    $explicitBase = Get-ConfiguredValue -Key "RELEASE_URL_BASE" -DefaultValue ""
    if (-not [string]::IsNullOrWhiteSpace($explicitBase)) {
        return $explicitBase.TrimEnd("/")
    }

    $checkUrl = Get-ConfiguredValue -Key "UPDATE_CHECK_URL" -DefaultValue "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases/latest"
    if ($checkUrl -match "^https://api\.github\.com/repos/([^/]+/[^/]+)/releases/latest/?$") {
        return "https://github.com/$($Matches[1])/releases"
    }

    if ($checkUrl -match "^https://gitee\.com/api/v5/repos/([^/]+/[^/]+)/releases/latest/?$") {
        return "https://gitee.com/$($Matches[1])/releases"
    }

    return "https://gitee.com/PackingProof/PackingProof-Desktop/releases"
}

function Expand-ReleaseTemplate {
    param(
        [string]$Template,
        [string]$ReleaseTag,
        [string]$FileName
    )

    return $Template.Replace("{tag}", $ReleaseTag).Replace("{file}", $FileName)
}

function Get-RelativePath {
    param(
        [string]$BaseDir,
        [string]$Path
    )

    $baseUri = [System.Uri](([System.IO.Path]::GetFullPath($BaseDir).TrimEnd('\') + '\'))
    $pathUri = [System.Uri]([System.IO.Path]::GetFullPath($Path))
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Copy-FilePreservingRelativePath {
    param(
        [string]$SourceFile,
        [string]$DestinationRoot,
        [string]$RelativePath
    )

    $target = Join-Path $DestinationRoot $RelativePath
    $targetParent = Split-Path -Parent $target
    if (-not [string]::IsNullOrWhiteSpace($targetParent)) {
        New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    }

    Copy-Item -LiteralPath $SourceFile -Destination $target -Force
}

function New-AppPatchPackage {
    param(
        [string]$CurrentAppDir,
        [string]$BaselineDir,
        [string]$PatchZipPath,
        [string]$BaselineVersion,
        [string]$LatestVersion,
        [string]$InstallerCmdPath,
        [string]$InstallerScriptPath,
        [switch]$ExcludeCompatibleRuntimes
    )

    if (-not (Test-Path $BaselineDir)) {
        throw "BaselineAppDir does not exist: $BaselineDir"
    }
    foreach ($requiredFile in @($InstallerCmdPath, $InstallerScriptPath)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "AppPatch manual installer source file not found: $requiredFile"
        }
    }

    $patchWorkDir = Join-Path ([System.IO.Path]::GetDirectoryName($PatchZipPath)) ("_patch_work_" + [System.IO.Path]::GetFileNameWithoutExtension($PatchZipPath))
    $patchFilesDir = Join-Path $patchWorkDir "files"
    if (Test-Path $patchWorkDir) {
        Remove-Item -LiteralPath $patchWorkDir -Recurse -Force
    }
    if (Test-Path $PatchZipPath) {
        Remove-Item -LiteralPath $PatchZipPath -Force
    }
    New-Item -ItemType Directory -Force -Path $patchFilesDir | Out-Null

    $changedFiles = @()
    Get-ChildItem -LiteralPath $CurrentAppDir -File -Recurse | ForEach-Object {
        $relativePath = Get-RelativePath -BaseDir $CurrentAppDir -Path $_.FullName
        if ($relativePath.StartsWith("tts_cache\", [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        if ($ExcludeCompatibleRuntimes -and (Test-IsAppPatchManagedRuntimePath -RelativePath $relativePath)) {
            return
        }
        $baselineFile = Join-Path $BaselineDir $relativePath
        $currentHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $isChanged = $true

        if (Test-Path $baselineFile) {
            $baselineHash = (Get-FileHash -LiteralPath $baselineFile -Algorithm SHA256).Hash.ToLowerInvariant()
            $isChanged = -not [string]::Equals($currentHash, $baselineHash, [System.StringComparison]::OrdinalIgnoreCase)
        }

        if ($isChanged) {
            Copy-FilePreservingRelativePath -SourceFile $_.FullName -DestinationRoot $patchFilesDir -RelativePath $relativePath
            $changedFiles += [ordered]@{
                "path" = $relativePath.Replace('\', '/')
                "sha256" = $currentHash
                "size" = $_.Length
            }
        }
    }

    if ($changedFiles.Count -eq 0) {
        Remove-Item -LiteralPath $patchWorkDir -Recurse -Force
        throw "AppPatch package has no changed files. Check BaselineAppDir or disable patch for this release."
    }

    $patchManifest = [ordered]@{}
    $patchManifest["type"] = "baseline_patch"
    $patchManifest["patch_baseline_version"] = $BaselineVersion
    $patchManifest["latest_version"] = $LatestVersion
    $patchManifest["files"] = $changedFiles
    $patchManifest |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $patchWorkDir "patch_manifest.json") -Encoding UTF8

    Copy-NormalizedCommandFile `
        -SourcePath $InstallerCmdPath `
        -DestinationPath (Join-Path $patchWorkDir $appPatchInstallerCmdName)
    Copy-Item -LiteralPath $InstallerScriptPath -Destination (Join-Path $patchWorkDir $appPatchInstallerScriptName) -Force

    $appPatchNotice = @(
        "PackingProof 主程序增量更新"
        ""
        "正常情况下无需手动下载，启动器会自动校验并安装此 AppPatch。"
        "如需手动更新，请完整解压本 ZIP，再双击《$appPatchInstallerCmdName》。"
        ""
        "脚本会校验补丁文件、识别原安装位置并在失败时恢复原文件。"
        "不要直接在压缩软件中运行，也不要单独移动 CMD、$appPatchInstallerScriptName、patch_manifest.json 或 files 文件夹。"
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $patchWorkDir $appPatchNoticeName) -Value $appPatchNotice -Encoding UTF8

    Compress-PackageWithRetry `
        -SourceDir $patchWorkDir `
        -DestinationZip $PatchZipPath `
        -CompressionLevel $ZipCompressionLevel
    Remove-Item -LiteralPath $patchWorkDir -Recurse -Force
}

function Test-ZipContainsEntryPrefix {
    param(
        [string]$ZipFile,
        [string]$EntryPrefix
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $normalizedPrefix = $EntryPrefix.Replace('\', '/')
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Replace('\', '/').StartsWith(
                    $normalizedPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }
    finally {
        $zip.Dispose()
    }
}

function Resolve-LauncherBaselineExecutable {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$PackageUrl
    )

    $baselineVersion = [string]$Baseline.version
    $cacheDirectory = Join-Path $repoRoot "package\launcher-baselines\v$baselineVersion"
    $cachedLauncherPath = Join-Path $cacheDirectory "ExpressPackingMonitoring.exe"
    $cachedPackagePath = Join-Path $cacheDirectory ([string]$Baseline.package.file)
    $legacyPackageRoot = Join-Path $repoRoot "package\PackingProof+v$baselineVersion"
    $launcherCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($BaselineLauncherPath)) {
        $launcherCandidates += [System.IO.Path]::GetFullPath($BaselineLauncherPath)
    }
    $launcherCandidates += @(
        $cachedLauncherPath,
        (Join-Path $legacyPackageRoot "PackingProof+v$baselineVersion\ExpressPackingMonitoring.exe")
    )

    foreach ($candidate in $launcherCandidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        Assert-LauncherFile `
            -Path $candidate `
            -ExpectedSize ([long]$Baseline.package.executable_size) `
            -ExpectedSha256 ([string]$Baseline.package.executable_sha256) `
            -Description "Launcher baseline executable"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
        Copy-Item -LiteralPath $candidate -Destination $DestinationPath -Force
        Write-Host "Launcher baseline reused from executable: $candidate"
        return
    }

    $packageCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($LauncherBaselinePackagePath)) {
        $packageCandidates += [System.IO.Path]::GetFullPath($LauncherBaselinePackagePath)
    }
    $packageCandidates += @(
        $cachedPackagePath,
        (Join-Path $legacyPackageRoot ([string]$Baseline.package.file))
    )
    $resolvedPackagePath = $packageCandidates |
        Select-Object -Unique |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($resolvedPackagePath)) {
        if (-not [System.Uri]::IsWellFormedUriString($PackageUrl, [System.UriKind]::Absolute) -or
            -not $PackageUrl.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Launcher baseline package is unavailable locally and its HTTPS URL is invalid: $PackageUrl"
        }

        New-Item -ItemType Directory -Force -Path $cacheDirectory | Out-Null
        $temporaryPackagePath = Join-Path $cacheDirectory (".launcher-download-" + [Guid]::NewGuid().ToString("N") + ".tmp")
        try {
            Write-Host "Downloading locked launcher baseline: $PackageUrl"
            Invoke-WebRequest `
                -Uri $PackageUrl `
                -OutFile $temporaryPackagePath `
                -Headers @{ "User-Agent" = "PackingProof-ReleaseBuilder" } `
                -MaximumRedirection 5 `
                -TimeoutSec 60
            Assert-LauncherPackage -PackagePath $temporaryPackagePath -Baseline $Baseline
            Move-Item -LiteralPath $temporaryPackagePath -Destination $cachedPackagePath -Force
            $resolvedPackagePath = $cachedPackagePath
        }
        finally {
            if (Test-Path -LiteralPath $temporaryPackagePath -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryPackagePath -Force
            }
        }
    }

    Assert-LauncherPackage -PackagePath $resolvedPackagePath -Baseline $Baseline
    Expand-LauncherBaselinePackage `
        -PackagePath $resolvedPackagePath `
        -DestinationPath $cachedLauncherPath `
        -Baseline $Baseline
    Copy-Item -LiteralPath $cachedLauncherPath -Destination $DestinationPath -Force
    Write-Host "Launcher baseline reused from package: $resolvedPackagePath"
}

$appPublishDir = Join-Path $outputFullPath "app"
$appBuildArtifacts = Join-Path $outputFullPath ".build-artifacts"
$gitCommitId = Get-GitCommitId
$packageUpdateCheckUrl = Get-ConfiguredValue -Key "UPDATE_CHECK_URL" -DefaultValue "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases/latest"
$launcherManifestFullPath = if (-not [string]::IsNullOrWhiteSpace($LauncherBaselineManifestPath)) {
    [System.IO.Path]::GetFullPath($LauncherBaselineManifestPath)
} elseif (-not [string]::IsNullOrWhiteSpace($BaselineLauncherManifestPath)) {
    Write-Warning "BaselineLauncherManifestPath is deprecated; use LauncherBaselineManifestPath."
    [System.IO.Path]::GetFullPath($BaselineLauncherManifestPath)
} else {
    Join-Path $PSScriptRoot "launcher-baseline.json"
}
$launcherBaseline = Read-LauncherBaselineManifest -ManifestPath $launcherManifestFullPath
$launcherSourceFingerprint = Get-LauncherLogicalFingerprint `
    -RepositoryRoot $repoRoot `
    -Runtime $Runtime `
    -UpdateCheckUrl $packageUpdateCheckUrl
if (-not [string]::Equals(
        $launcherSourceFingerprint,
        [string]$launcherBaseline.source_fingerprint,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Launcher logical inputs changed. Run Tools\Publish-LauncherBaseline.ps1 before packaging."
}
if (-not [string]::Equals([string]$launcherBaseline.runtime, $Runtime, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$launcherBaseline.update_check_url, $packageUpdateCheckUrl, [System.StringComparison]::Ordinal)) {
    throw "Launcher baseline runtime or embedded update URL does not match the current release configuration."
}
$launcherTag = [string]$launcherBaseline.tag
& git -C $repoRoot rev-parse --verify --quiet "$launcherTag^{commit}" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Launcher component tag is missing: $launcherTag"
}
$launcherFingerprintFiles = @(Get-LauncherFingerprintFiles)
& git -C $repoRoot diff --quiet "$launcherTag^{commit}" HEAD -- @launcherFingerprintFiles
if ($LASTEXITCODE -ne 0) {
    throw "Launcher tracked files changed after $launcherTag. Establish a new launcher baseline."
}

New-DefaultTtsCache

Stop-StaleReleaseAppInstances
Invoke-DotNetPublish -Arguments @(
    $appProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:InformationalVersion=$packageVersion",
    "-p:GitCommitId=$gitCommitId",
    "-p:PublishSingleFile=false",
    "--artifacts-path", $appBuildArtifacts,
    "-o", $appPublishDir
)
Remove-Item -LiteralPath $appBuildArtifacts -Recurse -Force

# Win10/11 标准包附带 Windows 系统语音（WinRT）辅助程序集；
# Win7 兼容变体在下方产物生成后剔除这些文件。
$winttsProject = Join-Path $repoRoot "ExpressPackingMonitoring.WinTts\ExpressPackingMonitoring.WinTts.csproj"
$winttsPublishDir = Join-Path $repoRoot "package\.wintts-publish-tmp"
Invoke-DotNetPublish -Arguments @(
    $winttsProject,
    "-c", $Configuration,
    "--nologo",
    "-o", $winttsPublishDir
)
foreach ($winttsFile in @("ExpressPackingMonitoring.WinTts.dll", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll")) {
    $winttsSource = Join-Path $winttsPublishDir $winttsFile
    if (-not (Test-Path -LiteralPath $winttsSource -PathType Leaf)) {
        throw "WinTts publish output missing: $winttsSource"
    }
    Copy-Item -LiteralPath $winttsSource -Destination (Join-Path $appPublishDir $winttsFile) -Force
}
Remove-Item -LiteralPath $winttsPublishDir -Recurse -Force

$ffmpegBaseline = Read-FFmpegBaselineManifest -ManifestPath $ffmpegBaselineManifestPath
$ffmpegCacheDirectory = Join-Path $repoRoot "package\dependency-cache\ffmpeg\$($ffmpegBaseline.version)"
$sevenZipExecutable = Resolve-SevenZipExecutable
Resolve-FFmpegBaselineExecutable `
    -Baseline $ffmpegBaseline `
    -CacheDirectory $ffmpegCacheDirectory `
    -DestinationPath (Join-Path $appPublishDir "tools\ffmpeg.exe") `
    -SevenZipExecutable $sevenZipExecutable

$launcherExe = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
$baselineReleaseTag = [string]$launcherBaseline.release_tag
$baselinePackageName = [string]$launcherBaseline.package.file
$baselineLauncherPackageUrlTemplate = Get-ConfiguredValue `
    -Key "LAUNCHER_PACKAGE_URL_TEMPLATE" `
    -DefaultValue "$(Get-ReleaseUrlBase)/download/{tag}/{file}"
$baselineLauncherPackageUrl = Expand-ReleaseTemplate `
    -Template $baselineLauncherPackageUrlTemplate `
    -ReleaseTag $baselineReleaseTag `
    -FileName $baselinePackageName
Resolve-LauncherBaselineExecutable `
    -Baseline $launcherBaseline `
    -DestinationPath $launcherExe `
    -PackageUrl $baselineLauncherPackageUrl

Get-ChildItem -LiteralPath $outputFullPath -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".pdb", ".dbg" } |
    Remove-Item -Force

Remove-PackageRuntimeState -AppDir $appPublishDir
Copy-PackageTtsCache -AppDir $appPublishDir
$publishedTtsCacheFiles = @(Get-ChildItem -LiteralPath (Join-Path $appPublishDir "tts_cache") -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".mp3", ".wav" })
if (-not $SkipTtsCacheGeneration -and $publishedTtsCacheFiles.Count -eq 0) {
    throw "Clean package validation failed: default TTS cache is empty"
}

$launcherExe = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
$appExe = Join-Path $appPublishDir "ExpressPackingMonitoring.exe"
$requiredAppRuntimeFiles = @(
    "zxing.dll",
    "OpenCvSharp.dll",
    "OpenCvSharp.WpfExtensions.dll",
    "OpenCvSharpExtern.dll"
)
if (-not (Test-Path $launcherExe)) {
    throw "Clean package validation failed: missing root launcher"
}
if (-not (Test-Path $appExe)) {
    throw "Clean package validation failed: missing app\ExpressPackingMonitoring.exe"
}
foreach ($runtimeFile in $requiredAppRuntimeFiles) {
    if (-not (Test-Path (Join-Path $appPublishDir $runtimeFile))) {
        throw "Clean package validation failed: missing camera barcode runtime dependency app\$runtimeFile"
    }
}

$normalizedVersion = Get-NormalizedReleaseVersion $packageVersion
$normalizedPatchBaselineVersion = Get-NormalizedReleaseVersion $PatchBaselineVersion
$releaseTag = "v$normalizedVersion"
$packageRoot = $packageArtifactRoot
$legacyAppFullZipPath = Join-Path $packageRoot "ExpressPackingMonitoring_AppFull_$releaseTag.zip"
$appPatchZipName = "ExpressPackingMonitoring_AppPatch_$releaseTag.zip"
$appPatchZipPath = Join-Path $packageRoot $appPatchZipName
$legacyManualUpdateZipPath = Join-Path $packageRoot "PackingProof_ManualUpdate_$releaseTag.zip"
$launcherPackageName = [string]$launcherBaseline.package.file
$launcherPackagePath = Join-Path $packageRoot $launcherPackageName
$updateJsonName = "update_$releaseTag.json"
$updateJsonPath = Join-Path $packageRoot $updateJsonName
$launcherManifestName = "launcher_manifest_$releaseTag.json"
$launcherManifestPath = Join-Path $packageRoot $launcherManifestName
$releaseInfoName = "release_info_$releaseTag.txt"
$releaseInfoPath = Join-Path $packageRoot $releaseInfoName
$setupFileName = "PackingProof_Setup_$releaseTag.exe"
$setupPath = Join-Path $packageRoot $setupFileName
$releaseUrlBase = Get-ReleaseUrlBase
$releasePageTemplate = Get-ConfiguredValue -Key "RELEASE_PAGE_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/tag/{tag}"
$appPatchUrlTemplate = Get-ConfiguredValue -Key "APP_PATCH_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/download/{tag}/{file}"
$appPatchGithubUrlTemplate = Get-ConfiguredValue -Key "APP_PATCH_GITHUB_URL_TEMPLATE" -DefaultValue "https://github.com/PackingProof/PackingProof-Desktop/releases/download/{tag}/{file}"
$appPatchGiteeUrlTemplate = Get-ConfiguredValue -Key "APP_PATCH_GITEE_URL_TEMPLATE" -DefaultValue "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/{tag}/{file}"
$launcherPackageUrlTemplate = Get-ConfiguredValue -Key "LAUNCHER_PACKAGE_URL_TEMPLATE" -DefaultValue "$releaseUrlBase/download/{tag}/{file}"
$launcherPackageGithubUrlTemplate = Get-ConfiguredValue -Key "LAUNCHER_PACKAGE_GITHUB_URL_TEMPLATE" -DefaultValue "https://github.com/PackingProof/PackingProof-Desktop/releases/download/{tag}/{file}"
$launcherPackageGiteeUrlTemplate = Get-ConfiguredValue -Key "LAUNCHER_PACKAGE_GITEE_URL_TEMPLATE" -DefaultValue "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/{tag}/{file}"
$releasePage = Expand-ReleaseTemplate -Template $releasePageTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$appPatchPlaceholderUrl = Expand-ReleaseTemplate -Template $appPatchUrlTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$appPatchGithubUrl = Expand-ReleaseTemplate -Template $appPatchGithubUrlTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$appPatchGiteeUrl = Expand-ReleaseTemplate -Template $appPatchGiteeUrlTemplate -ReleaseTag $releaseTag -FileName $appPatchZipName
$launcherPackagePlaceholderUrl = Expand-ReleaseTemplate -Template $launcherPackageUrlTemplate -ReleaseTag ([string]$launcherBaseline.release_tag) -FileName $launcherPackageName
$launcherPackageGithubUrl = Expand-ReleaseTemplate -Template $launcherPackageGithubUrlTemplate -ReleaseTag ([string]$launcherBaseline.release_tag) -FileName $launcherPackageName
$launcherPackageGiteeUrl = Expand-ReleaseTemplate -Template $launcherPackageGiteeUrlTemplate -ReleaseTag ([string]$launcherBaseline.release_tag) -FileName $launcherPackageName
$fullDownloadPageTemplate = Get-ConfiguredValue `
    -Key "FULL_DOWNLOAD_PRIMARY_PAGE_URL_TEMPLATE" `
    -DefaultValue "https://github.com/PackingProof/PackingProof-Desktop/releases/tag/{tag}"
$fullDownloadPage = Expand-ReleaseTemplate -Template $fullDownloadPageTemplate -ReleaseTag $releaseTag -FileName (Split-Path -Leaf $zipFullPath)
$fullDownloadFallbackPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_FALLBACK_PAGE_URL_TEMPLATE" -DefaultValue ""
if ([string]::IsNullOrWhiteSpace($fullDownloadFallbackPageTemplate)) {
    $fullDownloadFallbackPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_PAGE" -DefaultValue ""
}
if ([string]::IsNullOrWhiteSpace($fullDownloadFallbackPageTemplate)) {
    $fullDownloadFallbackPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_PAGE_URL_TEMPLATE" -DefaultValue $releasePage
}
$fullDownloadFallbackPage = Expand-ReleaseTemplate -Template $fullDownloadFallbackPageTemplate -ReleaseTag $releaseTag -FileName (Split-Path -Leaf $zipFullPath)

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
if (Test-Path $legacyAppFullZipPath) {
    Remove-Item -LiteralPath $legacyAppFullZipPath -Force
}
if (Test-Path $appPatchZipPath) {
    Remove-Item -LiteralPath $appPatchZipPath -Force
}
if (Test-Path $legacyManualUpdateZipPath) {
    Remove-Item -LiteralPath $legacyManualUpdateZipPath -Force
}
$launcherExecutableHash = ([string]$launcherBaseline.package.executable_sha256).ToLowerInvariant()
$launcherExecutableSize = [long]$launcherBaseline.package.executable_size
$launcherPackageHash = ([string]$launcherBaseline.package.sha256).ToLowerInvariant()
$launcherPackageSize = [long]$launcherBaseline.package.size
Assert-LauncherFile `
    -Path $launcherExe `
    -ExpectedSize $launcherExecutableSize `
    -ExpectedSha256 $launcherExecutableHash `
    -Description "Packaged launcher executable"
$launcherReleaseTagMatches = [string]::Equals(
    [string]$launcherBaseline.release_tag,
    $releaseTag,
    [System.StringComparison]::OrdinalIgnoreCase)
$launcherPublishedWithRelease = $launcherReleaseTagMatches -and -not $ReuseExistingLauncherBaseline
if ($ReuseExistingLauncherBaseline) {
    if (-not $launcherReleaseTagMatches) {
        throw "ReuseExistingLauncherBaseline is only valid when the app release tag matches the locked launcher release tag."
    }
    & git -C $repoRoot rev-parse --verify --quiet "$releaseTag^{commit}" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "ReuseExistingLauncherBaseline requires an existing app release tag: $releaseTag"
    }
}
if ($launcherPublishedWithRelease) {
    $cachedBaselinePackage = Join-Path $repoRoot "package\launcher-baselines\$releaseTag\$launcherPackageName"
    Assert-LauncherPackage -PackagePath $cachedBaselinePackage -Baseline $launcherBaseline
    if (-not [string]::Equals($cachedBaselinePackage, $launcherPackagePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $cachedBaselinePackage -Destination $launcherPackagePath -Force
    }
    Assert-LauncherPackage -PackagePath $launcherPackagePath -Baseline $launcherBaseline
}
if (-not (Test-Path -LiteralPath $installerBuildScript -PathType Leaf)) {
    throw "Installer build script not found: $installerBuildScript"
}
& $installerBuildScript `
    -SourceDir $outputFullPath `
    -Version $normalizedVersion `
    -OutputDir $packageRoot `
    -InstallerCompression $InstallerCompression
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer build failed: $setupPath"
}
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$setupSize = (Get-Item -LiteralPath $setupPath).Length
$setupSignatureStatus = (Get-AuthenticodeSignature -LiteralPath $setupPath).Status.ToString()

$patchSupported = $false
$patchReason = ""
$appPatchHash = ""
$appPatchSize = 0
$launcherChanged = $launcherPublishedWithRelease
$launcherCheckInfo = if ($launcherPublishedWithRelease) {
    "Launcher baseline $($launcherBaseline.tag) is published with this release."
} else {
    "Launcher baseline $($launcherBaseline.tag) is reused without rebuilding."
}
$launcherManifest = [ordered]@{
    app_release_version = $normalizedVersion
    launcher_baseline = $launcherBaseline
    launcher_package_url = $launcherPackagePlaceholderUrl
    published_with_release = $launcherPublishedWithRelease
}
$launcherManifest |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $launcherManifestPath -Encoding UTF8

if ($DisablePatch) {
    $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77ya5bey5Lyg5YWlIERpc2FibGVQYXRjaOOAgg=="
}
elseif ([string]::IsNullOrWhiteSpace($BaselineAppDir) -or -not (Test-Path $BaselineAppDir)) {
    $patchReason = ConvertFrom-Utf8Base64 "5pyq55Sf5oiQ5aKe6YeP5YyF77ya5pyq5Lyg5YWlIEJhc2VsaW5lQXBwRGlyIOaIlui3r+W+hOS4jeWtmOWcqOOAgg=="
}
else {
    $baselineAppFullPath = [System.IO.Path]::GetFullPath($BaselineAppDir)
    $runtimeCompatibility = Test-AppPatchRuntimeCompatibility `
        -CurrentAppDir $appPublishDir `
        -BaselineAppDir $baselineAppFullPath `
        -FFmpegBaseline $ffmpegBaseline
    if (-not $runtimeCompatibility.Compatible) {
        $patchReason = "未生成增量包：$($runtimeCompatibility.Reason)"
    }
    else {
        New-AppPatchPackage `
            -CurrentAppDir $appPublishDir `
            -BaselineDir $baselineAppFullPath `
            -PatchZipPath $appPatchZipPath `
            -BaselineVersion $normalizedPatchBaselineVersion `
            -LatestVersion $normalizedVersion `
            -InstallerCmdPath $appPatchCmdSource `
            -InstallerScriptPath $appPatchScriptSource `
            -ExcludeCompatibleRuntimes

        if (-not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "patch_manifest.json")) {
            throw "AppPatch package validation failed: missing patch_manifest.json"
        }
        foreach ($appPatchEntry in @(
            $appPatchInstallerCmdName,
            $appPatchInstallerScriptName,
            $appPatchNoticeName)) {
            if (-not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName $appPatchEntry)) {
                throw "AppPatch package validation failed: missing $appPatchEntry"
            }
        }
        foreach ($runtimeFile in $requiredAppRuntimeFiles) {
            $baselineRuntimeFile = Join-Path $baselineAppFullPath $runtimeFile
            $currentRuntimeFile = Join-Path $appPublishDir $runtimeFile
            $runtimeChanged = -not (Test-Path $baselineRuntimeFile)
            if (-not $runtimeChanged) {
                $baselineRuntimeHash = (Get-FileHash -LiteralPath $baselineRuntimeFile -Algorithm SHA256).Hash
                $currentRuntimeHash = (Get-FileHash -LiteralPath $currentRuntimeFile -Algorithm SHA256).Hash
                $runtimeChanged = -not [string]::Equals($baselineRuntimeHash, $currentRuntimeHash, [System.StringComparison]::OrdinalIgnoreCase)
            }
            if ($runtimeChanged -and -not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "files/$runtimeFile")) {
                throw "AppPatch package validation failed: missing changed camera barcode runtime dependency files/$runtimeFile"
            }
        }
        if (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName 'files/tools/ffmpeg.exe') {
            throw 'AppPatch package validation failed: compatible FFmpeg was included'
        }
        if (Test-ZipContainsEntryPrefix -ZipFile $appPatchZipPath -EntryPrefix 'files/libvlc/') {
            throw 'AppPatch package validation failed: compatible LibVLC was included'
        }
        if ($launcherChanged -and -not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "files/ExpressPackingMonitoring.dll")) {
            throw "AppPatch bridge validation failed: launcher changed but updated app assembly is missing"
        }

        $appPatchHash = (Get-FileHash -LiteralPath $appPatchZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $appPatchSize = (Get-Item -LiteralPath $appPatchZipPath).Length
        $patchSupported = $true
        $patchReason = "已生成兼容基线精简增量包：$appPatchZipName；$($runtimeCompatibility.Reason)"
    }
}

if ($launcherPublishedWithRelease -and -not $patchSupported) {
    throw "A new launcher baseline requires a compatible AppPatch bridge in the same release."
}

$updateManifest = [ordered]@{}
$updateManifest["latest_version"] = $normalizedVersion
$updateManifest["title"] = ConvertFrom-Utf8Base64 "6K+35aGr5YaZ5pu05paw5qCH6aKY"
$updateManifest["release_page"] = $releasePage
$updateManifest["patch_baseline_version"] = $normalizedPatchBaselineVersion
$updateManifest["patch_supported"] = $patchSupported
$launcherPackageInfo = [ordered]@{}
$launcherPackageInfo["protocol_version"] = [int]$launcherBaseline.protocol_version
$launcherPackageInfo["version"] = [string]$launcherBaseline.version
$launcherPackageInfo["url"] = $launcherPackagePlaceholderUrl
$launcherPackageInfo["github_url"] = $launcherPackageGithubUrl
$launcherPackageInfo["gitee_url"] = $launcherPackageGiteeUrl
$launcherPackageInfo["size"] = $launcherPackageSize
$launcherPackageInfo["sha256"] = $launcherPackageHash
$launcherPackageInfo["executable_size"] = $launcherExecutableSize
$launcherPackageInfo["executable_sha256"] = $launcherExecutableHash
$updateManifest["launcher_package"] = $launcherPackageInfo
if ($patchSupported) {
    $patchPackageInfo = [ordered]@{}
    $patchPackageInfo["type"] = "baseline_patch"
    $patchPackageInfo["url"] = $appPatchPlaceholderUrl
    $patchPackageInfo["github_url"] = $appPatchGithubUrl
    $patchPackageInfo["gitee_url"] = $appPatchGiteeUrl
    $patchPackageInfo["sha256"] = $appPatchHash
    $patchPackageInfo["size"] = $appPatchSize
    $updateManifest["patch_package"] = $patchPackageInfo
    $updateManifest["notes"] = @(
        "# 快递打包监控 v$normalizedVersion`n`n## 更新内容`n### 功能与体验`n- 请填写`n`n### 问题修复`n- 请填写`n`n### 兼容与工程`n- 请填写`n`n## 下载与更新说明`n- Setup：$setupFileName（未签名时注明 SmartScreen 提示）`n- 完整包 7z / ZIP：免安装，用于系统原生解压和故障恢复`n- 已安装用户：启动器会自动下载 AppPatch；如需手动更新，可完整解压 AppPatch 后双击包内更新脚本`n`n## 未验证事项`n- 请填写"
        "启动器会自动下载 AppPatch；如需手动更新，可完整解压 AppPatch 后双击包内更新脚本"
        "主程序会按锁定基线检查启动器；仅启动器真实变化时下载独立 LauncherPatch"
        "首次安装建议从完整下载页获取《$setupFileName》；完整 7z 是小体积免安装包，ZIP 用于系统原生解压和故障恢复"
    )
}
else {
    $updateManifest["patch_package"] = $null
    $updateManifest["notes"] = @(
        "本版本不支持自动增量更新，请下载《$setupFileName》完成升级"
        "完整 7z 是小体积免安装包，ZIP 用于系统原生解压和故障恢复"
    )
}
$updateManifest["full_download_page"] = $fullDownloadPage
$updateManifest["full_download_fallback_page"] = $fullDownloadFallbackPage

$updateManifest |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $updateJsonPath -Encoding UTF8

$zipParent = Split-Path -Parent $zipFullPath
if (-not [string]::IsNullOrWhiteSpace($zipParent)) {
    New-Item -ItemType Directory -Force -Path $zipParent | Out-Null
}
Compress-PackageWithRetry `
    -SourceDir $outputFullPath `
    -DestinationZip $zipFullPath `
    -CompressionLevel $ZipCompressionLevel
Compress-Package7zWithRetry `
    -SourceDir $outputFullPath `
    -DestinationArchive $sevenZipFullPath `
    -SevenZipExecutable $sevenZipExecutable `
    -CompressionLevel $SevenZipCompressionLevel

if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "ExpressPackingMonitoring.exe")) {
    throw "Full zip validation failed: missing root launcher"
}
if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/ExpressPackingMonitoring.exe")) {
    throw "Full zip validation failed: missing app/ExpressPackingMonitoring.exe"
}
if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "ExpressPackingMonitoring.exe" -SevenZipExecutable $sevenZipExecutable)) {
    throw "Full 7z validation failed: missing root launcher"
}
if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "app/ExpressPackingMonitoring.exe" -SevenZipExecutable $sevenZipExecutable)) {
    throw "Full 7z validation failed: missing app/ExpressPackingMonitoring.exe"
}
foreach ($runtimeFile in $requiredAppRuntimeFiles) {
    if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/$runtimeFile")) {
        throw "Full zip validation failed: missing camera barcode runtime dependency app/$runtimeFile"
    }
    if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "app/$runtimeFile" -SevenZipExecutable $sevenZipExecutable)) {
        throw "Full 7z validation failed: missing camera barcode runtime dependency app/$runtimeFile"
    }
}
$sevenZipHash = (Get-FileHash -LiteralPath $sevenZipFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sevenZipSize = (Get-Item -LiteralPath $sevenZipFullPath).Length
$fullZipHash = (Get-FileHash -LiteralPath $zipFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$fullZipSize = (Get-Item -LiteralPath $zipFullPath).Length

$patchReleaseInfo = if ($patchSupported) { $appPatchZipName } else { $patchReason }
$releaseInfoCheckLine = (ConvertFrom-Utf8Base64 "5LiK5Lyg5ZCO6K+35qOA5p+lIA==") + $updateJsonName + (ConvertFrom-Utf8Base64 "IOmHjOeahCBwYXRjaF9wYWNrYWdlLnVybCDmmK/lkKbkuI4gUmVsZWFzZSDpmYTku7bkuIvovb3lnLDlnYDkuIDoh7TjgII=")
$patchBaselineInfoLine = (ConvertFrom-Utf8Base64 "UGF0Y2gg5Z+657q/54mI5pys77ya") + $normalizedPatchBaselineVersion
$releaseInfoLines = @()
$releaseInfoLines += ConvertFrom-Utf8Base64 "UmVsZWFzZSDkuIrkvKDmuIXljZU="
$releaseInfoLines += ""
$releaseInfoLines += (ConvertFrom-Utf8Base64 "54mI5pys77ya") + $releaseTag
$releaseInfoLines += (ConvertFrom-Utf8Base64 "UmVsZWFzZSDpobXpnaLvvJo=") + $releasePage
$releaseInfoLines += "Full download page: " + $fullDownloadPage
$releaseInfoLines += "Full download fallback page: " + $fullDownloadFallbackPage
$releaseInfoLines += ""
$releaseInfoLines += "GitHub 默认上传："
$releaseInfoLines += "1. Windows 安装向导（推荐）：" + $setupFileName
$releaseInfoLines += "2. 完整包 7z（小体积免安装）：" + (Split-Path -Leaf $sevenZipFullPath)
$releaseInfoLines += "3. 完整包 ZIP（系统原生解压/故障恢复）：" + (Split-Path -Leaf $zipFullPath)
if ($patchSupported) {
    $releaseInfoLines += "4. AppPatch（自动更新；包内也可双击手动更新）：" + $patchReleaseInfo
    if ($launcherPublishedWithRelease) {
        $releaseInfoLines += "5. LauncherPatch（本版本建立新启动器基线）：" + $launcherPackageName
        $releaseInfoLines += "6. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
    }
    else {
        $releaseInfoLines += "5. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
        $releaseInfoLines += "启动器沿用 $($launcherBaseline.tag)，本版本不要重复上传 LauncherPatch"
    }
}
else {
    $releaseInfoLines += "4. 本版本不提供增量包：" + $patchReason
    $releaseInfoLines += "5. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
}
$releaseInfoLines += ""
$releaseInfoLines += "Gitee 命令行上传："
if ($patchSupported) {
    $releaseInfoLines += "1. AppPatch（自动/手动主程序更新）：" + $patchReleaseInfo
    if ($launcherPublishedWithRelease) {
        $releaseInfoLines += "2. LauncherPatch（本版本建立新启动器基线）：" + $launcherPackageName
        $releaseInfoLines += "3. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
    }
    else {
        $releaseInfoLines += "2. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
        $releaseInfoLines += "启动器沿用 $($launcherBaseline.tag)，本版本不要重复上传 LauncherPatch"
    }
}
else {
    $releaseInfoLines += "1. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
}
$releaseInfoLines += "Setup、完整 7z 和完整 ZIP 使用 Full download page，不上传到 Gitee"
$releaseInfoLines += "Local verification only (do not upload by default): " + $launcherManifestName
$releaseInfoLines += ""
$releaseInfoLines += "Setup SHA256:"
$releaseInfoLines += $setupHash
$releaseInfoLines += "Setup size: $setupSize bytes"
$releaseInfoLines += "Setup Authenticode status: $setupSignatureStatus"
if (-not [string]::Equals($setupSignatureStatus, "Valid", [System.StringComparison]::OrdinalIgnoreCase)) {
    $releaseInfoLines += "WARNING: Setup is unsigned; Windows SmartScreen may show an unknown publisher warning."
}
$releaseInfoLines += ""
$releaseInfoLines += "Full 7z SHA256:"
$releaseInfoLines += $sevenZipHash
$releaseInfoLines += "Full 7z size: $sevenZipSize bytes"
$releaseInfoLines += ""
$releaseInfoLines += "Full ZIP SHA256:"
$releaseInfoLines += $fullZipHash
$releaseInfoLines += "Full ZIP size: $fullZipSize bytes"
$releaseInfoLines += ""
$releaseInfoLines += $releaseInfoCheckLine
$releaseInfoLines += ""
$releaseInfoLines += $launcherCheckInfo
$releaseInfoLines += ""
$releaseInfoLines += $patchBaselineInfoLine
if ($patchSupported) {
    $releaseInfoLines += "AppPatch SHA256:"
    $releaseInfoLines += $appPatchHash
    $releaseInfoLines += ""
    $releaseInfoLines += "AppPatch size:"
    $releaseInfoLines += "$appPatchSize bytes"
}
$releaseInfo = $releaseInfoLines -join [Environment]::NewLine
$releaseInfo | Set-Content -LiteralPath $releaseInfoPath -Encoding UTF8

Write-Host "Clean package created: $outputFullPath"
Write-Host "Installer created: $setupPath"
Write-Host "7z package created: $sevenZipFullPath"
Write-Host "Zip package created: $zipFullPath"
if ($patchSupported) {
    Write-Host "AppPatch package created: $appPatchZipPath"
}
else {
    Write-Host "AppPatch package skipped: $patchReason"
}
Write-Host "Update manifest created: $updateJsonPath"
Write-Host "Release info created: $releaseInfoPath"
Write-Host "Root items:"
Get-ChildItem -LiteralPath $outputFullPath | Sort-Object PSIsContainer, Name | Select-Object Name, Mode, Length | Format-Table -AutoSize
