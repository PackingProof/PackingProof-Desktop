# 生成「不带 .NET 运行时」的清爽包，专供 Gitee 发布（Gitee 单附件上限 100MB，自包含 Setup 约 110MB 传不上去）。
#
#   pwsh -NoProfile -File Tools\Publish-NoRuntimePackage.ps1 -Tag v0.0.70 [-UploadGitee] [-IncludeZip]
#
# 约定：
# - 默认只生成并上传**安装向导**（Gitee 用户和 GitHub 用户一样双击安装）；ZIP 免安装包是可选本地产物，
#   只有显式加 -IncludeZip 时才生成并上传。
# - 根启动器是 AOT 原生程序，不需要运行时，直接复用已发布清爽包里的那一份（保证字节一致）；
# - `app\` 用 `dotnet publish --self-contained false` 重新发布，只含程序自身与非运行时依赖；
# - 目标机器必须已安装 .NET 8 Desktop Runtime (x64)，包内附说明文件；
# - 更新方式和标准包一致：增量包只包含与基线不同的应用文件，运行时没变时不含运行时，
#   所以 no-runtime 安装目录同样能被启动器的 AppPatch 正常更新。
param(
    [string]$Tag = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipSetup,
    [switch]$IncludeZip,
    [switch]$UploadGitee,
    [string]$GiteeRepository = "PackingProof/PackingProof-Desktop"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

. (Join-Path $PSScriptRoot "ReleaseVersion.Common.ps1")
. (Join-Path $PSScriptRoot "GiteeAuth.Common.ps1")

$releaseTag = if ([string]::IsNullOrWhiteSpace($Tag)) { (git describe --tags --exact-match 2>$null) } else { $Tag }
if ([string]::IsNullOrWhiteSpace($releaseTag)) {
    throw "必须用 -Tag 指定已发布的版本标签（例如 -Tag v0.0.70）"
}

$normalizedVersion = Get-NormalizedReleaseVersion $releaseTag
$artifactNames = Get-ReleaseArtifactNames -Tag $releaseTag -RepoRoot $repoRoot
$packageRoot = $artifactNames.PackageRoot
$cleanPackageDir = Join-Path $packageRoot "PackingProof+$releaseTag"
$launcherExe = Join-Path $cleanPackageDir "ExpressPackingMonitoring.exe"
$sourceAppDir = Join-Path $cleanPackageDir "app"
if (-not (Test-Path -LiteralPath $launcherExe -PathType Leaf)) {
    throw "找不到已发布清爽包里的根启动器：$launcherExe（先生成正式包）"
}

$gitCommitId = (git rev-parse HEAD).Trim()
$appProject = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$workRoot = Join-Path $repoRoot "package\.no-runtime-work"
$publishDir = Join-Path $workRoot "publish"
$packageDir = Join-Path $workRoot "package"
$buildArtifacts = Join-Path $workRoot "build-artifacts"
$zipName = "PackingProof_no-runtime_$releaseTag.zip"
$zipPath = Join-Path $packageRoot $zipName

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir, $packageDir | Out-Null

Write-Host "==> 发布不带运行时的 app（--self-contained false）"
& dotnet publish $appProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:InformationalVersion=$normalizedVersion `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:GitCommitId=$gitCommitId `
    -p:PublishSingleFile=false `
    --artifacts-path $buildArtifacts `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "无运行时发布失败，退出码 $LASTEXITCODE"
}
if (Test-Path -LiteralPath $buildArtifacts) {
    Remove-Item -LiteralPath $buildArtifacts -Recurse -Force
}

# FFmpeg 与可选的 TTS 缓存不在 publish 产物里，从正式清爽包复制，保证行为与标准包一致。
$sourceFfmpeg = Join-Path $sourceAppDir "tools\ffmpeg.exe"
if (Test-Path -LiteralPath $sourceFfmpeg -PathType Leaf) {
    $targetTools = Join-Path $publishDir "tools"
    New-Item -ItemType Directory -Force -Path $targetTools | Out-Null
    Copy-Item -LiteralPath $sourceFfmpeg -Destination (Join-Path $targetTools "ffmpeg.exe") -Force
}
$sourceTtsCache = Join-Path $sourceAppDir "tts_cache"
if (Test-Path -LiteralPath $sourceTtsCache -PathType Container) {
    Copy-Item -LiteralPath $sourceTtsCache -Destination (Join-Path $publishDir "tts_cache") -Recurse -Force
}

