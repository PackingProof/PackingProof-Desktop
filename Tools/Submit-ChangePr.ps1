# 把当前分支作为 PR 提交到远程。默认先提 Gitee，合并后再把主干同步到 GitHub。
#
#   pwsh -NoProfile -File Tools\Submit-ChangePr.ps1 -Title "<PR 标题>" [-BodyFile <markdown>] `
#       [-Target gitee|github|both] [-Base main] [-Merge] [-Approve] [-NoSync] [-Force] [-DryRun]
#
# 约定（与 AGENTS.md、docs/development/RELEASE_AND_RUNTIME.md 一致）：
# - 不直接向 main 推送提交，一律走 PR；合并用 rebase，保留每个提交，不 squash
# - 默认目标是 Gitee；可按仓库覆盖：仓库根目录 .env 里写 PR_TARGET_HOST=gitee|github|both，
#   也可以用环境变量 PR_TARGET_HOST 或命令行 -Target 临时指定（-Target 优先级最高）
# - PR 说明不传 -BodyFile 时，用"相对目标分支的提交列表"自动生成
# - -Merge 用 rebase 合并 PR；合并后默认把合并结果同步到另一个远端（-NoSync 可关闭）
# - Gitee 仓库要求"审查 / 测试"通过才能合并时，加 -Approve 先自动完成审查与测试标记
# - 改写了自己推上去的 PR 分支（amend / rebase）时要加 -Force，脚本用 --force-with-lease 覆盖
# - Gitee 令牌固定取 .env 的 GITEE_TOKEN，不打印、不落盘

param(
    [Parameter(Position = 0)][string]$Title = "",
    [string]$BodyFile = "",
    [string]$Target = "",
    [string]$Base = "main",
    [string]$GiteeRepository = "PackingProof/PackingProof-Desktop",
    [string]$GithubRepository = "PackingProof/PackingProof-Desktop",
    [string]$GiteeRemote = "Gitee",
    [string]$GithubRemote = "Github",
    [switch]$Merge,
    [switch]$Approve,
    [switch]$NoSync,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

. (Join-Path $PSScriptRoot "GiteeAuth.Common.ps1")

$defaultTarget = "gitee"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message"
}

function Read-DotEnvValue {
    param([Parameter(Mandatory = $true)][string]$Key)

    $envPath = Join-Path $repoRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath)) { return "" }

    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.*)$") {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return ""
}

# 目标优先级：命令行 -Target > 环境变量 > .env 的 PR_TARGET_HOST > 脚本默认（Gitee）
function Resolve-PrTargets {
    param([string]$Requested)

    $value = $Requested
    if ([string]::IsNullOrWhiteSpace($value)) { $value = $env:PR_TARGET_HOST }
    if ([string]::IsNullOrWhiteSpace($value)) { $value = Read-DotEnvValue -Key "PR_TARGET_HOST" }
    if ([string]::IsNullOrWhiteSpace($value)) { $value = $defaultTarget }

    switch ($value.Trim().ToLowerInvariant()) {
        "gitee" { return @("gitee") }
        "github" { return @("github") }
        "both" { return @("gitee", "github") }
        default { throw "PR 目标只能是 gitee、github 或 both，当前配置是：$value" }
    }
}

# 子命令输出直接打到控制台，函数只返回退出码，避免调用方把输出和退出码混在一个变量里。
function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    if ($DryRun) {
        Write-Host "    [dry-run] $FilePath $($Arguments -join ' ')"
        return 0
    }

    & $FilePath @Arguments | Out-Host
    return $LASTEXITCODE
}

function Get-BaseRefName {
    param(
        [Parameter(Mandatory = $true)][string]$RemoteName,
        [Parameter(Mandatory = $true)][string]$BaseBranch
    )

    return "refs/remotes/$RemoteName/$BaseBranch"
}

# PR 说明默认由"相对目标分支的提交列表"生成，保证评审能看到这批提交的实际内容。
function Get-ChangePullRequestBody {
    param(
        [Parameter(Mandatory = $true)][string]$RemoteName,
        [Parameter(Mandatory = $true)][string]$BaseBranch
    )

    $baseRef = Get-BaseRefName -RemoteName $RemoteName -BaseBranch $BaseBranch
    $resolved = & git rev-parse --verify --quiet $baseRef 2>$null
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return "无法定位基准分支 $baseRef，请在 PR 里补充变更说明。"
    }

    $subjects = @(& git log --pretty=format:"- %s" "$baseRef..HEAD" 2>$null)
    if ($subjects.Count -eq 0) {
        return "本分支相对 $baseRef 没有新提交。"
    }
    return ((@("本分支相对 $baseRef 的提交：", "") + $subjects) -join [Environment]::NewLine)
}

