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
. (Join-Path $PSScriptRoot "ReleaseNotes.Common.ps1")
. (Join-Path $PSScriptRoot "LauncherBaseline.Common.ps1")

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
    # 发行标签与启动器标签（launcher-vX.Y.Z）可能同时落在 HEAD 上 —— 重建启动器基线时就是这样。
    # 不能再用 git describe --tags --exact-match：它会挑到 launcher-vX.Y.Z，于是"标签与版本不一致"误报。
    # 与 Publish-CleanPackage.ps1 同一口径：只在 HEAD 上的标签里认 v<数字> 形式的发行标签。
    $releaseTagAtHead = @(& git tag --points-at HEAD 2>$null) |
        Where-Object { $_ -match '^v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($releaseTagAtHead)) {
        Write-Warn "当前提交没有 v 形式的发行 tag（csproj 是 $projectVersion，发布前需建 v$projectVersion）"
    }
    elseif ($releaseTagAtHead.Trim() -eq "v$projectVersion") {
        Write-Ok "当前提交的 tag $($releaseTagAtHead.Trim()) 与 csproj 一致"
        $previousTag = Get-PreviousFormalReleaseTag -RepoRoot $repoRoot -ReleaseTag $releaseTagAtHead.Trim()
        $subjects = Get-ReleaseCommitSubjects -RepoRoot $repoRoot -FromTag $previousTag
        $rangeFrom = if ([string]::IsNullOrWhiteSpace($previousTag)) { "仓库起点" } else { $previousTag }
        Write-Ok "发布笔记范围：$rangeFrom .. $($releaseTagAtHead.Trim()) 共 $($subjects.Count) 个提交，必须逐条覆盖"
    }
    else {
        Write-Fail "tag $($releaseTagAtHead.Trim()) 与 csproj 的 $projectVersion 不一致"
    }
}

Write-Host ""
Write-Host "== 启动器基线 =="
# 启动器的逻辑输入（Launcher\Program.cs、UpdateCore 的更新客户端等）变了就必须先重建基线。
# 这条校验原本只写在 Publish-CleanPackage.ps1 里，而且要等构建、全量测试、FFmpeg 往返全部跑完
# 才拦下来 —— 白等十几分钟才发现要返工。这里用同一套指纹算法提前核对。
$launcherRuntime = "win-x64"
$launcherUpdateCheckUrl = $env:UPDATE_CHECK_URL
if ([string]::IsNullOrWhiteSpace($launcherUpdateCheckUrl)) {
    $launcherUpdateCheckUrl = Read-DotEnvValue -Key "UPDATE_CHECK_URL"
}
if ([string]::IsNullOrWhiteSpace($launcherUpdateCheckUrl)) {
    $launcherUpdateCheckUrl = "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases/latest"
}

$launcherBaseline = $null
$launcherBaselineError = ""
try {
    $launcherBaseline = Read-LauncherBaselineManifest -ManifestPath (Join-Path $PSScriptRoot "launcher-baseline.json")
}
catch {
    $launcherBaselineError = $_.Exception.Message
}

if ($null -eq $launcherBaseline) {
    Write-Fail "启动器基线清单不可用：$launcherBaselineError"
}
else {
    $launcherTag = [string]$launcherBaseline.tag
    $launcherFingerprint = Get-LauncherLogicalFingerprint `
        -RepositoryRoot $repoRoot `
        -Runtime $launcherRuntime `
        -UpdateCheckUrl $launcherUpdateCheckUrl

    if (-not [string]::Equals(
            $launcherFingerprint,
            [string]$launcherBaseline.source_fingerprint,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Fail "启动器逻辑输入已变化：先跑 pwsh -NoProfile -File Tools\Publish-LauncherBaseline.ps1 -Version $projectVersion，提交新的 launcher-baseline.json，再创建组件标签（当前基线 $launcherTag）"
    }
    elseif (-not [string]::Equals([string]$launcherBaseline.runtime, $launcherRuntime, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$launcherBaseline.update_check_url, $launcherUpdateCheckUrl, [System.StringComparison]::Ordinal)) {
        Write-Fail "启动器基线 $launcherTag 里的 runtime / 更新地址与当前发布配置不一致，需要重建基线"
    }
    else {
        & git rev-parse --verify --quiet "$launcherTag^{commit}" *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "启动器组件标签 $launcherTag 还不存在：提交新的 launcher-baseline.json 后创建该标签"
        }
        else {
            $launcherFingerprintFiles = @(Get-LauncherFingerprintFiles)
            & git diff --quiet "$launcherTag^{commit}" HEAD -- @launcherFingerprintFiles
            if ($LASTEXITCODE -ne 0) {
                Write-Fail "组件标签 $launcherTag 之后启动器指纹文件又改过，需要重建基线并重打该标签"
            }
            else {
                Write-Ok "启动器基线 $launcherTag 与当前逻辑输入一致"
            }
        }
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
