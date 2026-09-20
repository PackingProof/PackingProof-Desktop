# 生成「不带 .NET 运行时」的清爽包，专供 Gitee 发布（Gitee 单附件上限 100MB，自包含 Setup 约 110MB 传不上去）。
#
#   pwsh -NoProfile -File Tools\Publish-NoRuntimePackage.ps1 -Tag v0.0.70 [-UploadGitee]
#
# 约定：
# - 根启动器是 AOT 原生程序，不需要运行时，直接复用已发布清爽包里的那一份（保证字节一致）；
# - `app\` 用 `dotnet publish --self-contained false` 重新发布，只含程序自身与非运行时依赖；
# - 目标机器必须已安装 .NET 8 Desktop Runtime (x64)，包内附说明文件；
# - 更新方式和标准包一致：增量包只包含与基线不同的应用文件，运行时没变时不含运行时，
#   所以 no-runtime 安装目录同样能被启动器的 AppPatch 正常更新。
param(
    [string]$Tag = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
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
$zipName = "PackingProof_${releaseTag}_no-runtime.zip"
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
本包仅供 Gitee 下载（Gitee 单附件上限 100MB，自包含安装包放不下）。
更新方式和标准包一致：启动器会自动下载并安装增量包（增量包只含应用文件，不含运行时）；
也可以重新下载新版本包，用其中的 app\ 覆盖旧目录。
%LOCALAPPDATA%\ExpressPackingMonitoring 下的配置、数据库与录像不受影响。
"@

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
Write-Host ("==> 产物：{0}（{1:N1} MB）" -f $zipPath, ($zipSize / 1MB))
if ($zipSize -gt 100MB) {
    throw "产物超过 Gitee 单附件上限 100MB，需要进一步精简"
}

if ($UploadGitee) {
    $null = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
    Write-Host "==> 上传到 Gitee Release $releaseTag"
    & gitee release upload --repo $GiteeRepository --tag $releaseTag --file $zipPath
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee 附件上传失败，退出码 $LASTEXITCODE"
    }
    Write-Host "Gitee 已上传：$zipName"
}

Remove-Item -LiteralPath $workRoot -Recurse -Force
Write-Host "完成"