function Get-GiteePullRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$HeadBranch
    )

    $json = & gitee api "/repos/$Repository/pulls?state=open&per_page=100" --json
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }

    foreach ($pull in @($json | ConvertFrom-Json)) {
        foreach ($candidate in @(
            "$($pull.head.ref)",
            "$($pull.head.label)",
            "$($pull.head_branch)",
            "$($pull.source_branch)")) {
            if ($candidate -eq $HeadBranch) {
                return @{
                    Number = [int]$pull.number
                    Url = "$($pull.html_url)"
                }
            }
        }
    }
    return $null
}

function Get-GithubPullRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$HeadBranch
    )

    $json = & gh pr view $HeadBranch --repo $Repository --json number,url 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }

    $pull = $json | ConvertFrom-Json
    return @{
        Number = [int]$pull.number
        Url = "$($pull.url)"
    }
}

function Get-ExistingPullRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$HeadBranch
    )

    if ($DryRun) { return $null }
    if ($Platform -eq "gitee") {
        return Get-GiteePullRequest -Repository $Repository -HeadBranch $HeadBranch
    }
    return Get-GithubPullRequest -Repository $Repository -HeadBranch $HeadBranch
}

function New-ChangePullRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$RemoteName,
        [Parameter(Mandatory = $true)][string]$HeadBranch,
        [Parameter(Mandatory = $true)][string]$BaseBranch,
        [string]$BodyText = ""
    )

    Write-Step "拉取 $RemoteName/$BaseBranch 作为 PR 基准"
    $fetchExit = Invoke-External -FilePath "git" -Arguments @(
        "fetch", $RemoteName, "refs/heads/$BaseBranch`:$(Get-BaseRefName -RemoteName $RemoteName -BaseBranch $BaseBranch)")
    if ($fetchExit -ne 0) {
        throw "拉取 $RemoteName/$BaseBranch 失败"
    }

    # 先推送再查 PR：分支被改写（amend / rebase / 补提交）时，PR 上要看到的是最新一次推送的内容。
    Write-Step "推送分支 $HeadBranch 到 $RemoteName"
    $pushArguments = @("push")
    if ($Force) { $pushArguments += "--force-with-lease" }
    $pushArguments += @($RemoteName, "HEAD:refs/heads/$HeadBranch")
    $pushExit = Invoke-External -FilePath "git" -Arguments $pushArguments
    if ($pushExit -ne 0) {
        throw "推送分支失败：$RemoteName $HeadBranch"
    }

    $existing = Get-ExistingPullRequest `
        -Platform $Platform `
        -Repository $Repository `
        -HeadBranch $HeadBranch
    if ($null -ne $existing) {
        Write-Host "    PR 已存在：#$($existing.Number) $($existing.Url)"
        return $existing
    }

    if ([string]::IsNullOrWhiteSpace($BodyText)) {
        $BodyText = Get-ChangePullRequestBody -RemoteName $RemoteName -BaseBranch $BaseBranch
    }

    Write-Step "创建 $Platform PR：$HeadBranch → $BaseBranch"
    $arguments = @(
        "pr", "create",
        "--repo", $Repository,
        "--base", $BaseBranch,
        "--head", $HeadBranch,
        "--title=$Title",
        # 用 --body=<值> 的写法：PR 说明是多行文本，可能以 "-" 开头，
        # 拆成两个参数时 Gitee/gh 的解析器会把它当成选项。
        "--body=$BodyText")

    $cli = if ($Platform -eq "gitee") { "gitee" } else { "gh" }
    $createExit = Invoke-External -FilePath $cli -Arguments $arguments
    if ($createExit -ne 0) {
        throw "创建 $Platform PR 失败"
    }
    if ($DryRun) { return $null }

    return Get-ExistingPullRequest `
        -Platform $Platform `
        -Repository $Repository `
        -HeadBranch $HeadBranch
}

