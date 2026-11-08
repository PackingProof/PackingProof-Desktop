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
    [string]$PatchBaselineVersion = "0.0.84",
    [ValidateSet("gitee", "github", "auto")]
    [string]$DownloadSource = "github",
    [switch]$IncludeFullZip,
    [switch]$IncludeSevenZip,
    [switch]$SkipTtsCacheGeneration,
    [switch]$ConfirmManualCoreChecks,
    [switch]$ReuseExistingLauncherBaseline,
    [switch]$DisablePatch,
    [switch]$SkipStorePackage,
    [ValidateSet("include", "exclude")]
    [string]$VisionObb = "include",
    [switch]$AllowExperimentalOpenCvStore,
    [string]$StoreIdentityConfigPath = "",
    [string]$OpenCvNativeDllPath = "",
    # 只在“老装机升级到共享运行时布局”的那一版打开：增量包里额外带上整份 runtime\。
    [switch]$IncludeRuntimeInPatch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "ReleaseVersion.Common.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.Common.ps1")
. (Join-Path $PSScriptRoot "LauncherBaseline.Common.ps1")
. (Join-Path $PSScriptRoot "FFmpegBaseline.Common.ps1")
. (Join-Path $PSScriptRoot "OpenCvBaseline.Common.ps1")
. (Join-Path $PSScriptRoot "VisionModelBaseline.Common.ps1")
. (Join-Path $PSScriptRoot "AppPatchRuntimeCompatibility.Common.ps1")
. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")
$appProject = Join-Path $repoRoot "src\ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$releaseValidationScript = Join-Path $repoRoot "tools\Test-Release.ps1"
$installerBuildScript = Join-Path $repoRoot "tools\Build-Installer.ps1"
$ttsCacheBuilderProject = Join-Path $repoRoot "tools\ExpressPackingMonitoring.TtsCacheBuilder\ExpressPackingMonitoring.TtsCacheBuilder.csproj"
$appPatchCmdSource = Join-Path $repoRoot "tools\Install-AppPatch.cmd"
$appPatchScriptSource = Join-Path $repoRoot "tools\Apply-AppPatch.ps1"
$appPatchInstallerCmdName = "双击更新主程序.cmd"
$appPatchInstallerScriptName = "apply_app_patch.ps1"
$appPatchNoticeName = "主程序更新说明.txt"
$launcherPatchCmdSource = Join-Path $repoRoot "tools\Install-LauncherPatch.cmd"
$launcherPatchScriptSource = Join-Path $repoRoot "tools\Apply-LauncherPatch.ps1"
$launcherPatchInstallerCmdName = "双击更新启动器.cmd"
$launcherPatchInstallerScriptName = "apply_launcher_patch.ps1"
$launcherPatchManifestName = "launcher_patch_manifest.json"
$launcherPatchNoticeName = "启动器更新说明.txt"
$ffmpegBaselineManifestPath = Join-Path $PSScriptRoot "ffmpeg-baseline.json"
$openCvBaselineManifestPath = Join-Path $PSScriptRoot "opencv-baseline.json"
$visionModelBaselineManifestPath = Join-Path $PSScriptRoot "vision-model-baseline.json"
$openCvBuilderPath = Join-Path $PSScriptRoot "Build-MinimalOpenCv.ps1"

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
# 只处理路径位于仓库开发构建目录（src\ExpressPackingMonitoring\bin\Release 或 package 下 .build-artifacts）
# 的进程，绝不触碰 %LOCALAPPDATA% 等已安装版本。
function Stop-StaleReleaseAppInstances {
    $releaseBinRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $repoRoot "src\ExpressPackingMonitoring\bin\Release"))
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

function Get-GitBuildSuffix {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReleaseTag,
        [string]$CommitId
    )

    $worktreeStatus = @(& git -C $repoRoot status --porcelain 2>$null)
    $isDirty = $LASTEXITCODE -ne 0 -or $worktreeStatus.Count -gt 0
    $tagsAtHead = @(& git -C $repoRoot tag --points-at HEAD 2>$null)
    $isReleaseTagAtHead = $LASTEXITCODE -eq 0 -and $tagsAtHead -contains $ReleaseTag

    if ($isReleaseTagAtHead) {
        return $(if ($isDirty) { "-dirty" } else { "" })
    }

    $description = (& git -C $repoRoot describe --tags --match "v[0-9]*" --long --abbrev=8 HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and
        "$description" -match '-(?<count>\d+)-g(?<commit>[0-9a-fA-F]+)$') {
        $dirtySuffix = if ($isDirty) { "-dirty" } else { "" }
        return "-$($Matches['count'])-g$($Matches['commit'].ToLowerInvariant())$dirtySuffix"
    }

    if ([string]::IsNullOrWhiteSpace($CommitId)) {
        return $(if ($isDirty) { "-dirty" } else { "" })
    }

    $commitCount = (& git -C $repoRoot rev-list --count HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or "$commitCount" -notmatch '^\d+$') {
        $commitCount = "0"
    }
    $shortCommitId = $CommitId.Substring(0, [Math]::Min(8, $CommitId.Length)).ToLowerInvariant()
    $dirtySuffix = if ($isDirty) { "-dirty" } else { "" }
    return "-$commitCount-g$shortCommitId$dirtySuffix"
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

function Get-BaselineVersionFromPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $versions = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches($fullPath, '[+\\/](v?\d+\.\d+\.\d+)')) {
        $null = $versions.Add((Get-NormalizedReleaseVersion $match.Groups[1].Value))
    }

    return ,@($versions)
}