Write-Host "==> 组装清爽包（根启动器 + app\）"
Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $packageDir "ExpressPackingMonitoring.exe") -Force
Copy-Item -LiteralPath $publishDir -Destination (Join-Path $packageDir "app") -Recurse -Force
Set-Content -LiteralPath (Join-Path $packageDir "使用前必读.txt") -Encoding UTF8 -Value @"
本包不包含 .NET 运行时，运行前请先安装：
  .NET 8 Desktop Runtime (x64)  https://dotnet.microsoft.com/download/dotnet/8.0

安装后双击根目录的 ExpressPackingMonitoring.exe 启动。
这份安装向导仅供 Gitee 下载（Gitee 单附件上限 100MB，自包含安装包放不下）。
更新方式和标准包一致：启动器会自动下载并安装增量包（增量包只含应用文件，不含运行时）；
也可以直接下载新版本的安装向导，覆盖安装到原目录。
%LOCALAPPDATA%\ExpressPackingMonitoring 下的配置、数据库与录像不受影响。
"@

$zipPath = ""
if ($IncludeZip) {
    $zipPath = Join-Path $packageRoot $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    $sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($sevenZip) {
        & $sevenZip.Source a -tzip -mx=5 $zipPath (Join-Path $packageDir "*") | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip 打包失败，退出码 $LASTEXITCODE"
        }
    }
    else {
        Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force
    }

    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    Write-Host ("==> ZIP（可选本地产物）：{0}（{1:N1} MB）" -f $zipPath, ($zipSize / 1MB))
    if ($zipSize -gt 100MB) {
        throw "ZIP 超过 Gitee 单附件上限 100MB，需要进一步精简"
    }
}

# 同样打一份安装向导：Gitee 用户和 GitHub 用户拿到的是同一种"双击安装"体验。
$setupPath = ""
if (-not $SkipSetup) {
    # 命名与其它产物一致：版本号放最后（PackingProof_Setup_vX.Y.Z.exe / PackingProof_AppPatch_vX.Y.Z.zip）。
    $setupName = "PackingProof_Setup_no-runtime_$releaseTag.exe"
    $installerBuilder = Join-Path $PSScriptRoot "Build-Installer.ps1"
    Write-Host "==> 生成安装向导（不含运行时）"
    & $installerBuilder `
        -SourceDir $packageDir `
        -Version $normalizedVersion `
        -OutputDir $packageRoot `
        -OutputFileName $setupName
    $setupPath = Join-Path $packageRoot $setupName
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "安装向导没有生成：$setupPath"
    }
    $setupSize = (Get-Item -LiteralPath $setupPath).Length
    Write-Host ("==> 安装向导：{0}（{1:N1} MB）" -f $setupPath, ($setupSize / 1MB))
    if ($setupSize -gt 100MB) {
        Write-Host "提示：安装向导超过 Gitee 单附件上限 100MB，本次不上传；可改用 -IncludeZip 生成较小的 ZIP" -ForegroundColor Yellow
        $setupPath = ""
    }
}

if ($UploadGitee) {
    $null = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
    Write-Host "==> 上传到 Gitee Release $releaseTag"
    $uploadFiles = @()
    if (-not [string]::IsNullOrWhiteSpace($setupPath)) { $uploadFiles += $setupPath }
    if (-not [string]::IsNullOrWhiteSpace($zipPath)) { $uploadFiles += $zipPath }
    if ($uploadFiles.Count -eq 0) {
        throw "没有可上传的产物"
    }
    & gitee release upload --repo $GiteeRepository $releaseTag @uploadFiles
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee 附件上传失败，退出码 $LASTEXITCODE"
    }
    Write-Host ("Gitee 已上传：{0}" -f (($uploadFiles | ForEach-Object { Split-Path -Leaf $_ }) -join ", "))
}

Remove-Item -LiteralPath $workRoot -Recurse -Force
Write-Host "完成"
