# 把当前 tag 的桌面端产物发布到 GitHub 与 Gitee Release。
#
#   pwsh -NoProfile -File Tools\Publish-Releases.ps1 <release-notes-file> [-Title "<一句话内容>"] [-Prerelease]
#
# 约定（与 docs/development/RELEASE_AND_RUNTIME.md 的资产表一致）：
# - GitHub：Setup、update JSON、可选 AppPatch；仅新启动器基线时附 LauncherPatch
# - Gitee：update JSON、可选 AppPatch；仅新启动器基线时附 LauncherPatch，不上传 Setup
# - 完整 7z 与完整 ZIP 是本地产物，任何渠道都不上传
# - 产物必须已由 Tools\Publish-CleanPackage.ps1 生成并通过校验
# - 标题固定 `v<X.Y.Z> <一句话内容>`，两个平台保持一致
# - GitHub 用 gh、Gitee 用 gitee CLI，登录态由 CLI 自己维护，脚本不读凭据

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$NotesFile,
    [string]$Title = "",
    [switch]$Prerelease
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$repoSlug = "PackingProof/PackingProof-Desktop"

function Assert-Command {
    param([string]$Name, [string]$Hint)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "找不到 $Name，$Hint"
    }
}

function Test-IsInsidePackageDir {
    param([string]$Path, [string]$RepoRoot)

    $packageRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "package"))
    return $Path.StartsWith(($packageRoot.TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-Path -LiteralPath $NotesFile)) {
    throw "找不到发布笔记：$NotesFile"
}
$notesFullPath = (Resolve-Path -LiteralPath $NotesFile).Path

# 发布笔记不入库，必须来自 package 下该版本自己的产物目录。
if (-not (Test-IsInsidePackageDir -Path $notesFullPath -RepoRoot $repoRoot)) {
    Write-Warning "发布笔记不在 package\ 产物目录下：$notesFullPath"
}

if (git status --porcelain --untracked-files=all) {
    throw "发布前 Git 工作区必须干净"
}

$tag = (git describe --tags --exact-match 2>$null)
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "当前提交没有精确 tag，请先建 v<X.Y.Z> 标签"
}
$tag = $tag.Trim()

$version = $tag.TrimStart('v')
$packageRoot = Join-Path $repoRoot "package\PackingProof+$tag"
if (-not (Test-Path -LiteralPath $packageRoot)) {
    throw "找不到产物目录：$packageRoot，请先执行 Tools\Publish-CleanPackage.ps1"
}

# 按资产表挑文件：Setup 与 update JSON 必须存在，补丁包按本次是否生成决定。
$setupPath = Join-Path $packageRoot "PackingProof_Setup_v$version.exe"
$updateJsonPath = Join-Path $packageRoot "update_v$version.json"
$appPatchPath = Join-Path $packageRoot "PackingProof_AppPatch_$tag.zip"
$launcherPatchPath = Join-Path $packageRoot "PackingProof_LauncherPatch_$tag.zip"

foreach ($required in @($setupPath, $updateJsonPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "缺少必须上传的产物：$required"
    }
}

$githubAssets = @($setupPath, $updateJsonPath)
$giteeAssets = @($updateJsonPath)
if (Test-Path -LiteralPath $appPatchPath) {
    $githubAssets += $appPatchPath
    $giteeAssets += $appPatchPath
}
if (Test-Path -LiteralPath $launcherPatchPath) {
    $githubAssets += $launcherPatchPath
    $giteeAssets += $launcherPatchPath
}

$releaseTitle = if ([string]::IsNullOrWhiteSpace($Title)) { $tag } else { "$tag $Title" }

Assert-Command -Name "gh" -Hint "GitHub Release 无法创建；安装后执行 gh auth login"
Assert-Command -Name "gitee" -Hint "Gitee Release 无法创建；安装后执行 gitee auth login --token <token>"

Write-Host "发布 $tag"
Write-Host "  标题   $releaseTitle"
Write-Host "  笔记   $notesFullPath"
Write-Host "  GitHub 资产："
$githubAssets | ForEach-Object { Write-Host "    $(Split-Path -Leaf $_)" }
Write-Host "  Gitee 资产："
$giteeAssets | ForEach-Object { Write-Host "    $(Split-Path -Leaf $_)" }

Write-Host ""
Write-Host "==> GitHub Release"
& gh release view $tag --repo $repoSlug *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "GitHub 上 $tag 已存在，跳过创建"
}
else {
    $ghArgs = @("release", "create", $tag) + $githubAssets +
        @("--repo", $repoSlug, "--title", $releaseTitle, "--notes-file", $notesFullPath)
    if ($Prerelease) { $ghArgs += "--prerelease" }

    # 公网到 GitHub 偶发连不上，重试几次再判失败。
    $created = $false
    foreach ($attempt in 1..5) {
        & gh @ghArgs
        if ($LASTEXITCODE -eq 0) { $created = $true; break }
        if ($attempt -lt 5) { Start-Sleep -Seconds 10 }
    }
    if (-not $created) {
        throw "GitHub Release 创建失败"
    }
}

Write-Host ""
Write-Host "==> Gitee Release"
& gitee release view $tag --repo $repoSlug *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Gitee 上 $tag 已存在，跳过创建"
}
else {
    $notesText = Get-Content -LiteralPath $notesFullPath -Raw
    $giteeArgs = @(
        "release", "create",
        "--repo", $repoSlug,
        "--tag", $tag,
        "--target", "main",
        "--name", $releaseTitle,
        "--notes", $notesText)
    if ($Prerelease) { $giteeArgs += "--prerelease" }

    & gitee @giteeArgs *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee Release 创建失败"
    }
}

foreach ($asset in $giteeAssets) {
    & gitee release upload --repo $repoSlug $tag $asset *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee 附件上传失败：$(Split-Path -Leaf $asset)"
    }
}
Write-Host "Gitee 附件已上传（Gitee 会把文件名里的 + 显示成空格，属正常）"

Write-Host ""
Write-Host "GitHub 与 Gitee Release 均已就绪：$tag"
Write-Host "Setup 只在 GitHub 提供，Gitee 侧按资产表不上传"
