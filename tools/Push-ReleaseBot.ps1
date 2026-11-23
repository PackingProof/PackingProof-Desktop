# 使用发布机器人把本地主线提交或标签直接推送到 GitHub/Gitee。
# 默认发布不再创建 PR，但远端写入仍禁止使用个人身份。

param(
    [ValidateSet("branch", "tag")]
    [string]$Kind = "branch",
    [string]$Name = "main",
    [ValidateSet("gitee", "github", "both")]
    [string]$Target = "",
    [string]$GitHubRemote = "Github",
    [string]$GiteeRemote = "Gitee",
    [switch]$ReplaceTag,
    [switch]$Confirmed,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

. (Join-Path $PSScriptRoot "GiteeAuth.Common.ps1")
. (Join-Path $PSScriptRoot "ReleaseAuth.Common.ps1")
. (Join-Path $PSScriptRoot "Branding.Common.ps1")
$brand = Get-BrandProfile -RepoRoot $repoRoot

function Read-DotEnvValue {
    param([Parameter(Mandatory = $true)][string]$Key)

    $envPath = Join-Path $repoRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath)) {
        return ""
    }

    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.*)$") {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return ""
}

function Resolve-PushTargets {
    param([string]$Requested)

    $value = $Requested
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = $env:RELEASE_PUSH_TARGET
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = Read-DotEnvValue -Key "RELEASE_PUSH_TARGET"
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = $env:PR_TARGET_HOST
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = Read-DotEnvValue -Key "PR_TARGET_HOST"
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = "both"
    }

    switch ($value.Trim().ToLowerInvariant()) {
        "gitee" { return @("gitee") }
        "github" { return @("github") }
        "both" { return @("gitee", "github") }
        default {
            throw "发布推送目标只能是 gitee、github 或 both，当前配置是：$value"
        }
    }
}

function Get-RemoteName {
    param([Parameter(Mandatory = $true)][string]$Platform)

    if ($Platform -eq "github") {
        if ([string]::IsNullOrWhiteSpace($GitHubRemote)) {
            throw "GitHub 远端名不能为空"
        }
        return $GitHubRemote.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($GiteeRemote)) {
        throw "Gitee 远端名不能为空"
    }
    return $GiteeRemote.Trim()
}

if ($Kind -eq "branch" -and $Name -ne "main") {
    throw "默认发布只允许直推 main；当前目标分支：$Name"
}
if ($Kind -eq "tag" -and $Name -notmatch '^(?:v|launcher-v)\d+\.\d+\.\d+$') {
    throw "发布标签必须使用 vX.Y.Z 或 launcher-vX.Y.Z 形式：$Name"
}
if ($ReplaceTag -and $Kind -ne "tag") {
    throw "-ReplaceTag 只能用于标签推送"
}
if (git status --porcelain --untracked-files=all) {
    throw "远端推送前工作区必须干净"
}

$headCommit = (git rev-parse HEAD).Trim()
$sourceRef = if ($Kind -eq "tag") {
    $tagCommit = & git rev-parse --verify --quiet "refs/tags/$Name^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) {
        throw "找不到本地标签：$Name"
    }
    if ($Name -match '^v\d+\.\d+\.\d+$') {
        $localMainCommit = & git rev-parse --verify --quiet "refs/heads/main^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($localMainCommit)) {
            throw "找不到本地 main，无法确认发布标签位置"
        }
        if (-not [string]::Equals(
                $tagCommit.Trim(),
                $localMainCommit.Trim(),
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "发布标签 $Name 必须指向本地 main 的当前提交；请先完成本地快进和发布门禁"
        }
        if (-not [string]::Equals(
                $tagCommit.Trim(),
                $headCommit,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "发布标签 $Name 必须指向当前 HEAD；请切到发布提交后再推送"
        }
    }
    "refs/tags/$Name"
}
else {
    "HEAD"
}
$destinationRef = if ($Kind -eq "tag") {
    "refs/tags/$Name"
}
else {
    "refs/heads/$Name"
}

$targets = Resolve-PushTargets -Requested $Target
Write-Host "发布机器人直推"
Write-Host "  提交：$headCommit"
Write-Host "  来源：$sourceRef"
Write-Host "  目标：$($targets -join ', ') / $destinationRef"

if (-not $Confirmed -and -not $DryRun) {
    throw "远端直推属于发布写操作。确认目标与提交后加 -Confirmed 重新执行"
}

if ($DryRun) {
    foreach ($platform in $targets) {
        $remote = Get-RemoteName $platform
        if ($ReplaceTag) {
            Write-Host "  [dry-run] git push $remote --delete $destinationRef"
        }
        Write-Host "  [dry-run] git push $remote $sourceRef`:$destinationRef"
    }
    return
}

& pwsh -NoProfile -File (Join-Path $PSScriptRoot "Test-PrivateBrandLeak.ps1") -RepoRoot $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "私有定制身份泄漏检查未通过，已停止远端推送"
}

Enable-ReleaseBotGitTransport -Platforms $targets -Brand $brand

foreach ($platform in $targets) {
    $remote = Get-RemoteName $platform
    if ($Kind -eq "branch") {
        & git fetch --no-tags $remote "refs/heads/$Name`:refs/remotes/$remote/$Name"
        if ($LASTEXITCODE -ne 0) {
            throw "无法读取 $remote/$Name，已停止推送"
        }
        & git merge-base --is-ancestor "refs/remotes/$remote/$Name" HEAD
        if ($LASTEXITCODE -ne 0) {
            throw "本地 $Name 不是 $remote/$Name 的快进后继；禁止强推，先同步远端"
        }
    }
    elseif ($ReplaceTag) {
        $existingTag = & git ls-remote --tags $remote $destinationRef 2>$null |
            Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($existingTag)) {
            & git push --porcelain $remote --delete $destinationRef
            if ($LASTEXITCODE -ne 0) {
                throw "发布机器人删除 $remote/$destinationRef 失败"
            }
            Write-Host "  $remote 旧标签已删除：$destinationRef"
        }
    }

    & git push --porcelain $remote "$sourceRef`:$destinationRef"
    if ($LASTEXITCODE -ne 0) {
        throw "发布机器人推送 $remote 失败"
    }

    $remoteValue = & git ls-remote $remote $destinationRef 2>$null |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($remoteValue)) {
        throw "推送后无法确认 $remote/$destinationRef"
    }
    Write-Host "  $remote OK: $remoteValue"
}

Write-Host "发布机器人直推完成"
