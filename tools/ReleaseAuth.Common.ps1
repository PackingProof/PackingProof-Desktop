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

    return ""
}

function Test-GitHubReleaseAuthentication {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        $null = Import-GitHubReleaseTokenFromEnvFile -RepoRoot $RepoRoot
    }
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
