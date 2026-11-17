[CmdletBinding()]
param(
    [string]$Repo = 'PackingProof/PackingProof-Desktop',
    [string]$Version = ''
)

# 只读校验：不动任何发布内容。
#
# 本脚本校验目标版本在两个平台是否带齐更新清单和增量包，以及下载内容是否一致。
# 0.0.73 / 0.0.74 的分页挑选兼容逻辑仍保留在应用端，但不再作为发布阻断条件。

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AnonymousHeaders = @{ 'User-Agent' = 'PackingProof-UpdateReachability' }
$GiteeHeaders = @{ 'User-Agent' = 'PackingProof-UpdateReachability' }
$GithubHeaders = @{ 'User-Agent' = 'PackingProof-UpdateReachability' }
$ManifestPattern = '^update_v.*\.json$'
$Failures = [System.Collections.Generic.List[string]]::new()
$Warnings = [System.Collections.Generic.List[string]]::new()

function Get-ConfiguredToken([string]$Names) {
    foreach ($Name in $Names.Split(',')) {
        $Value = [Environment]::GetEnvironmentVariable($Name.Trim())
        if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.Trim() }

        $EnvFile = Join-Path $RepoRoot '.env'
        if (Test-Path -LiteralPath $EnvFile) {
            foreach ($Line in [System.IO.File]::ReadAllLines($EnvFile, [System.Text.Encoding]::UTF8)) {
                if ($Line -match "^\s*$([regex]::Escape($Name.Trim()))\s*=\s*(.+)$") {
                    return $Matches[1].Trim().Trim('"', "'")
                }
            }
        }
    }

    # 本机约定：发布用的 Gitee 令牌也会单独存在用户目录里（只对 Gitee 生效）
    if ($Names -match 'GITEE_TOKEN') {
        $KeyFile = Join-Path $env:USERPROFILE '.gitee\apikey.txt'
        if (Test-Path -LiteralPath $KeyFile) {
            $Value = ([System.IO.File]::ReadAllText($KeyFile, [System.Text.Encoding]::UTF8)).Trim()
            if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value }
        }
    }

    return ''
}

# Gitee 匿名 API 很容易限流（现场校验时就是 403 Rate Limit Exceeded），有令牌就带上
$GiteeToken = Get-ConfiguredToken 'GITEE_RELEASE_TOKEN,GITEE_TOKEN'
if ($GiteeToken) { $GiteeHeaders['Authorization'] = "token $GiteeToken" }
$GithubToken = Get-ConfiguredToken 'GITHUB_RELEASE_TOKEN,GH_TOKEN,GITHUB_TOKEN'
if ($GithubToken) { $GithubHeaders['Authorization'] = "Bearer $GithubToken" }

function Write-Pass([string]$Message) {
    Write-Host "[ OK ] $Message" -ForegroundColor Green
}

