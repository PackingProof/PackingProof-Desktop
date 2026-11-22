param(
    [string]$Version = "",
    [ValidateSet("include", "exclude")]
    [string]$VisionObb = "include",
    [string]$PatchBaselineVersion = "0.0.100",
    [string]$BaselineAppDir = "",
    [ValidateSet("gitee", "github", "auto")]
    [string]$DownloadSource = "github",
    [switch]$ConfirmManualCoreChecks,
    [switch]$SkipTtsCacheGeneration,
    [switch]$DisablePatch,
    [switch]$SkipStorePackage
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (git status --porcelain --untracked-files=all) {
    throw "发布准备前 Git 工作区必须干净"
}

$versionFile = Join-Path $repoRoot "Directory.Build.props"
$versionMatch = Select-String -LiteralPath $versionFile -Pattern "<Version>([^<]+)</Version>" |
    Select-Object -First 1
if (-not $versionMatch) {
    throw "读不到 Directory.Build.props 里的 <Version>"
}
$projectVersion = $versionMatch.Matches[0].Groups[1].Value.Trim()
$requestedVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectVersion
}
else {
    $Version.Trim().TrimStart("v")
}
if ($requestedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号不是 X.Y.Z 形式：$requestedVersion"
}
if (-not [string]::Equals(
        $requestedVersion,
        $projectVersion,
        [System.StringComparison]::Ordinal)) {
    throw "Directory.Build.props 版本是 $projectVersion，与请求的 v$requestedVersion 不一致"
}

$releaseTag = "v$requestedVersion"
& git rev-parse --verify --quiet "$releaseTag^{commit}" *> $null
if ($LASTEXITCODE -ne 0) {
    throw "找不到 $releaseTag。请先把发布提交快进到本地 main，并在该提交创建本地标签"
}
$headCommit = (git rev-parse HEAD).Trim()
$tagCommit = (git rev-parse "$releaseTag^{commit}").Trim()
if (-not [string]::Equals(
        $headCommit,
        $tagCommit,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "当前 HEAD 不是 $releaseTag 指向的提交。发布门禁必须在已打标签的同一提交上运行"
}

Write-Host "发布准备：$releaseTag"
Write-Host "  提交：$headCommit"
Write-Host "  步骤：发布自动验收一次 -> 出包（跳过重复全量测试）"

$validationArgs = @(
    "-NoProfile",
    "-File", (Join-Path $PSScriptRoot "Test-Release-Automated.ps1"),
    "-Configuration", "Release",
    "-VisionObb", $VisionObb
)
& pwsh @validationArgs
if ($LASTEXITCODE -ne 0) {
    throw "发布自动验收失败，退出码 $LASTEXITCODE；不要推送标签或继续发布"
}

$packageArgs = @(
    "-NoProfile",
    "-File", (Join-Path $PSScriptRoot "Publish-CleanPackage.ps1"),
    "-Configuration", "Release",
    "-Version", $requestedVersion,
    "-PatchBaselineVersion", $PatchBaselineVersion,
    "-DownloadSource", $DownloadSource,
    "-VisionObb", $VisionObb,
    "-SkipFullTest"
)
if (-not [string]::IsNullOrWhiteSpace($BaselineAppDir)) {
    $packageArgs += @("-BaselineAppDir", $BaselineAppDir)
}
if ($ConfirmManualCoreChecks) {
    $packageArgs += "-ConfirmManualCoreChecks"
}
if ($SkipTtsCacheGeneration) {
    $packageArgs += "-SkipTtsCacheGeneration"
}
if ($DisablePatch) {
    $packageArgs += "-DisablePatch"
}
if ($SkipStorePackage) {
    $packageArgs += "-SkipStorePackage"
}

& pwsh @packageArgs
if ($LASTEXITCODE -ne 0) {
    throw "发布包生成失败，退出码 $LASTEXITCODE"
}

Write-Host ""
Write-Host "发布门禁与出包完成：$releaseTag"
Write-Host "下一步：执行 tools\Publish-Releases.ps1 -ValidateOnly；校验通过后才推送 main 与标签"