function Resolve-EffectivePatchBaseline {
    param(
        [string]$BaselineAppDir,
        [string]$PatchBaselineVersion,
        [switch]$Explicit
    )

    $versions = Get-BaselineVersionFromPath -Path $BaselineAppDir
    if ($null -eq $versions) {
        $versions = @()
    }
    $explicitVersion = if ($Explicit) {
        Get-NormalizedReleaseVersion $PatchBaselineVersion
    }
    else {
        ""
    }

    if ($versions.Count -eq 0) {
        if ($Explicit) {
            return @{
                EffectiveVersion = $explicitVersion
                FromPath = $false
            }
        }
        if ([string]::IsNullOrWhiteSpace($BaselineAppDir)) {
            return @{
                EffectiveVersion = (Get-NormalizedReleaseVersion $PatchBaselineVersion)
                FromPath = $false
            }
        }
        throw "无法从 -BaselineAppDir 识别基线版本号，且未显式指定 -PatchBaselineVersion；为避免声明基线与实际差异基线不一致，必须显式指定。"
    }

    if ($versions.Count -eq 1) {
        $effectiveVersion = @($versions)[0]
        if ($Explicit -and -not [string]::Equals(
                $explicitVersion,
                $effectiveVersion,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "实际基线目录版本 $effectiveVersion 与 -PatchBaselineVersion $explicitVersion 不一致；AppPatch 的 patch_baseline_version 必须与实际差异基线一致。"
        }
        return @{
            EffectiveVersion = $effectiveVersion
            FromPath = $true
        }
    }

    if ($Explicit -and $versions -contains $explicitVersion) {
        return @{
            EffectiveVersion = $explicitVersion
            FromPath = $true
        }
    }

    throw "基线目录包含多个版本号（$($versions -join ', ')），无法自动确定实际基线；请显式指定与目录一致的 -PatchBaselineVersion。"
}

$packageVersion = Get-PackageVersion
$normalizedVersion = Get-NormalizedReleaseVersion $packageVersion
$releaseTag = "v$normalizedVersion"
$gitCommitId = Get-GitCommitId
$buildIdentitySuffix = Get-GitBuildSuffix -ReleaseTag $releaseTag -CommitId $gitCommitId
$artifactVersion = "$normalizedVersion$buildIdentitySuffix"
$informationalVersion = $artifactVersion
$packageName = ConvertTo-SafePathName "PackingProof+v$artifactVersion"
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

# 同版本重打时，在构建 Setup 之前就让旧 Store 清单失效。否则构建中途失败、
# 或跳过 Store 打包时，旧清单仍可能与旧 MSIX/旧 sibling 一起通过发布门禁。
$storeManifestPath = Join-Path $packageArtifactRoot "store_package_$releaseTag.json"
if (Test-Path -LiteralPath $storeManifestPath) {
    Remove-Item -LiteralPath $storeManifestPath -Force
}

$patchBaselineExplicit = $PSBoundParameters.ContainsKey("PatchBaselineVersion")
$patchBaselineResolution = Resolve-EffectivePatchBaseline `
    -BaselineAppDir $BaselineAppDir `
    -PatchBaselineVersion $PatchBaselineVersion `
    -Explicit:$patchBaselineExplicit
$normalizedPatchBaselineVersion = $patchBaselineResolution.EffectiveVersion
if ([version]$normalizedVersion -ge [version]'0.0.86' -and
    [version]$normalizedPatchBaselineVersion -lt [version]'0.0.84') {
    throw "0.0.86 及以后正式版的补丁基线不得低于 0.0.84；当前版本 $normalizedVersion 使用了 $normalizedPatchBaselineVersion。请使用 -PatchBaselineVersion 0.0.84 和对应的 v0.0.84 基线目录。"
}

Invoke-CoreRegressionTests
if (-not $ConfirmManualCoreChecks) {
    Write-Warning "Manual core business and recovery checks are not confirmed. Packaging will continue; review docs/development/RELEASE_FIELD_CHECKS.md; packaging continues without them."
}

# 重新打包会重建产物目录，发布笔记是人工写的，先留着别丢。
# 产物目录名带提交后缀（-2-g<sha> / -dirty），重打一次就换一个目录名，所以不能只认本次目录：
# 会在本次目录、版本稳定位置、同版本历史产物目录里找最新的一份人工笔记（模板骨架不算）。
$releaseNotesFileName = Get-ReleaseNotesFileName -NormalizedVersion $normalizedVersion
# 人工笔记写在版本产物目录（package\PackingProof+vX.Y.Z\）：发布脚本 Publish-Releases.ps1
# 就是从这个位置读取的；写进内层"干净包"目录它找不到，会误报"发布笔记不存在"。
$releaseNotesDir = Split-Path -Parent $outputFullPath
$releaseNotesPath = Join-Path $releaseNotesDir $releaseNotesFileName
$preservedReleaseNotes = Resolve-PreservedReleaseNotes `
    -RepoRoot $repoRoot `
    -NormalizedVersion $normalizedVersion `
    -OutputDir $releaseNotesDir
$preservedReleaseNotesText = $preservedReleaseNotes.Text
if (-not [string]::IsNullOrWhiteSpace($preservedReleaseNotesText)) {
    Write-Host "Release notes kept from: $($preservedReleaseNotes.Path)"
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

function Get-ZipEntryText {
    param(
        [string]$ZipFile,
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        $entry = $zip.Entries |
            Where-Object {
                [string]::Equals(
                    $_.FullName.Replace('\', '/'),
                    $EntryName,
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            Select-Object -First 1
        if ($null -eq $entry) {
            return $null
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
        $zip.Dispose()
    }
}

function Assert-PatchMetadataConsistency {
    param(
        [string]$PatchZipPath,
        [string]$UpdateJsonPath,
        [string]$ExpectedBaseline,
        [string]$ExpectedLatest,
        [string]$ExpectedSha256,
        [long]$ExpectedSize
    )

    $patchJson = Get-ZipEntryText -ZipFile $PatchZipPath -EntryName "patch_manifest.json"
    if ([string]::IsNullOrWhiteSpace($patchJson)) {
        throw "AppPatch 元数据校验失败：包内缺少 patch_manifest.json"
    }

    $patchManifest = $patchJson | ConvertFrom-Json
    $problems = @()
    if (-not [string]::Equals(
            [string]$patchManifest.patch_baseline_version,
            $ExpectedBaseline,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $problems += "patch_manifest.json 基线 $($patchManifest.patch_baseline_version) 与实际基线 $ExpectedBaseline 不一致"
    }
    if (-not [string]::Equals(
            [string]$patchManifest.latest_version,
            $ExpectedLatest,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $problems += "patch_manifest.json 版本 $($patchManifest.latest_version) 与 $ExpectedLatest 不一致"
    }

    $updateJson = Get-Content -Raw -Encoding UTF8 $UpdateJsonPath | ConvertFrom-Json
    if (-not [string]::Equals(
            [string]$updateJson.patch_baseline_version,
            $ExpectedBaseline,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $problems += "update JSON 基线 $($updateJson.patch_baseline_version) 与实际基线 $ExpectedBaseline 不一致"
    }
    if (-not [string]::Equals(
            [string]$updateJson.patch_package.sha256,
            $ExpectedSha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $problems += "update JSON 的 sha256 与 AppPatch 实际哈希不一致"
    }
    if ([long]$updateJson.patch_package.size -ne $ExpectedSize) {
        $problems += "update JSON 的 size 与 AppPatch 实际大小不一致"
    }

    if ($problems.Count -gt 0) {
        foreach ($path in @($PatchZipPath, $UpdateJsonPath)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force
            }
        }
        throw "AppPatch 元数据一致性校验失败，已删除产物：" + [Environment]::NewLine + ($problems -join [Environment]::NewLine)
    }
}

function Test-ZipContainsEntry {
    param(
        [string]$ZipFile,
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $normalizedEntryName = $EntryName.Replace('\', '/')
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::Equals(
                    $entry.FullName.Replace('\', '/'),
                    $normalizedEntryName,
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

function ConvertFrom-Utf8Base64 {
    param([string]$Value)

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Value))
}

function Get-DotEnvValue {
    param([string]$Key)

    $paths = @(
        (Join-Path $repoRoot ".env"),
        (Join-Path $repoRoot "src\ExpressPackingMonitoring.Launcher\.env"),
        (Join-Path $repoRoot "src\ExpressPackingMonitoring\.env")
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
        [switch]$ExcludeCompatibleRuntimes,
        [string[]]$ExcludeRelativePaths = @(),
        # 过渡补丁专用：把共享运行时整份投递给老装机（它们没有 runtime\，只靠增量包升不上来）。
        [string]$RuntimeDir = "",
        # 累计退役清单的路径。测试夹具要指向临时文件，别让它把合成条目写回仓库里那份。
        [string]$RetireListPath = ""
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
    $excludedPaths = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($excludedPath in @($ExcludeRelativePaths)) {
        if (-not [string]::IsNullOrWhiteSpace($excludedPath)) {
            [void]$excludedPaths.Add($excludedPath.Replace('/', '\').TrimStart('\'))
        }
    }
    $protectedOpenCvName = "OpenCvSharpExtern.dll"
    $currentManagedOpenCv = Join-Path $CurrentAppDir "OpenCvSharp.dll"
    $baselineManagedOpenCv = Join-Path $BaselineDir "OpenCvSharp.dll"
    if ($excludedPaths.Contains($protectedOpenCvName)) {
        $currentNativeOpenCv = Join-Path $CurrentAppDir $protectedOpenCvName
        $baselineNativeOpenCv = Join-Path $BaselineDir $protectedOpenCvName
        if (-not (Test-Path -LiteralPath $currentManagedOpenCv -PathType Leaf) -or
            -not (Test-Path -LiteralPath $baselineManagedOpenCv -PathType Leaf) -or
            -not (Test-Path -LiteralPath $currentNativeOpenCv -PathType Leaf) -or
            -not (Test-Path -LiteralPath $baselineNativeOpenCv -PathType Leaf)) {
            throw "OpenCV native DLL is excluded from AppPatch, but managed or native OpenCV is missing from current or baseline payload."
        }
        $currentManagedHash = (Get-FileHash -LiteralPath $currentManagedOpenCv -Algorithm SHA256).Hash
        $baselineManagedHash = (Get-FileHash -LiteralPath $baselineManagedOpenCv -Algorithm SHA256).Hash
        if (-not [string]::Equals(
                $currentManagedHash,
                $baselineManagedHash,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "OpenCvSharp.dll changed while OpenCvSharpExtern.dll is excluded from AppPatch. Publish a full package or include a matching native DLL with an explicit migration plan."
        }
    }
    Get-ChildItem -LiteralPath $CurrentAppDir -File -Recurse | ForEach-Object {
        $relativePath = Get-RelativePath -BaseDir $CurrentAppDir -Path $_.FullName
        if ([string]::Equals(
                [IO.Path]::GetFileName($relativePath),
                $protectedOpenCvName,
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        if ($excludedPaths.Contains($relativePath)) {
            return
        }
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

    # 清理列表：基线里有、新版本已经不发布的文件（换大版本时版本号写进文件名的原生库、
    # 已下线的旧程序集）。增量包一贯只新增或覆盖，不给出这份列表这些文件会一直留在 app\ 里。
    # 白名单：tts_cache（缓存，可留可重建）与用户可替换的 ffmpeg / libvlc（Test-IsAppPatchManagedRuntimePath）。
    $currentRelativePaths = @{}
    foreach ($currentFile in Get-ChildItem -LiteralPath $CurrentAppDir -File -Recurse) {
        $currentRelativePaths[(Get-RelativePath -BaseDir $CurrentAppDir -Path $currentFile.FullName)] = $true
    }

    $deletedFiles = @()
    $newlyRetiredPaths = @()
    # 布局里的文件（共享运行时载荷）绝不能进清理列表：老装机那边它们要由启动器"迁移进
    # runtime\.legacy\（可回滚）"，直接删掉就绕过了回滚；而且它们本来就会被迁移处理掉。
    $runtimePayloadNames = @{}
    if (-not [string]::IsNullOrWhiteSpace($RuntimeDir) -and (Test-Path -LiteralPath $RuntimeDir -PathType Container)) {
        foreach ($layoutFile in Get-ChildItem -LiteralPath $RuntimeDir -File -Recurse) {
            $runtimePayloadNames[$layoutFile.Name] = $true
        }
    }
    foreach ($baselineFile in Get-ChildItem -LiteralPath $BaselineDir -File -Recurse) {
        $relativePath = Get-RelativePath -BaseDir $BaselineDir -Path $baselineFile.FullName
        if ([string]::Equals(
                [IO.Path]::GetFileName($relativePath),
                $protectedOpenCvName,
                [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($currentRelativePaths.ContainsKey($relativePath)) { continue }
        if ($relativePath.StartsWith("tts_cache\", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($excludedPaths.Contains($relativePath)) { continue }
        # runtime-seed 只能由落成器读取，补丁不得清理；旧装机里多出的 WinForms 种子
        # 留着无害，下一次安装完整包会自然收敛。
        if ($relativePath.StartsWith("runtime-seed\", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        # 白名单里的（用户可替换的 ffmpeg / libvlc）既不入退役清单也不删；基线里没有、
        # 当前目录故意不带的那批 libvlc 插件正是这一类，别让它们污染清单。
        if (Test-IsAppPatchManagedRuntimePath -RelativePath $relativePath) { continue }
        if ($runtimePayloadNames.ContainsKey([System.IO.Path]::GetFileName($relativePath))) { continue }
        $newlyRetiredPaths += $relativePath.Replace('\', '/')
    }

    # 退役清单是**累计**的，只增不减：清理列表按"基线 − 当前"算，而基线每换一次大版本就会抬升，
    # 上一代没清掉的残留（例如老启动器不认 deletes、或用户走手动安装）不会再次出现在差集里。
    # 所以把每次算出的条目并进这份文件、随发布一起提交，后面的补丁会继续带着它们。
    if ([string]::IsNullOrWhiteSpace($RetireListPath)) {
        $RetireListPath = Join-Path $repoRoot "tools\app-patch-retire-list.json"
    }
    $retireListPath = $RetireListPath
    $retiredPaths = [System.Collections.Generic.List[string]]::new()
    $retireListComment = ""
    if (Test-Path -LiteralPath $retireListPath -PathType Leaf) {
        try {
            $retireList = Get-Content -LiteralPath $retireListPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -ne $retireList.PSObject.Properties['_comment']) {
                $retireListComment = [string]$retireList._comment
            }
            if ($null -ne $retireList.PSObject.Properties['retired']) {
                foreach ($existing in @($retireList.retired)) {
                    $value = ([string]$existing).Trim()
                    if ($value.Length -gt 0 -and -not $retiredPaths.Contains($value)) { $retiredPaths.Add($value) }
                }
            }
        }
        catch {
            throw "退役清单读取失败：$retireListPath（$($_.Exception.Message)）"
        }
    }
    foreach ($candidate in $newlyRetiredPaths) {
        if (-not $retiredPaths.Contains($candidate)) {
            $retiredPaths.Add($candidate)
            Write-Host ("新增退役文件（本次随补丁清理，之后继续跟着补丁走）：{0}" -f $candidate)
        }
    }
    $orderedRetiredPaths = @($retiredPaths | Sort-Object -Unique)
    $retireListPayload = [ordered]@{ schemaVersion = 1 }
    if ($retireListComment.Length -gt 0) { $retireListPayload["_comment"] = $retireListComment }
    $retireListPayload["retired"] = $orderedRetiredPaths
    $retireListJson = ($retireListPayload | ConvertTo-Json -Depth 4) + "`n"
    $existingRetireListJson = if (Test-Path -LiteralPath $retireListPath -PathType Leaf) {
        [System.IO.File]::ReadAllText($retireListPath, [System.Text.Encoding]::UTF8)
    }
    else {
        ""
    }
    # 内容没变就不写：出包/测试都不该无谓地把一个"必须随发布提交"的文件改脏。
    if (-not [string]::Equals($existingRetireListJson, $retireListJson, [System.StringComparison]::Ordinal)) {
        [System.IO.File]::WriteAllText(
            $retireListPath,
            $retireListJson,
            (New-Object System.Text.UTF8Encoding($false)))
    }

    foreach ($relativePath in $orderedRetiredPaths) {
        $normalizedPath = $relativePath.Replace('/', '\')
        if ([string]::Equals(
                [IO.Path]::GetFileName($normalizedPath),
                $protectedOpenCvName,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "AppPatch retire list must not contain OpenCV native DLL: $relativePath"
        }
        if ($currentRelativePaths.ContainsKey($normalizedPath)) { continue }
        if ($normalizedPath.StartsWith("tts_cache\", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (Test-IsAppPatchManagedRuntimePath -RelativePath $normalizedPath) { continue }
        if ($runtimePayloadNames.ContainsKey([System.IO.Path]::GetFileName($normalizedPath))) { continue }
        $deletedFiles += [ordered]@{
            "path" = $relativePath
        }
    }

    # 清理列表的生成期守卫：清单本身有冲突或越界时，客户端会在安装那一刻才报错（用户侧一次安装失败）。
    foreach ($deleted in $deletedFiles) {
        $relativePath = ([string]$deleted.path).Replace('/', '\')
        if ([string]::Equals(
                [IO.Path]::GetFileName($relativePath),
                $protectedOpenCvName,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "AppPatch delete list must not contain OpenCV native DLL: $relativePath"
        }
        foreach ($written in $changedFiles) {
            if ([string]::Equals(
                    ([string]$written.path).Replace('/', '\'),
                    $relativePath,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "清理列表与写入列表冲突：$relativePath"
            }
        }
        if (Test-IsAppPatchManagedRuntimePath -RelativePath $relativePath) {
            throw "清理列表命中了受管运行时路径（用户可替换，不能删）：$relativePath"
        }
        foreach ($protected in @(
                "ExpressPackingMonitoring.exe",
                "ExpressPackingMonitoring.dll",
                "ExpressPackingMonitoring.deps.json",
                "ExpressPackingMonitoring.runtimeconfig.json")) {
            if ([string]::Equals($relativePath, $protected, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "清理列表不得包含主程序核心文件：$relativePath"
            }
        }
        if ($relativePath.StartsWith("runtime-seed\", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "清理列表不得包含运行时种子：$relativePath"
        }
    }
    if ($deletedFiles.Count -gt 2000) {
        Write-Warning ("清理列表条目异常多（{0} 个），请确认基线目录是否选错" -f $deletedFiles.Count)
    }

    if (-not [string]::IsNullOrWhiteSpace($RuntimeDir) -and (Test-Path -LiteralPath $RuntimeDir -PathType Container)) {
        # 运行时不与任何基线比对：老装机没有 runtime\，整份都要投递。
        # 物理位置放在 files\.runtime\ 下，避免与 app\ 里的同名文件冲突；清单用 root 标明落点。
        $runtimeFilesDir = Join-Path $patchFilesDir ".runtime"
        $runtimeFiles = @(Get-ChildItem -LiteralPath $RuntimeDir -File -Recurse)
        if ($runtimeFiles.Count -eq 0) {
            throw "RuntimeDir 里没有文件，无法生成过渡补丁：$RuntimeDir"
        }
        foreach ($runtimeFile in $runtimeFiles) {
            $relativePath = Get-RelativePath -BaseDir $RuntimeDir -Path $runtimeFile.FullName
            Copy-FilePreservingRelativePath -SourceFile $runtimeFile.FullName -DestinationRoot $runtimeFilesDir -RelativePath $relativePath
            $changedFiles += [ordered]@{
                "path" = $relativePath.Replace('\', '/')
                "sha256" = (Get-FileHash -LiteralPath $runtimeFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "size" = $runtimeFile.Length
                "root" = "runtime"
            }
        }
        Write-Host ("过渡补丁已带上共享运行时：{0} 个文件" -f $runtimeFiles.Count)
    }

    $patchManifest = [ordered]@{}
    $patchManifest["type"] = "baseline_patch"
    $patchManifest["patch_baseline_version"] = $BaselineVersion
    $patchManifest["latest_version"] = $LatestVersion
    $patchManifest["files"] = $changedFiles
    if ($deletedFiles.Count -gt 0) {
        $patchManifest["deletes"] = $deletedFiles
        Write-Host ("增量包将清理 {0} 个旧版本遗留文件" -f $deletedFiles.Count)
    }
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
        "本补丁只适用于 Setup 版（exe 安装版）。"
        "Microsoft Store 版请使用 Microsoft Store 的「库」页面更新，不要运行本脚本。"
        ""
        "脚本会校验补丁文件、识别原安装位置并在失败时恢复原文件。"
        "本补丁不会替换 OpenCV 原生运行库，老用户继续使用现有兼容版本。"
        "不要直接在压缩软件中运行，也不要单独移动 CMD、$appPatchInstallerScriptName、patch_manifest.json 或 files 文件夹。"
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $patchWorkDir $appPatchNoticeName) -Value $appPatchNotice -Encoding UTF8

    Compress-PackageWithRetry `
        -SourceDir $patchWorkDir `
        -DestinationZip $PatchZipPath `
        -CompressionLevel $ZipCompressionLevel
    if (Test-ZipContainsEntry -ZipFile $PatchZipPath -EntryName "files/$protectedOpenCvName") {
        throw "AppPatch package validation failed: OpenCV native DLL must not be included"
    }
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
$storeSymbolsDir = Join-Path $repoRoot "package\.store-symbols-tmp"
if (Test-Path -LiteralPath $storeSymbolsDir) {
    Remove-Item -LiteralPath $storeSymbolsDir -Recurse -Force
}
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
    throw "Launcher logical inputs changed. Run tools\Publish-LauncherBaseline.ps1 before packaging."
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
if (-not (Test-LauncherBaselineSourceUnchanged -RepositoryRoot $repoRoot -Tag $launcherTag -Baseline $launcherBaseline)) {
    throw "Launcher tracked files changed after $launcherTag. Establish a new launcher baseline."
}

New-DefaultTtsCache

Stop-StaleReleaseAppInstances
$includeVisionObb = $VisionObb -eq "include"
$visionModelBaseline = $null
$visionModelCachePath = ""
if ($includeVisionObb) {
    $visionModelBaseline = Read-VisionModelBaselineManifest `
        -ManifestPath $visionModelBaselineManifestPath
    $visionModelCacheDirectory = Join-Path $repoRoot "package\dependency-cache\vision-models"
    $visionModelCachePath = Join-Path $visionModelCacheDirectory ([string]$visionModelBaseline.file_name)
    $visionModelCachePath = Resolve-VisionModelBaseline `
        -Baseline $visionModelBaseline `
        -CacheDirectory $visionModelCacheDirectory `
        -DestinationPath $visionModelCachePath
}
$visionPublishArguments = if ($includeVisionObb) {
    @(
        "-p:IncludeVisionObb=true",
        "-p:CameraVisionModelPath=$visionModelCachePath"
    )
}
else {
    @("-p:IncludeVisionObb=false")
}
$appPublishArguments = @(
    $appProject,
    "-c", $Configuration,
    "-r", $Runtime,
    # 自包含发布：老启动器只能装 app\ 内的条目（它不认 root 落点），所以增量包里的应用必须自带运行时；
    # 共享布局由主程序首次启动时按 app\runtime-seed\ 用硬链接落成（见 RuntimeLayoutSeeder）。
    "--self-contained", "true",
    "-p:InformationalVersion=$informationalVersion",
    "-p:IncludeSourceRevisionInInformationalVersion=false",
    "-p:GitCommitId=$gitCommitId",
    "-p:PackingProofCollectStoreSymbols=true",
    "-p:DebugType=portable",
    "-p:DebugSymbols=true"
) + $visionPublishArguments + @(
    "-p:PublishSingleFile=false",
    "--artifacts-path", $appBuildArtifacts,
    "-o", $appPublishDir
)
Invoke-DotNetPublish -Arguments $appPublishArguments
Remove-Item -LiteralPath $appBuildArtifacts -Recurse -Force

$publishedPdbFiles = @(Get-ChildItem -LiteralPath $appPublishDir -Recurse -File -Filter "*.pdb")
if ($publishedPdbFiles.Count -eq 0) {
    throw "Release publish did not produce Store symbol files"
}
foreach ($pdbFile in $publishedPdbFiles) {
    Copy-FilePreservingRelativePath `
        -SourceFile $pdbFile.FullName `
        -DestinationRoot $storeSymbolsDir `
        -RelativePath (Get-RelativePath -BaseDir $appPublishDir -Path $pdbFile.FullName)
    Remove-Item -LiteralPath $pdbFile.FullName -Force
}

# Win10/11 标准包附带 Windows 系统语音（WinRT）辅助程序集；
# Win7 兼容变体在下方产物生成后剔除这些文件。
$winttsProject = Join-Path $repoRoot "src\ExpressPackingMonitoring.WinTts\ExpressPackingMonitoring.WinTts.csproj"
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
$winttsPdb = Join-Path $winttsPublishDir "ExpressPackingMonitoring.WinTts.pdb"
if (Test-Path -LiteralPath $winttsPdb -PathType Leaf) {
    Copy-Item -LiteralPath $winttsPdb -Destination (Join-Path $storeSymbolsDir "ExpressPackingMonitoring.WinTts.pdb") -Force
}
Remove-Item -LiteralPath $winttsPublishDir -Recurse -Force

$ffmpegBaseline = Read-FFmpegBaselineManifest -ManifestPath $ffmpegBaselineManifestPath
$ffmpegCacheDirectory = Join-Path $repoRoot "package\dependency-cache\ffmpeg\$($ffmpegBaseline.version)"
$sevenZipExecutable = Resolve-SevenZipExecutable
Resolve-FFmpegBaselineExecutable `
    -Baseline $ffmpegBaseline `
    -CacheDirectory $ffmpegCacheDirectory `
    -DestinationPath (Join-Path $appPublishDir "tools\ffmpeg.exe") `
    -SevenZipExecutable $sevenZipExecutable `
    -RepositoryRoot $repoRoot

$openCvBaseline = Read-OpenCvBaselineManifest -ManifestPath $openCvBaselineManifestPath
$openCvAppPatchExcludeRelativePaths = if ([bool]$openCvBaseline.app_patch_excluded) {
    @("OpenCvSharpExtern.dll")
}
else {
    @()
}
$visionAppPatchExcludeRelativePaths = if ($includeVisionObb) {
    @()
}
else {
    @(
        "ExpressPackingMonitoring.Vision.Obb.dll",
        "Microsoft.ML.OnnxRuntime.dll",
        "Microsoft.AI.DirectML.dll",
        "System.Numerics.Tensors.dll",
        "onnxruntime_providers_shared.dll",
        "DirectML.dll",
        "models\vision-obb.onnx"
    )
}
$openCvCacheDirectory = Join-Path $repoRoot "package\dependency-cache\opencv\$($openCvBaseline.version)-$($openCvBaseline.artifact_id)"
Resolve-OpenCvBaselineNativeDll `
    -Baseline $openCvBaseline `
    -CacheDirectory $openCvCacheDirectory `
    -DestinationPath (Join-Path $appPublishDir "OpenCvSharpExtern.dll") `
    -RepositoryRoot $repoRoot `
    -BuilderPath $openCvBuilderPath `
    -SourcePath $OpenCvNativeDllPath

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
if ($includeVisionObb) {
    $requiredAppRuntimeFiles += @(
        "onnxruntime.dll",
        "DirectML.dll",
        ("models\" + [string]$visionModelBaseline.installed_name)
    )
}
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
if ($includeVisionObb) {
    Assert-VisionModelRuntimeFiles -AppDir $appPublishDir -Baseline $visionModelBaseline
}

# 自包含发布：运行时平铺在 app\ 里。老启动器只认 app\ 内的条目（它不认 root 落点），所以补丁里的应用
# 必须自带运行时；包根**不再**放 runtime\，共享布局由主程序首次启动时用硬链接从 app\ 落成
# （ExpressPackingMonitoring.UpdateCore\RuntimeLayoutSeeder.cs）。这里把自包含特征与"种子"一起强校验。
foreach ($selfContainedMarker in @("coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $appPublishDir $selfContainedMarker) -PathType Leaf)) {
        throw "Clean package validation failed: app\$selfContainedMarker 缺失，说明这次不是自包含发布"
    }
}

$runtimeSeedSourcePath = Join-Path $outputFullPath ".runtime-seed-source"
if (Test-Path -LiteralPath $runtimeSeedSourcePath) {
    [System.IO.Directory]::Delete($runtimeSeedSourcePath, $true)
}
Write-Host "生成共享运行时布局（用于推导运行时种子）：$runtimeSeedSourcePath"
$sharedRuntimeLayout = New-SharedRuntimeLayout -AppDir $appPublishDir -DestinationPath $runtimeSeedSourcePath
$verifiedLayout = Test-SharedRuntimeLayout -LayoutPath $sharedRuntimeLayout.LayoutPath
Write-Host ("共享运行时布局自检通过：hostfxr {0}，文件 {1}，迁移映射 {2} 项" -f `
        $sharedRuntimeLayout.HostFxrVersion, $sharedRuntimeLayout.FileCount, $sharedRuntimeLayout.LegacyMappedFiles)

$runtimeSeed = New-RuntimeLayoutSeed -LayoutPath $sharedRuntimeLayout.LayoutPath -AppDir $appPublishDir
Write-Host ("运行时种子：布局 {0} 个文件，app\ 里已有同名同哈希 {1} 个（首次启动建硬链接），种子自带 {2} 个" -f `
        $runtimeSeed.LayoutFiles, $runtimeSeed.FromAppDir, $runtimeSeed.FromSeed)
[System.IO.Directory]::Delete($runtimeSeedSourcePath, $true)

$packageRoot = $packageArtifactRoot
$appPayloadManifestFiles = @(Get-AppPayloadManifestFiles -AppDir $appPublishDir)
$appPayloadFingerprint = Get-AppPayloadFingerprint -AppDir $appPublishDir
$legacyAppFullZipPath = Join-Path $packageRoot "ExpressPackingMonitoring_AppFull_$releaseTag.zip"
$appPatchZipName = "PackingProof_AppPatch_$releaseTag.zip"
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
$packageIntegrityName = "package_integrity_$releaseTag.json"
$packageIntegrityPath = Join-Path $packageRoot $packageIntegrityName
$setupFileName = "PackingProof_Setup_v$artifactVersion.exe"
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
$fullDownloadPage = Expand-ReleaseTemplate -Template $fullDownloadPageTemplate -ReleaseTag $releaseTag -FileName $setupFileName
$fullDownloadFallbackPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_FALLBACK_PAGE_URL_TEMPLATE" -DefaultValue ""
if ([string]::IsNullOrWhiteSpace($fullDownloadFallbackPageTemplate)) {
    $fullDownloadFallbackPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_PAGE" -DefaultValue ""
}
if ([string]::IsNullOrWhiteSpace($fullDownloadFallbackPageTemplate)) {
    $fullDownloadFallbackPageTemplate = Get-ConfiguredValue -Key "FULL_DOWNLOAD_PAGE_URL_TEMPLATE" -DefaultValue $releasePage
}
$fullDownloadFallbackPage = Expand-ReleaseTemplate -Template $fullDownloadFallbackPageTemplate -ReleaseTag $releaseTag -FileName $setupFileName

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
    -OutputFileName $setupFileName `
    -InstallerCompression $InstallerCompression
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer build failed: $setupPath"
}
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$setupSize = (Get-Item -LiteralPath $setupPath).Length
$giteeSetupIncluded = Test-GiteeAttachmentFits -Size $setupSize
$setupSignatureStatus = (Get-AuthenticodeSignature -LiteralPath $setupPath).Status.ToString()
[ordered]@{
    app_release_version = $normalizedVersion
    app_payload_sha256 = $appPayloadFingerprint
    app_payload_file_count = $appPayloadManifestFiles.Count
    setup_file = Split-Path -Leaf $setupPath
    setup_sha256 = $setupHash
    setup_size = $setupSize
} |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $packageIntegrityPath -Encoding UTF8

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
    foreach ($runtimeWarning in @($runtimeCompatibility.Warnings)) {
        Write-Warning $runtimeWarning
    }
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
            -ExcludeCompatibleRuntimes `
            -ExcludeRelativePaths ($openCvAppPatchExcludeRelativePaths + $visionAppPatchExcludeRelativePaths) `
            -RuntimeDir $(if ($IncludeRuntimeInPatch) { $sharedRuntimeLayoutPath } else { "" })

        if (-not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName "patch_manifest.json")) {
            throw "AppPatch package validation failed: missing patch_manifest.json"
        }
        if ($IncludeRuntimeInPatch -and -not (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName 'files/.runtime/layout.json')) {
            throw 'AppPatch transition validation failed: missing shared runtime layout'
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
            if ([string]::Equals($runtimeFile, "OpenCvSharpExtern.dll", [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
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
        if (Test-ZipContainsEntry -ZipFile $appPatchZipPath -EntryName 'files/OpenCvSharpExtern.dll') {
            throw 'AppPatch package validation failed: OpenCV native DLL must not be included'
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

if ($launcherPublishedWithRelease -and -not $patchSupported -and -not $DisablePatch) {
    throw "A new launcher baseline requires a compatible AppPatch bridge in the same release."
}

# 过渡补丁靠增量包把共享运行时送下去；没生成增量包时会退回普通发布，必须显式提醒。
if ($IncludeRuntimeInPatch -and -not $patchSupported) {
    Write-Warning "已指定 IncludeRuntimeInPatch，但本次没有生成增量包：$patchReason"
}

$storePackagePath = ""
$storePackageHash = ""
$storePackageSize = 0
$storePackageVersion = ""
$storePackageIdentity = ""
if ($SkipStorePackage) {
    Remove-Item -LiteralPath $storeSymbolsDir -Recurse -Force -ErrorAction SilentlyContinue
    if (-not [bool]$openCvBaseline.store_channel_allowed) {
        Write-Host "精简 OpenCV 尚未完成现场验证：本次按要求跳过 Microsoft Store MSIX，避免覆盖存量 Store 用户。"
    }
    else {
        Write-Warning "已跳过 Microsoft Store MSIX；该选项只用于本地验证，正式发布必须同时产出商店包。"
    }
}
elseif (-not [bool]$openCvBaseline.store_channel_allowed -and -not $AllowExperimentalOpenCvStore) {
    throw "当前 OpenCV 精简库尚未完成现场验证，已禁止生成 Microsoft Store MSIX，避免整包更新覆盖存量 Store 用户。确认产品接受风险后使用 -AllowExperimentalOpenCvStore，或在验证前使用 -SkipStorePackage。"
}
else {
    $storePackageScript = Join-Path $PSScriptRoot "Publish-StorePackage.ps1"
    if (-not (Test-Path -LiteralPath $storePackageScript -PathType Leaf)) {
        throw "找不到 Store 打包脚本：$storePackageScript"
    }
    $storeOutputRoot = Join-Path $repoRoot "package\store"
    $storeBuildArgs = @(
        "-Version", $normalizedVersion,
        "-AppPayloadDir", $appPublishDir,
        "-SymbolsDir", $storeSymbolsDir,
        "-RequireStoreIdentity",
        "-Sign",
        "-SuppressCertificateDetails",
        "-OutputRoot", $storeOutputRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($StoreIdentityConfigPath)) {
        $storeBuildArgs += @("-ConfigPath", $StoreIdentityConfigPath)
    }
    Write-Host "Building Microsoft Store MSIX from the release app payload..."
    try {
        & pwsh -NoProfile -File $storePackageScript @storeBuildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Microsoft Store MSIX build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Remove-Item -LiteralPath $storeSymbolsDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $storeProductDir = Join-Path $storeOutputRoot ("PackingProof_Store_v" + $normalizedVersion)
    $storePackagePath = Join-Path $storeProductDir ("PackingProof_Store_v" + $normalizedVersion + ".msix")
    $storeSymbolPath = Join-Path $storeProductDir ("PackingProof_Store_v" + $normalizedVersion + ".appxsym")
    if (-not (Test-Path -LiteralPath $storePackagePath -PathType Leaf)) {
        throw "Store MSIX validation failed: missing package $storePackagePath"
    }
    if (-not (Test-Path -LiteralPath $storeSymbolPath -PathType Leaf)) {
        throw "Store symbol validation failed: missing package $storeSymbolPath"
    }

    $storePackageHash = (Get-FileHash -LiteralPath $storePackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $storePackageSize = (Get-Item -LiteralPath $storePackagePath).Length
    $storeSymbolHash = (Get-FileHash -LiteralPath $storeSymbolPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $storeSymbolSize = (Get-Item -LiteralPath $storeSymbolPath).Length
    $storeMarkerPath = Join-Path $storeProductDir "payload\PackingProof.PackageEdition.json"
    if (-not (Test-Path -LiteralPath $storeMarkerPath -PathType Leaf)) {
        throw "Store MSIX validation failed: missing package edition marker $storeMarkerPath"
    }
    $storeMarker = Get-Content -LiteralPath $storeMarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $storePackageVersion = [string]$storeMarker.package_version
    $storePackageIdentity = [string]$storeMarker.package_identity
    if ([string]::IsNullOrWhiteSpace($storePackageVersion) -or
        [string]::IsNullOrWhiteSpace($storePackageIdentity)) {
        throw "Store MSIX validation failed: package edition marker is incomplete"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $storeArchive = [System.IO.Compression.ZipFile]::OpenRead($storePackagePath)
    try {
        $storeEntries = @($storeArchive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($storeEntries -notcontains "PackingProof.PackageEdition.json") {
            throw "Store MSIX validation failed: package edition marker is not embedded"
        }
        foreach ($forbiddenEntry in @(
            "apply_app_patch.ps1",
            "双击更新主程序.cmd",
            "apply_launcher_patch.ps1",
            "双击更新启动器.cmd"
        )) {
            if ($storeEntries -contains $forbiddenEntry) {
                throw "Store MSIX validation failed: Setup update script is embedded: $forbiddenEntry"
            }
        }
        if ($storeEntries | Where-Object { $_ -like "*.pdb" }) {
            throw "Store MSIX validation failed: PDB files must be submitted through the appxsym package"
        }
    }
    finally {
        $storeArchive.Dispose()
    }
    $storeSymbolArchive = [System.IO.Compression.ZipFile]::OpenRead($storeSymbolPath)
    try {
        $symbolEntries = @($storeSymbolArchive.Entries | Where-Object { $_.Name -like "*.pdb" })
        if ($symbolEntries.Count -eq 0) {
            throw "Store appxsym validation failed: no PDB files were found"
        }
    }
    finally {
        $storeSymbolArchive.Dispose()
    }

    $storeManifest = [ordered]@{
        app_release_version = $normalizedVersion
        package_version = $storePackageVersion
        identity_name = $storePackageIdentity
        package_file = Split-Path -Leaf $storePackagePath
        relative_path = $storePackagePath.Substring($repoRoot.TrimEnd('\').Length + 1)
        sha256 = $storePackageHash
        size = $storePackageSize
        symbol_file = Split-Path -Leaf $storeSymbolPath
        symbol_sha256 = $storeSymbolHash
        symbol_size = $storeSymbolSize
        app_payload_sha256 = $appPayloadFingerprint
        app_payload_file_count = $appPayloadManifestFiles.Count
        signed = $true
    }
    $storeManifest |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $storeManifestPath -Encoding UTF8
}

$updateManifest = [ordered]@{}
$updateManifest["latest_version"] = $normalizedVersion
$updateManifest["title"] = ConvertFrom-Utf8Base64 "6K+35aGr5YaZ5pu05paw5qCH6aKY"
$updateManifest["download_source"] = $DownloadSource
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
# 反向时序：换了新启动器、而它需要更新的主程序时才填（0 = 不要求）。见 UpdateCore\LauncherProtocol.cs。
$launcherPackageInfo["required_app_protocol"] = 0
$updateManifest["launcher_package"] = $launcherPackageInfo
if ($patchSupported) {
    $patchPackageInfo = [ordered]@{}
    $patchPackageInfo["type"] = "baseline_patch"
    $patchPackageInfo["url"] = $appPatchPlaceholderUrl
    $patchPackageInfo["github_url"] = $appPatchGithubUrl
    $patchPackageInfo["gitee_url"] = $appPatchGiteeUrl
    $patchPackageInfo["sha256"] = $appPatchHash
    $patchPackageInfo["size"] = $appPatchSize
    # 这份补丁要求启动器至少具备的能力号：带 runtime 落点/载荷的补丁只有新启动器装得上，
    # 必须显式声明，客户端（主程序与启动器）据此判定"要不要先把启动器更新掉"。
    # 自包含发布（0.0.83 起）的补丁全是 app\ 内的条目、老启动器也能装，所以这里是 0。
    $requiredLauncherProtocol = if ($IncludeRuntimeInPatch) { 2 } else { 0 }
    $patchPackageInfo["required_launcher_protocol"] = $requiredLauncherProtocol
    if ($IncludeRuntimeInPatch -and $requiredLauncherProtocol -lt 2) {
        throw "带运行时的过渡补丁必须声明 required_launcher_protocol >= 2"
    }
    $updateManifest["patch_package"] = $patchPackageInfo
    $updateManifest["notes"] = @(
        "请填写本版本最重要的用户可见变化"
        "请填写本版本修复的主要问题"
    )
}
else {
    $updateManifest["patch_package"] = $null
    $updateManifest["notes"] = @(
        "请填写本版本最重要的用户可见变化"
        "请填写本版本修复的主要问题"
    )
}
$updateManifest["full_download_page"] = $fullDownloadPage
$updateManifest["full_download_fallback_page"] = $fullDownloadFallbackPage

$updateManifest |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $updateJsonPath -Encoding UTF8

if ($patchSupported) {
    Assert-PatchMetadataConsistency `
        -PatchZipPath $appPatchZipPath `
        -UpdateJsonPath $updateJsonPath `
        -ExpectedBaseline $normalizedPatchBaselineVersion `
        -ExpectedLatest $normalizedVersion `
        -ExpectedSha256 $appPatchHash `
        -ExpectedSize $appPatchSize
}

$zipParent = Split-Path -Parent $zipFullPath
if (-not [string]::IsNullOrWhiteSpace($zipParent)) {
    New-Item -ItemType Directory -Force -Path $zipParent | Out-Null
}
if ($IncludeFullZip) {
    Compress-PackageWithRetry `
        -SourceDir $outputFullPath `
        -DestinationZip $zipFullPath `
        -CompressionLevel $ZipCompressionLevel
}
if ($IncludeSevenZip) {
    Compress-Package7zWithRetry `
        -SourceDir $outputFullPath `
        -DestinationArchive $sevenZipFullPath `
        -SevenZipExecutable $sevenZipExecutable `
        -CompressionLevel $SevenZipCompressionLevel
}

if ($IncludeFullZip) {
    if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "ExpressPackingMonitoring.exe")) {
        throw "Full zip validation failed: missing root launcher"
    }
    if (-not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/ExpressPackingMonitoring.exe")) {
        throw "Full zip validation failed: missing app/ExpressPackingMonitoring.exe"
    }
}
if ($IncludeSevenZip) {
    if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "ExpressPackingMonitoring.exe" -SevenZipExecutable $sevenZipExecutable)) {
        throw "Full 7z validation failed: missing root launcher"
    }
    if (-not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "app/ExpressPackingMonitoring.exe" -SevenZipExecutable $sevenZipExecutable)) {
        throw "Full 7z validation failed: missing app/ExpressPackingMonitoring.exe"
    }
}
foreach ($runtimeFile in $requiredAppRuntimeFiles) {
    if ($IncludeFullZip -and -not (Test-ZipContainsEntry -ZipFile $zipFullPath -EntryName "app/$runtimeFile")) {
        throw "Full zip validation failed: missing camera barcode runtime dependency app/$runtimeFile"
    }
    if ($IncludeSevenZip -and -not (Test-SevenZipContainsEntry -ArchivePath $sevenZipFullPath -EntryName "app/$runtimeFile" -SevenZipExecutable $sevenZipExecutable)) {
        throw "Full 7z validation failed: missing camera barcode runtime dependency app/$runtimeFile"
    }
}
$fullZipHash = if ($IncludeFullZip) { (Get-FileHash -LiteralPath $zipFullPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { "" }
$fullZipSize = if ($IncludeFullZip) { (Get-Item -LiteralPath $zipFullPath).Length } else { 0 }

$patchReleaseInfo = if ($patchSupported) {
    "$appPatchZipName"
} else {
    $patchReason
}
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
$nextGithubAssetIndex = 2
if ($patchSupported) {
    $releaseInfoLines += "$nextGithubAssetIndex. AppPatch（自动更新；包内也可双击手动更新）：" + $patchReleaseInfo
    $nextGithubAssetIndex++
    if ($launcherPublishedWithRelease) {
        $releaseInfoLines += "$nextGithubAssetIndex. LauncherPatch（本版本建立新启动器基线）：" + $launcherPackageName
        $nextGithubAssetIndex++
    }
    $releaseInfoLines += "$nextGithubAssetIndex. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
    if (-not $launcherPublishedWithRelease) {
        $releaseInfoLines += "启动器沿用 $($launcherBaseline.tag)，本版本不要重复上传 LauncherPatch"
    }
}
else {
    $releaseInfoLines += "$nextGithubAssetIndex. 本版本不提供增量包：" + $patchReason
    $nextGithubAssetIndex++
    $releaseInfoLines += "$nextGithubAssetIndex. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
}
$releaseInfoLines += ""
$releaseInfoLines += "Gitee 命令行上传："
$giteeAssetIndex = 1
if ($giteeSetupIncluded) {
    $releaseInfoLines += "$giteeAssetIndex. Windows 安装向导（推荐）：" + $setupFileName
    $giteeAssetIndex++
}
if ($patchSupported) {
    $releaseInfoLines += "$giteeAssetIndex. AppPatch（自动/手动主程序更新）：" + $patchReleaseInfo
    $giteeAssetIndex++
    if ($launcherPublishedWithRelease) {
        $releaseInfoLines += "$giteeAssetIndex. LauncherPatch（本版本建立新启动器基线）：" + $launcherPackageName
        $giteeAssetIndex++
    }
    else {
        $releaseInfoLines += "启动器沿用 $($launcherBaseline.tag)，本版本不要重复上传 LauncherPatch"
    }
    $releaseInfoLines += "$giteeAssetIndex. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
}
else {
    $releaseInfoLines += "$giteeAssetIndex. " + (ConvertFrom-Utf8Base64 "5pu05paw5o+P6L+w5paH5Lu277ya") + $updateJsonName
}
if ($giteeSetupIncluded) {
    $releaseInfoLines += "完整 Setup 严格小于 100MB，已直接上传 Gitee"
}
else {
    $releaseInfoLines += "完整 Setup 达到 100MB，Gitee 使用 no-runtime 安装向导或 Full download page"
}
$releaseInfoLines += "完整 7z 与完整 ZIP 默认不生成、不上传；仅在本地确有需要时传入 -IncludeSevenZip 或 -IncludeFullZip"
$releaseInfoLines += "Local verification only (do not upload by default): " + $launcherManifestName
$releaseInfoLines += ""
$releaseInfoLines += "Setup SHA256:"
$releaseInfoLines += $setupHash
$releaseInfoLines += "Setup size: $setupSize bytes"
$releaseInfoLines += "Setup Authenticode status: $setupSignatureStatus"
if (-not [string]::Equals($setupSignatureStatus, "Valid", [System.StringComparison]::OrdinalIgnoreCase)) {
    $releaseInfoLines += "WARNING: Setup is unsigned; Windows SmartScreen may show an unknown publisher warning."
}
if (-not [string]::IsNullOrWhiteSpace($storePackagePath)) {
    $releaseInfoLines += ""
    $releaseInfoLines += "Microsoft Store MSIX (submit via Partner Center; do not upload to Git Release):"
    $releaseInfoLines += $storePackagePath
    $releaseInfoLines += "Store package identity: $storePackageIdentity"
    $releaseInfoLines += "Store package version: $storePackageVersion"
    $releaseInfoLines += "Store package SHA256:"
    $releaseInfoLines += $storePackageHash
    $releaseInfoLines += "Store package size: $storePackageSize bytes"
}
else {
    $releaseInfoLines += ""
    $releaseInfoLines += "Microsoft Store MSIX: skipped for local verification only"
}
$releaseInfoLines += ""
$releaseInfoLines += "OpenCV native variant: $($openCvBaseline.artifact_id)"
$releaseInfoLines += "OpenCV native size: $($openCvBaseline.native.size) bytes"
$releaseInfoLines += "OpenCV native SHA256:"
$releaseInfoLines += [string]$openCvBaseline.native.sha256
if ($IncludeFullZip) {
    $releaseInfoLines += ""
    $releaseInfoLines += "Optional local full ZIP SHA256:"
    $releaseInfoLines += $fullZipHash
    $releaseInfoLines += "Optional local full ZIP size: $fullZipSize bytes"
}
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

# 发布笔记与提交覆盖：打包时就把"上一个正式版以来的全部提交"落成清单，
# 并保证发布笔记文件存在，避免发布那一刻才发现漏写。
$previousReleaseTag = Get-PreviousFormalReleaseTag -RepoRoot $repoRoot -ReleaseTag $releaseTag
$commitChecklist = Write-ReleaseCommitChecklist `
    -RepoRoot $repoRoot `
    -PackageRoot $outputFullPath `
    -NormalizedVersion $normalizedVersion `
    -FromTag $previousReleaseTag
if (-not [string]::IsNullOrWhiteSpace($preservedReleaseNotesText)) {
    [System.IO.File]::WriteAllText(
        $releaseNotesPath,
        $preservedReleaseNotesText,
        [System.Text.UTF8Encoding]::new($false))
    # 版本稳定位置留一份：下次重打目录名带提交后缀时，还能从这里找回人工笔记。
    $releaseNotesDraftPath = Get-ReleaseNotesDraftPath -RepoRoot $repoRoot -NormalizedVersion $normalizedVersion
    $releaseNotesDraftDir = Split-Path -Parent $releaseNotesDraftPath
    if (-not (Test-Path -LiteralPath $releaseNotesDraftDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $releaseNotesDraftDir | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $releaseNotesDraftPath,
        $preservedReleaseNotesText,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Release notes kept from previous run: $releaseNotesPath"
}
elseif (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\development\RELEASE_NOTES_TEMPLATE.md") -Destination $releaseNotesPath
    Write-Host "Release notes skeleton created: $releaseNotesPath"
}

# 产物新鲜度守卫：曾经出现过"重新打包，但 app 负载还是上一次的旧文件"（源码改了、DLL 没重新编出来），
# 于是发出去的包看着是新的、内容却是旧的。这里要求 app 里的主程序集不早于应用侧最新的源码文件。
$payloadAssemblyPath = Join-Path $appPublishDir "ExpressPackingMonitoring.dll"
$appSourceRoot = Join-Path $repoRoot "src\ExpressPackingMonitoring"
if ((Test-Path -LiteralPath $payloadAssemblyPath) -and (Test-Path -LiteralPath $appSourceRoot)) {
    $newestAppSource = Get-ChildItem -LiteralPath $appSourceRoot -Recurse -File -Include *.cs, *.xaml |
        Where-Object { $_.FullName -notmatch '\\obj\\' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $newestAppSource -and
        (Get-Item -LiteralPath $payloadAssemblyPath).LastWriteTime -lt $newestAppSource.LastWriteTime) {
        throw "产物比源码旧，请清理产物目录后重新打包：$payloadAssemblyPath 早于 $($newestAppSource.FullName)"
    }
}

Write-Host "Clean package created: $outputFullPath"
Write-Host "Installer created: $setupPath"
if ($IncludeSevenZip) {
    Write-Host "Optional local 7z package created: $sevenZipFullPath"
}
if ($IncludeFullZip) {
    Write-Host "Optional local zip package created: $zipFullPath"
}
if ($patchSupported) {
    Write-Host "AppPatch package created: $appPatchZipPath"
}
else {
    Write-Host "AppPatch package skipped: $patchReason"
}
Write-Host "Update manifest created: $updateJsonPath"
Write-Host "Release info created: $releaseInfoPath"
Write-Host "Commit checklist created: $($commitChecklist.Path)"
Write-Host "Release notes: $releaseNotesPath"
if (-not [string]::IsNullOrWhiteSpace($storePackagePath)) {
    Write-Host "Microsoft Store MSIX created: $storePackagePath"
    Write-Host "Store package SHA256: $storePackageHash"
}
else {
    Write-Host "Microsoft Store MSIX skipped (local verification only)"
}
Write-Host ""
Write-Host "发布前必须完成："
Write-Host "  1. 按 docs\development\RELEASE_NOTES_TEMPLATE.md 填写 $releaseNotesFileName"
Write-Host "  2. 逐条核对 $(Split-Path -Leaf $commitChecklist.Path)：$($commitChecklist.Range) 共 $($commitChecklist.Count) 个提交，用户可见变化都要写进发布笔记"
Write-Host "  3. 填写 $(Split-Path -Leaf $updateJsonPath) 的 title 与 notes（面向启动器显示的简洁摘要）"
Write-Host "  4. 运行 pwsh -NoProfile -File tools\Publish-Releases.ps1 -ValidateOnly 自检后再正式发布"
if (-not [string]::IsNullOrWhiteSpace($storePackagePath)) {
    Write-Host "  5. 将 Microsoft Store MSIX 上传到 Partner Center，并与 GitHub/Gitee Release 同一版本一起提交"
}
Write-Host "Root items:"
Get-ChildItem -LiteralPath $outputFullPath | Sort-Object PSIsContainer, Name | Select-Object Name, Mode, Length | Format-Table -AutoSize
