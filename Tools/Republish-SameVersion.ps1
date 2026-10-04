# 重发同一个已发布版本（热修已发出去的包）：重建 → 替换两边资产 → 重做 Gitee no-runtime。
#
#   pwsh -NoProfile -File Tools\Republish-SameVersion.ps1 -Version 0.0.74 -Title "<一句话内容>" `
#       -ConfirmCommitCoverage -Confirmed
#
# 约定（与 docs/development/RELEASE_AND_RUNTIME.md 的《重发同一版本》一致）：
# - 这是"对外动作"，不加 -Confirmed 只打印计划并停下，不构建、不上传；
#   想看计划又不带确认用 -DryRun
# - 版本号默认取 csproj 的 <Version>；标签默认 v<版本号>
# - 发布标签必须指向已合并到主干的提交，且本次修复要在里面；HEAD 允许比标签多提交
#   （工具、文档这类改动），但只要应用侧代码有差异就直接拒绝，避免发出去的包不是标签内容
# - 重建交给 Publish-CleanPackage.ps1（它自己会清产物目录，并保留人工写好的发布笔记）
# - 上传交给 Publish-Releases.ps1：-UpdateNotes -ReplaceAssets，GitHub 用 --clobber，
#   Gitee 先删同名附件再上传（Gitee 不支持覆盖上传）
# - 最后重做 Gitee 专属 no-runtime 安装包，Gitee 侧才有"双击安装"入口

param(
    [string]$Version = "",
    [string]$Tag = "",
    [string]$Title = "",
    [switch]$ConfirmCommitCoverage,
    [switch]$SkipNoRuntime,
    [switch]$DryRun,
    [switch]$Confirmed
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

. (Join-Path $PSScriptRoot "ReleaseVersion.Common.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.Common.ps1")

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message"
}

function Invoke-ReleaseStep {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptName,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $scriptPath = Join-Path $PSScriptRoot $ScriptName
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "找不到脚本：$scriptPath"
    }

    & pwsh -NoProfile -File $scriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$ScriptName 失败，退出码 $LASTEXITCODE"
    }
}

if (git status --porcelain --untracked-files=all) {
    throw "重发前 Git 工作区必须干净"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
    $match = Select-String -LiteralPath $csproj -Pattern "<Version>([^<]+)</Version>" | Select-Object -First 1
    if (-not $match) {
        throw "读不到 csproj 里的 <Version>，请用 -Version 显式指定"
    }
    $Version = $match.Matches[0].Groups[1].Value.Trim()
}

$normalizedVersion = Get-NormalizedReleaseVersion $Version
if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号不是 X.Y.Z 形式：$Version"
}
$releaseTag = if ([string]::IsNullOrWhiteSpace($Tag)) { "v$normalizedVersion" } else { $Tag.Trim() }

& git rev-parse --verify --quiet "$releaseTag^{commit}" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "找不到 tag：$releaseTag"
}
$tagCommit = (git rev-parse "$releaseTag^{commit}").Trim()
$headCommit = (git rev-parse HEAD).Trim()
$tagMatchesHead = [string]::Equals($headCommit, $tagCommit, [System.StringComparison]::OrdinalIgnoreCase)

# 允许 HEAD 比标签多提交（工具、文档），但应用侧代码必须与标签完全一致，
# 否则构建出来的包不是标签内容，用户拿到的和源码对不上。
$appRelativePaths = @(
    "ExpressPackingMonitoring",
    "ExpressPackingMonitoring.Core",
    "ExpressPackingMonitoring.UpdateCore",
    "ExpressPackingMonitoring.Launcher",
    "ExpressPackingMonitoring.Host",
    "ExpressPackingMonitoring.WinTts",
    "Installer"
)
if (-not $tagMatchesHead) {
    $appDiff = @(& git diff --name-only "$tagCommit..$headCommit" -- $appRelativePaths 2>$null)
    if ($appDiff.Count -gt 0) {
        $list = ($appDiff | Select-Object -First 10) -join [Environment]::NewLine
        throw "HEAD 与 $releaseTag 之间的应用侧代码有差异，不能重发这个版本：$([Environment]::NewLine)$list"
    }
}

if ([string]::IsNullOrWhiteSpace($Title) -and -not $DryRun) {
    throw "必须用 -Title 写一句话概括本版最核心的变化（与发布标题一致）"
}

$artifactNames = Get-ReleaseArtifactNames -Tag $releaseTag -RepoRoot $repoRoot
$planSteps = @(
    "重建产物：pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1 -Version $normalizedVersion",
    "替换两边资产：pwsh -NoProfile -File Tools\Publish-Releases.ps1 -Tag $releaseTag -Title <标题> -UpdateNotes -ReplaceAssets$(if ($ConfirmCommitCoverage) { ' -ConfirmCommitCoverage' })",
    $(if ($SkipNoRuntime) { "重做 Gitee no-runtime：（已按 -SkipNoRuntime 跳过）" } else { "重做 Gitee no-runtime：pwsh -NoProfile -File Tools\Publish-NoRuntimePackage.ps1 -Tag $releaseTag -UploadGitee" })
)

Write-Host "重发同一版本：$releaseTag"
Write-Host "  构建提交   $headCommit$(if ($tagMatchesHead) { "（与 $releaseTag 一致）" } else { "（比 $releaseTag 多提交，仅工具/文档差异）" })"
Write-Host "  产物目录   $($artifactNames.PackageRoot)"
Write-Host "  发布笔记   $(Get-ReleaseNotesFileName -NormalizedVersion $normalizedVersion)（重建时会保留人工写的那份）"
Write-Host "  步骤"
$planSteps | ForEach-Object { Write-Host "    - $_" }
Write-Host "  事后仍需人工：macOS 包在 Mac 桌面会话里跑 Tools/Publish-MacRelease.sh <版本> both"

if ($DryRun) {
    Write-Host ""
    Write-Host "仅查看计划：未构建、未上传"
    exit 0
}

if (-not $Confirmed) {
    throw "重发会覆盖两个平台上已发布的资产，属于对外动作：确认无误后加 -Confirmed 再来一次（只看计划用 -DryRun）"
}

Write-Step "重建产物目录（会保留人工写好的发布笔记）"
$cleanPackageArgs = @("-Version", $normalizedVersion)
Invoke-ReleaseStep -ScriptName "Publish-CleanPackage.ps1" -Arguments $cleanPackageArgs

Write-Step "替换 GitHub 与 Gitee 上的资产、更新正文"
$releaseArgs = @(
    "-Tag", $releaseTag,
    "-Title", $Title,
    "-UpdateNotes",
    "-ReplaceAssets"
)
if ($ConfirmCommitCoverage) {
    $releaseArgs += "-ConfirmCommitCoverage"
}
Invoke-ReleaseStep -ScriptName "Publish-Releases.ps1" -Arguments $releaseArgs

if (-not $SkipNoRuntime) {
    Write-Step "重做 Gitee 专属 no-runtime 安装包"
    Invoke-ReleaseStep -ScriptName "Publish-NoRuntimePackage.ps1" -Arguments @("-Tag", $releaseTag, "-UploadGitee")
}

Write-Host ""
$noRuntimeSummary = if ($SkipNoRuntime) { "" } else { "，Gitee no-runtime 安装包已重做" }
Write-Host "$releaseTag 重发完成：GitHub 与 Gitee 资产已替换$noRuntimeSummary"
Write-Host "还差一步（人工）：在 Mac 桌面会话里跑 Tools/Publish-MacRelease.sh $normalizedVersion both，再确认两边 DMG 是否为本次构建"
