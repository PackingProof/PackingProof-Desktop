# Release authentication for the repository publisher accounts.
# Tokens are imported into the current process only and are never printed.

function Read-ReleaseEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Key
    )

    $envPath = Join-Path $RepoRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        return ""
    }

    foreach ($line in [System.IO.File]::ReadAllLines($envPath, [System.Text.Encoding]::UTF8)) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.*)$") {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return ""
}

function ConvertTo-ReleaseBase64Url {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    return [Convert]::ToBase64String($Bytes)
        .TrimEnd("=")
        .Replace("+", "-")
        .Replace("/", "_")
}

function Get-GitHubReleaseBotDirectory {
    $configured = $env:PACKINGPROOF_GITHUB_RELEASE_BOT_DIR
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        return $configured.Trim()
    }

    return Join-Path $env:USERPROFILE ".config\PackingProof\github-apps\packingproof-release-bot"
}

function New-GitHubAppJwt {
    param(
        [Parameter(Mandatory = $true)][string]$AppId,
        [Parameter(Mandatory = $true)][string]$PrivateKey
    )

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($PrivateKey)
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $header = @{ alg = "RS256"; typ = "JWT" } | ConvertTo-Json -Compress
        $payload = [ordered]@{
            iat = $now - 60
            exp = $now + 540
            iss = $AppId
        } | ConvertTo-Json -Compress
        $unsigned = "$(ConvertTo-ReleaseBase64Url ([Text.Encoding]::UTF8.GetBytes($header))).$(ConvertTo-ReleaseBase64Url ([Text.Encoding]::UTF8.GetBytes($payload)))"
        $signature = $rsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($unsigned),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        return "$unsigned.$(ConvertTo-ReleaseBase64Url $signature)"
    }
    finally {
        $rsa.Dispose()
    }
}

function Import-GitHubReleaseAppInstallationToken {
    $botRoot = Get-GitHubReleaseBotDirectory
    $appPath = Join-Path $botRoot "app.json"
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        return ""
    }

    $app = Get-Content -LiteralPath $appPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $privateKeyFile = [string]$app.private_key_file
    if ([string]::IsNullOrWhiteSpace($privateKeyFile)) {
        $privateKeyFile = "private-key.pem"
    }
    $privateKeyPath = if ([IO.Path]::IsPathRooted($privateKeyFile)) {
        $privateKeyFile
    } else {
        Join-Path $botRoot $privateKeyFile
    }
    if (-not (Test-Path -LiteralPath $privateKeyPath -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace([string]$app.app_id) -or
        [string]::IsNullOrWhiteSpace([string]$app.installation_id)) {
        return ""
    }

    $privateKey = [IO.File]::ReadAllText($privateKeyPath, [Text.Encoding]::UTF8)
    $jwt = New-GitHubAppJwt -AppId ([string]$app.app_id) -PrivateKey $privateKey
    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "PackingProof-Release-Bot"
        "X-GitHub-Api-Version" = "2022-11-28"
        Authorization = "Bearer $jwt"
    }

    # GitHub App 与网页/CLI 使用独立认证；避免失效的本地代理阻断 API。
    $noProxyItems = @($env:NO_PROXY, "github.com", "api.github.com") |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $env:NO_PROXY = ($noProxyItems -join ",")

    $tokenResult = Invoke-RestMethod `
        -Method Post `
        -Uri "https://api.github.com/app/installations/$($app.installation_id)/access_tokens" `
        -Headers $headers `
        -ContentType "application/json" `
        -Body "{}" `
        -TimeoutSec 30
    $token = [string]$tokenResult.token
    if ([string]::IsNullOrWhiteSpace($token)) {
        return ""
    }

    $env:GH_TOKEN = $token
    $env:GITHUB_TOKEN = $token
    return "GitHub App PackingProof Release Bot"
}

function Import-GitHubReleaseTokenFromEnvFile {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    foreach ($name in @("GITHUB_RELEASE_TOKEN", "GH_TOKEN", "GITHUB_TOKEN")) {
        $token = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $env:GH_TOKEN = $token.Trim()
            $env:GITHUB_TOKEN = $token.Trim()
            return "环境变量 $name"
        }

        $token = Read-ReleaseEnvValue -RepoRoot $RepoRoot -Key $name
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $env:GH_TOKEN = $token
            $env:GITHUB_TOKEN = $token
            return ".env $name"
        }
    }

    try {
        return Import-GitHubReleaseAppInstallationToken
    }
    catch {
        Write-Warning "GitHub Release Bot App 认证失败：$($_.Exception.Message)"
        return ""
    }
}

function Test-GitHubReleaseAuthentication {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        return $false
    }

    $pushPermission = & gh api "repos/$Repository" --jq ".permissions.push" 2>$null
    return $LASTEXITCODE -eq 0
        -and [string]::Equals(
            "$pushPermission".Trim(),
            "true",
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Enable-ReleaseBotGitTransport {
    param([Parameter(Mandatory = $true)][string[]]$Platforms)

    $configs = [System.Collections.Generic.List[object]]::new()
    if ($Platforms -contains "github") {
        $githubSource = Import-GitHubReleaseAppInstallationToken
        if ([string]::IsNullOrWhiteSpace($githubSource)) {
            throw "GitHub 远端写入必须使用 PackingProof Release Bot 的 GitHub App token；个人 gh 登录态和普通 GITHUB_TOKEN 不会用于推送"
        }
        $githubToken = [Uri]::EscapeDataString($env:GH_TOKEN)
        $configs.Add(@{
            Key = "url.https://x-access-token:$githubToken@github.com/.insteadOf"
            Value = "https://github.com/"
        })
    }
    if ($Platforms -contains "gitee") {
        $giteeSource = Import-GiteeReleaseBotToken
        if ([string]::IsNullOrWhiteSpace($giteeSource)) {
            throw "Gitee 远端写入必须使用 PackingProof-Release-Bot token 文件；个人 gitee 登录态和普通 .env token 不会用于推送"
        }
        $giteeUsername = $env:PACKINGPROOF_GITEE_RELEASE_BOT_USERNAME
        if ([string]::IsNullOrWhiteSpace($giteeUsername)) {
            $giteeUsername = "PackingProof-Release-Bot"
        }
        $giteeUser = [Uri]::EscapeDataString($giteeUsername.Trim())
        $giteeToken = [Uri]::EscapeDataString($env:GITEE_TOKEN)
        $configs.Add(@{
            Key = "url.https://$giteeUser`:$giteeToken@gitee.com/.insteadOf"
            Value = "https://gitee.com/"
        })
    }

    [Environment]::SetEnvironmentVariable(
        "GIT_CONFIG_COUNT",
        [string]$configs.Count,
        [EnvironmentVariableTarget]::Process)
    for ($index = 0; $index -lt $configs.Count; $index++) {
        [Environment]::SetEnvironmentVariable(
            "GIT_CONFIG_KEY_$index",
            [string]$configs[$index].Key,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "GIT_CONFIG_VALUE_$index",
            [string]$configs[$index].Value,
            [EnvironmentVariableTarget]::Process)
    }
    # 本地 pre-push hook 只放行经过本函数配置机器人认证的推送。
    $env:PACKINGPROOF_RELEASE_BOT = "1"
}
