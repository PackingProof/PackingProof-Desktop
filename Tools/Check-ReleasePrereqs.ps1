# 发布前置条件自检：在花时间构建之前，先确认这台机器真的能走完发布流程。
#
#   pwsh -NoProfile -File Tools\Check-ReleasePrereqs.ps1
#
# 只读检查，不构建、不上传、不修改任何东西，也不打印凭据内容。
# 完整发布顺序见 docs/development/RELEASE_AND_RUNTIME.md。

$ErrorActionPreference = "Continue"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

. (Join-Path $PSScriptRoot "GiteeAuth.Common.ps1")

$blockers = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

function Write-Ok    { param([string]$Message) Write-Host "  [OK]   $Message" }
function Write-Fail  { param([string]$Message) Write-Host "  [缺失] $Message"; $blockers.Add($Message) }
function Write-Warn  { param([string]$Message) Write-Host "  [提示] $Message"; $warnings.Add($Message) }

function Read-DotEnvValue {
    param([string]$Key)

    $envPath = Join-Path $repoRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath)) { return "" }

    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.*)$") {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return ""
}

Write-Host "发布前置条件自检"
Write-Host "仓库：$repoRoot"

Write-Host ""
Write-Host "== 仓库状态 =="
if (git status --porcelain --untracked-files=all) {
    Write-Fail "工作区不干净，发布脚本会拒绝执行（先提交或 git stash -u）"
}
else {
    Write-Ok "工作区干净"
}

$csprojPath = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$projectVersion = ""
if (Test-Path -LiteralPath $csprojPath) {
    $match = Select-String -Path $csprojPath -Pattern "<Version>([^<]+)</Version>" | Select-Object -First 1
    if ($match) { $projectVersion = $match.Matches[0].Groups[1].Value.Trim() }
}
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    Write-Fail "读不到 csproj 里的 <Version>"
}
else {
    $tag = (git describe --tags --exact-match 2>$null)
    if ([string]::IsNullOrWhiteSpace($tag)) {
        Write-Warn "当前提交没有精确 tag（csproj 是 $projectVersion，发布前需建 v$projectVersion）"
    }
    elseif ($tag.Trim() -eq "v$projectVersion") {
        Write-Ok "当前提交的 tag $($tag.Trim()) 与 csproj 一致"
    }
    else {
        Write-Fail "tag $($tag.Trim()) 与 csproj 的 $projectVersion 不一致"
    }
}

Write-Host ""
Write-Host "== 本机配置 .env =="
if (Test-Path -LiteralPath (Join-Path $repoRoot ".env")) {
    Write-Ok ".env 存在"
}
else {
    Write-Warn ".env 不存在；Gitee 发布令牌会退回读取本机 .gitee\apikey.txt"
}

Write-Host ""
Write-Host "== 发布渠道登录态 =="
if (Get-Command gh -ErrorAction SilentlyContinue) {
    gh auth status *> $null
    if ($LASTEXITCODE -eq 0) { Write-Ok "gh 已登录" }
    else { Write-Fail "gh 未登录，执行 gh auth login" }
}
else {
    Write-Fail "未安装 gh，GitHub Release 无法创建"
}

# Gitee 令牌固定来自 .env；`gitee auth status` 在令牌失效时仍返回 0，
# 所以这里做一次真实只读调用，避免到发布那一刻才发现认证不可用。
if (Get-Command gitee -ErrorAction SilentlyContinue) {
    $giteeTokenSource = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
    $giteeTokenLabel = if ($giteeTokenSource) { $giteeTokenSource } else { "gitee CLI 登录态" }
    if (Test-GiteeAuthentication -Repository "PackingProof/PackingProof-Desktop" -RepoRoot $repoRoot) {
        Write-Ok "gitee 令牌可用（来源：$giteeTokenLabel）"
    }
    elseif ($giteeTokenSource) {
        Write-Fail "gitee 令牌不可用（来源：$giteeTokenLabel），请核对 .env 的 GITEE_TOKEN"
    }
    else {
        Write-Fail "gitee 不可用：.env 里没有 GITEE_TOKEN，gitee CLI 登录态也不可用"
    }
}
else {
    Write-Fail "未安装 gitee CLI，Gitee Release 无法创建"
}

Write-Host ""
Write-Host "== 工具链 =="
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Ok "dotnet 可用（$(dotnet --version 2>$null)）"
}
else {
    Write-Fail "找不到 dotnet"
}

# FFmpeg 基线以 .7z 分发，解压依赖 7-Zip；即使不生成完整 7z 也必须有它。
# 解析顺序与 Publish-CleanPackage.ps1 的 Resolve-SevenZipExecutable 保持一致。
$sevenZipCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($env:SEVEN_ZIP_EXE)) {
    $sevenZipCandidates += $env:SEVEN_ZIP_EXE
}
$sevenZipCommand = Get-Command "7z.exe" -ErrorAction SilentlyContinue
if ($sevenZipCommand) { $sevenZipCandidates += $sevenZipCommand.Source }
$sevenZipCandidates += @(
    (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe"))

if ($sevenZipCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) {
    Write-Ok "7-Zip 可用（FFmpeg 基线解压需要）"
}
else {
    Write-Fail "找不到 7-Zip，执行 winget install --id 7zip.7zip -e -s winget"
}

# 与 Tools\Build-Installer.ps1 的解析顺序保持一致，避免自检和实际构建判断不同。
$isccCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_ISCC)) {
    $isccCandidates += $env:INNO_SETUP_ISCC
}
$isccCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($isccCommand) { $isccCandidates += $isccCommand.Source }
$isccCandidates += @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"))

if ($isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) {
    Write-Ok "Inno Setup 可用（Setup 安装包需要）"
}
else {
    Write-Fail "找不到 Inno Setup，执行 winget install --id JRSoftware.InnoSetup -e -s winget"
}

Write-Host ""
Write-Host "======== 自检汇总 ========"
if ($warnings.Count -gt 0) {
    Write-Host "提示（不阻断）："
    foreach ($item in $warnings) { Write-Host "  - $item" }
}

if ($blockers.Count -gt 0) {
    Write-Host ""
    Write-Host "阻断项 $($blockers.Count) 个，必须先解决："
    foreach ($item in $blockers) { Write-Host "  - $item" }
    Write-Host ""
    Write-Host "流程看 docs/development/RELEASE_AND_RUNTIME.md"
    exit 1
}

Write-Host ""
Write-Host "本机发布前置条件齐备"
Write-Host "下一步：pwsh -NoProfile -File Tools\Test-CI.ps1 跑本地 CI 门禁"