function Merge-ChangePullRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][int]$Number
    )

    Write-Step "以 rebase 方式合并 $Platform PR #$Number（保留每个提交）"
    if ($Approve -and $Platform -eq "gitee") {
        # Gitee 默认要求审查与测试都通过才允许合并；单人多仓场景下由本账号补上标记。
        $null = Invoke-External -FilePath "gitee" -Arguments @(
            "pr", "approve", "--force", "--repo", $Repository, "$Number")
        $null = Invoke-External -FilePath "gitee" -Arguments @(
            "pr", "test", "--force", "--repo", $Repository, "$Number")
    }

    $cli = if ($Platform -eq "gitee") { "gitee" } else { "gh" }
    $exit = Invoke-External -FilePath $cli -Arguments @(
        "pr", "merge", "--repo", $Repository, "--rebase", "$Number")
    if ($exit -ne 0) {
        throw "合并 $Platform PR #$Number 失败（可能要求先审查或测试通过）"
    }
}

function Sync-BaseBranchToRemote {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRemote,
        [Parameter(Mandatory = $true)][string]$TargetRemote
    )

    $baseRef = Get-BaseRefName -RemoteName $SourceRemote -BaseBranch $Base
    Write-Step "同步 $Base 分支：$SourceRemote → $TargetRemote"
    $fetchExit = Invoke-External -FilePath "git" -Arguments @(
        "fetch", $SourceRemote, "refs/heads/$Base`:$baseRef")
    if ($fetchExit -ne 0) {
        throw "拉取 $SourceRemote/$Base 失败"
    }
    $pushExit = Invoke-External -FilePath "git" -Arguments @(
        "push", $TargetRemote, "$baseRef`:refs/heads/$Base")
    if ($pushExit -ne 0) {
        throw "同步 $TargetRemote/$Base 失败"
    }
}

if (git status --porcelain --untracked-files=all) {
    throw "工作区不干净：先把改动提交到功能分支再提 PR"
}

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -eq "HEAD") {
    throw "当前是分离头指针状态，先切到功能分支再提 PR"
}
if ($branch -eq $Base) {
    throw "当前分支就是 $Base：改动请提交到功能分支，再通过 PR 合并"
}
if ([string]::IsNullOrWhiteSpace($Title)) {
    throw "必须用 -Title 给出 PR 标题"
}

$pullRequestBody = ""
if (-not [string]::IsNullOrWhiteSpace($BodyFile)) {
    if (-not (Test-Path -LiteralPath $BodyFile)) {
        throw "找不到 PR 说明文件：$BodyFile"
    }
    $pullRequestBody = [System.IO.File]::ReadAllText(
        (Resolve-Path -LiteralPath $BodyFile).Path,
        [System.Text.Encoding]::UTF8)
}

$targets = Resolve-PrTargets -Requested $Target
Write-Host "PR 目标：$($targets -join ' → ')（分支 $branch → $Base）"

$created = @()
foreach ($platform in $targets) {
    if ($platform -eq "gitee") {
        $slug = $GiteeRepository
        $remoteName = $GiteeRemote
        $null = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
    }
    else {
        $slug = $GithubRepository
        $remoteName = $GithubRemote
    }

    $pull = New-ChangePullRequest `
        -Platform $platform `
        -Repository $slug `
        -RemoteName $remoteName `
        -HeadBranch $branch `
        -BaseBranch $Base `
        -BodyText $pullRequestBody
    $created += [pscustomobject]@{ Platform = $platform; Pull = $pull }
}

if ($Merge) {
    foreach ($item in $created) {
        if ($null -eq $item.Pull) {
            throw "$($item.Platform) PR 没有取到编号，无法自动合并；请手工合并"
        }
        $slug = if ($item.Platform -eq "gitee") { $GiteeRepository } else { $GithubRepository }
        Merge-ChangePullRequest `
            -Platform $item.Platform `
            -Repository $slug `
            -Number $item.Pull.Number
    }

    if (-not $NoSync) {
        if ($created[-1].Platform -eq "gitee") {
            Sync-BaseBranchToRemote -SourceRemote $GiteeRemote -TargetRemote $GithubRemote
        }
        else {
            Sync-BaseBranchToRemote -SourceRemote $GithubRemote -TargetRemote $GiteeRemote
        }
    }
}

Write-Host ""
foreach ($item in $created) {
    $url = if ($null -eq $item.Pull) { "（dry-run，未创建）" } else { $item.Pull.Url }
    Write-Host "$($item.Platform)： $url"
}
if (-not $Merge) {
    Write-Host "PR 已提交，合并时用 rebase（保留每个提交），合并后同步另一个远端；也可以加 -Merge 一次做完"
}