function Write-Fail([string]$Message) {
    $Failures.Add($Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Write-Warn([string]$Message) {
    $Warnings.Add($Message)
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Get-ManifestAssets($Release) {
    if ($null -eq $Release) { return @() }
    return @($Release.assets | Where-Object { $_.name -match $ManifestPattern })
}

function Get-ReleaseList([string]$Platform, [string]$Uri, [hashtable]$Headers) {
    try {
        return (Invoke-RestMethod -Uri $Uri -Headers $Headers -TimeoutSec 60)
    }
    catch {
        if ($Headers.ContainsKey('Authorization')) {
            Write-Warn "$Platform 列表带令牌请求失败（$($_.Exception.Message)），改用匿名请求重试"
            return (Invoke-RestMethod -Uri $Uri -Headers $AnonymousHeaders -TimeoutSec 60)
        }
        throw
    }
}

function Get-TagVersion([string]$Tag) {
    return ($Tag -replace '^v', '')
}

Write-Host "校验 $Repo 的更新可达性（只读）"

try {
    $GiteeReleases = Get-ReleaseList 'Gitee' "https://gitee.com/api/v5/repos/$Repo/releases?per_page=100" $GiteeHeaders
}
catch {
    Write-Fail "取不到 Gitee release 列表，无法完成校验：$($_.Exception.Message)"
    exit 2
}

try {
    $GithubReleases = Get-ReleaseList 'GitHub' "https://api.github.com/repos/$Repo/releases?per_page=100" $GithubHeaders
}
catch {
    Write-Fail "取不到 GitHub release 列表，无法完成校验：$($_.Exception.Message)"
    exit 2
}
Write-Host ("Gitee release 数 = {0}；GitHub release 数 = {1}" -f $GiteeReleases.Count, $GithubReleases.Count)

if (-not $Version) {
    # GitHub 是"新 → 旧"返回，第一个带更新清单的就是当前最新的 Windows 版本
    $Newest = $GithubReleases | Where-Object { (Get-ManifestAssets $_).Count -gt 0 } | Select-Object -First 1
    if ($null -eq $Newest) {
        Write-Fail 'GitHub 上找不到任何带 update_v*.json 的版本，无法确定目标版本'
        exit 1
    }
    $Version = $Newest.tag_name
}
$Version = $Version.TrimStart('v')
$Tag = "v$Version"
Write-Host "目标版本：$Tag"

$GiteeRelease = $GiteeReleases | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
$GithubRelease = $GithubReleases | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
if ($null -eq $GiteeRelease) { Write-Fail "Gitee 上没有 $Tag 这个 release" } else { Write-Pass "Gitee 上存在 $Tag" }
if ($null -eq $GithubRelease) { Write-Fail "GitHub 上没有 $Tag 这个 release" } else { Write-Pass "GitHub 上存在 $Tag" }

$ManifestName = "update_v$Version.json"

function Test-ReleaseAssets([string]$Platform, $Release) {
    if ($null -eq $Release) { return }
    $Manifest = @($Release.assets | Where-Object { $_.name -eq $ManifestName })
    if ($Manifest.Count -eq 0) {
        Write-Fail "$Platform 的 $Tag 缺少 $ManifestName（老客户端会直接跳过这一版）"
    }
    else {
        Write-Pass "$Platform 的 $Tag 带 $ManifestName"
    }
}

Test-ReleaseAssets 'Gitee' $GiteeRelease
Test-ReleaseAssets 'GitHub' $GithubRelease

# 清单里的 size / sha256 必须和真实增量包一致，而且两个平台指向同一份内容
$GiteeManifest = @((Get-ManifestAssets $GiteeRelease))[0]
$GithubManifest = @((Get-ManifestAssets $GithubRelease))[0]
if ($null -ne $GiteeManifest -and $null -ne $GithubManifest) {
    $GiteeChecksum = $null
    $GithubChecksum = $null
    foreach ($Item in @(
            @{ Platform = 'Gitee'; Release = $GiteeRelease; Manifest = $GiteeManifest },
            @{ Platform = 'GitHub'; Release = $GithubRelease; Manifest = $GithubManifest })) {
        # 清单与增量包都是公开附件，不带令牌（令牌失效时也不会把校验卡住）
        $Json = Invoke-RestMethod -Uri $Item.Manifest.browser_download_url -Headers $AnonymousHeaders -TimeoutSec 60
        $Package = $Json.patch_package
        if ($null -eq $Package -or -not $Package.url) {
            Write-Fail "$($Item.Platform) 的清单没有 patch_package.url，老客户端无法增量更新"
            continue
        }
        $HasGithub = [bool]$Package.github_url
        $HasGitee = [bool]$Package.gitee_url
        if (-not $HasGitee) {
            Write-Fail "$($Item.Platform) 的清单缺少 gitee_url，国内网络下没有镜像可退"
        }
        if (-not $HasGithub) {
            Write-Warn "$($Item.Platform) 的清单缺少 github_url，只剩 Gitee 一个下载入口"
        }
        $DeclaredPatchUrl = if ($Item.Platform -eq 'Gitee' -and $HasGitee) {
            [string]$Package.gitee_url
        }
        elseif ($Item.Platform -eq 'GitHub' -and $HasGithub) {
            [string]$Package.github_url
        }
        else {
            [string]$Package.url
        }
        $PatchName = [System.IO.Path]::GetFileName(([uri]$DeclaredPatchUrl).AbsolutePath)
        $PatchAsset = @($Item.Release.assets | Where-Object { $_.name -eq $PatchName }) | Select-Object -First 1
        if ($null -eq $PatchAsset) {
            Write-Fail "$($Item.Platform) 的清单声明补丁 $PatchName，但该 Release 没有这个附件"
        }
        else {
            Write-Pass "$($Item.Platform) 的清单引用补丁 $PatchName"
            $PatchBytes = (Invoke-WebRequest -Uri $PatchAsset.browser_download_url -Headers $AnonymousHeaders -TimeoutSec 300).Content
            $Hash = [System.BitConverter]::ToString(
                [System.Security.Cryptography.SHA256]::HashData([byte[]]$PatchBytes)).Replace('-', '').ToLowerInvariant()
            if ($Hash -ne $Package.sha256) {
                Write-Fail "$($Item.Platform) 的清单 sha256 与实际增量包不一致（清单 $($Package.sha256)，实际 $Hash）"
            }
            elseif ([int64]$PatchBytes.Length -ne [int64]$Package.size) {
                Write-Fail "$($Item.Platform) 的清单 size 与实际增量包不一致（清单 $($Package.size)，实际 $($PatchBytes.Length)）"
            }
            else {
                Write-Pass "$($Item.Platform) 的清单 sha256/size 与实际增量包一致"
            }
        }
        if ($Item.Platform -eq 'Gitee') { $GiteeChecksum = $Package.sha256 } else { $GithubChecksum = $Package.sha256 }
    }
    if ($GiteeChecksum -and $GithubChecksum -and $GiteeChecksum -ne $GithubChecksum) {
        Write-Fail '两个平台的清单指向的增量包不是同一份（sha256 不一致）'
    }
}

if ($Failures.Count -gt 0) {
    Write-Host ("校验未通过：{0} 项失败，{1} 项警告" -f $Failures.Count, $Warnings.Count) -ForegroundColor Red
    exit 1
}

Write-Host ("校验通过：{0} 项警告" -f $Warnings.Count) -ForegroundColor Green
exit 0
