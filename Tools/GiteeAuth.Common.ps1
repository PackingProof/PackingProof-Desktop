# Gitee 发布认证的令牌来源：仓库根目录 .env 的 GITEE_TOKEN（AGENTS.md 规定）。
#
# 只把它注入当前进程环境供 gitee CLI 使用，不打印、不写盘、不提交。
# gitee CLI 优先使用 GITEE_TOKEN，其次才用它自己保存的登录态；登录态按身份字符串
# 各存一份，容易停在失效的旧身份上（而且 `gitee auth status` 在令牌失效时仍返回 0），
# 所以发布脚本必须先注入 .env 里的令牌，不能只看 CLI 的登录状态。

function Import-GiteeTokenFromEnvFile {
    param([Parameter(Mandatory = $true)] [string]$RepoRoot)

    if (-not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        return "环境变量"
    }

    $envPath = Join-Path $RepoRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath)) {
        return ""
    }

    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match "^\s*GITEE_TOKEN\s*=\s*(.*)$") {
            $token = $Matches[1].Trim().Trim('"').Trim("'")
            if (-not [string]::IsNullOrWhiteSpace($token)) {
                $env:GITEE_TOKEN = $token
                return ".env"
            }
        }
    }

    return ""
}

function Test-GiteeAuthentication {
    param(
        [Parameter(Mandatory = $true)] [string]$Repository,
        [Parameter(Mandatory = $true)] [string]$RepoRoot
    )

    # 自己负责注入 .env 的令牌，避免调用方忘记导入而回退到失效的 CLI 登录态。
    $null = Import-GiteeTokenFromEnvFile -RepoRoot $RepoRoot

    & gitee release list --repo $Repository *> $null
    return $LASTEXITCODE -eq 0
}

function Get-GiteeReleaseId {
    param(
        [Parameter(Mandatory = $true)] [string]$Repository,
        [Parameter(Mandatory = $true)] [string]$Tag
    )

    if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        throw "缺少 GITEE_TOKEN，无法读取 Gitee Release 附件"
    }

    $headers = @{ Authorization = "Bearer $($env:GITEE_TOKEN)" }
    $release = Invoke-RestMethod `
        -Uri "https://gitee.com/api/v5/repos/$Repository/releases/tags/$Tag" `
        -Headers $headers `
        -Method Get `
        -TimeoutSec 30
    return [long]$release.id
}

function Get-GiteeReleaseAttachments {
    param(
        [Parameter(Mandatory = $true)] [string]$Repository,
        [Parameter(Mandatory = $true)] [long]$ReleaseId
    )

    if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        throw "缺少 GITEE_TOKEN，无法读取 Gitee Release 附件"
    }

    $headers = @{ Authorization = "Bearer $($env:GITEE_TOKEN)" }
    return @(Invoke-RestMethod `
        -Uri "https://gitee.com/api/v5/repos/$Repository/releases/$ReleaseId/attach_files" `
        -Headers $headers `
        -Method Get `
        -TimeoutSec 30)
}

# gitee CLI 只能上传附件、不能删除附件；重复上传会在 Release 上留下同名文件，
# 所以替换附件前先按名字删掉旧的。令牌只用请求头传递，不打印、不落盘。
function Remove-GiteeReleaseAttachmentByName {
    param(
        [Parameter(Mandatory = $true)] [string]$Repository,
        [Parameter(Mandatory = $true)] [long]$ReleaseId,
        [Parameter(Mandatory = $true)] [string]$FileName
    )

    $headers = @{ Authorization = "Bearer $($env:GITEE_TOKEN)" }
    $attachments = Get-GiteeReleaseAttachments -Repository $Repository -ReleaseId $ReleaseId

    $removed = 0
    foreach ($attachment in $attachments) {
        if (-not [string]::Equals(
                "$($attachment.name)",
                $FileName,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        Invoke-RestMethod `
            -Uri "https://gitee.com/api/v5/repos/$Repository/releases/$ReleaseId/attach_files/$($attachment.id)" `
            -Headers $headers `
            -Method Delete `
            -TimeoutSec 30 | Out-Null
        $removed++
    }
    return $removed
}
