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
    # Invoke-RestMethod 把 JSON 数组当成一个对象返回，必须先落到变量再包数组，
    # 否则 @(Invoke-RestMethod ...) 会包成"只含一个数组元素"的数组。
    $attachments = Invoke-RestMethod `
        -Uri "https://gitee.com/api/v5/repos/$Repository/releases/$ReleaseId/attach_files" `
        -Headers $headers `
        -Method Get `
        -TimeoutSec 30
    if ($null -eq $attachments) {
        return @()
    }
    return @($attachments)
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

# Release 是否存在。Gitee 没有单独的存在性接口，这里用不抛错的请求按状态码判断；
# 404 只是"还没建 Release"，其余状态码是真实故障，必须抛出来。
function Test-GiteeReleaseExists {
    param(
        [Parameter(Mandatory = $true)] [string]$Repository,
        [Parameter(Mandatory = $true)] [string]$Tag
    )

    if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        throw "缺少 GITEE_TOKEN，无法读取 Gitee Release 状态"
    }

    $headers = @{ Authorization = "Bearer $($env:GITEE_TOKEN)" }
    $statusCode = 0
    $response = Invoke-RestMethod `
        -Uri "https://gitee.com/api/v5/repos/$Repository/releases/tags/$Tag" `
        -Headers $headers `
        -Method Get `
        -TimeoutSec 30 `
        -SkipHttpErrorCheck `
        -StatusCodeVariable statusCode

    if ($statusCode -eq 404) {
        return $false
    }
    if ($null -eq $response -and $statusCode -eq 0) {
        throw "读取 Gitee Release $Tag 失败：没有拿到响应"
    }
    if ($statusCode -lt 200 -or $statusCode -ge 300) {
        throw "读取 Gitee Release $Tag 失败：HTTP $statusCode"
    }
    return $true
}

# 重发同一版本时，Gitee 会保留旧附件、再放一份同名文件（它不支持覆盖上传）。
# 上传前统一走这里：Release 还没建（首次发布）时不算错误，直接跳过。
function Remove-GiteeReleaseAttachmentsForTag {
    param(
        [Parameter(Mandatory = $true)] [string]$Repository,
        [Parameter(Mandatory = $true)] [string]$Tag,
        [Parameter(Mandatory = $true)] [string[]]$FileNames
    )

    if (-not (Test-GiteeReleaseExists -Repository $Repository -Tag $Tag)) {
        Write-Host "Gitee Release $Tag 还不存在，跳过旧附件清理"
        return
    }
    $releaseId = Get-GiteeReleaseId -Repository $Repository -Tag $Tag

    foreach ($fileName in $FileNames) {
        $removed = Remove-GiteeReleaseAttachmentByName `
            -Repository $Repository `
            -ReleaseId $releaseId `
            -FileName $fileName
        if ($removed -gt 0) {
            Write-Host "Gitee 旧附件已删除 $removed 个：$fileName"
        }
    }
}
