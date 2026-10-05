[CmdletBinding()]
param(
    [string]$Repo = 'PackingProof/PackingProof-Desktop',
    [string]$Version = '',
    [switch]$AllowLegacyBlind,
    [int]$LegacyPageSize = 30
)

# 只读校验：不动任何发布内容。
#
# 现场事故：v0.0.75 发布后，老客户端（0.0.73 / 0.0.74）在 Gitee 上只看得到最旧的
# LegacyPageSize 条 release，新版被挤到第二页 → 判"没有带本平台安装包的版本" → 掉到
# GitHub → 更新清单挂在打不开的 github.com 附件域名上 → 用户只看到"没有连接、没有回应"。
# 本脚本按老客户端的挑选口径复盘"这一版发布之后，用户到底看不看得见、下不下得动"。

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
$GiteeToken = Get-ConfiguredToken 'GITEE_TOKEN'
if ($GiteeToken) { $GiteeHeaders['Authorization'] = "token $GiteeToken" }
$GithubToken = Get-ConfiguredToken 'GITHUB_TOKEN,GH_TOKEN'
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
$PatchName = "PackingProof_AppPatch_v$Version.zip"

function Test-ReleaseAssets([string]$Platform, $Release) {
    if ($null -eq $Release) { return }
    $Manifest = @($Release.assets | Where-Object { $_.name -eq $ManifestName })
    $Patch = @($Release.assets | Where-Object { $_.name -eq $PatchName })
    if ($Manifest.Count -eq 0) {
        Write-Fail "$Platform 的 $Tag 缺少 $ManifestName（老客户端会直接跳过这一版）"
    }
    else {
        Write-Pass "$Platform 的 $Tag 带 $ManifestName"
    }
    if ($Patch.Count -eq 0) {
        Write-Fail "$Platform 的 $Tag 缺少 $PatchName（增量更新下不动）"
    }
    else {
        Write-Pass "$Platform 的 $Tag 带 $PatchName"
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
        $PatchAsset = @($Item.Release.assets | Where-Object { $_.name -eq $PatchName })[0]
        if ($null -ne $PatchAsset) {
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

# 老客户端（0.0.73 / 0.0.74）的挑选口径：Gitee 只看第一页（旧 → 新）、取第一个带更新清单的版本；
# GitHub 是新 → 旧，取第一个带更新清单的版本。
$GiteeFirstPage = @($GiteeReleases | Select-Object -First $LegacyPageSize)
$LegacyPick = @($GiteeFirstPage | Where-Object { (Get-ManifestAssets $_).Count -gt 0 })[0]
$LegacyPickTag = if ($null -eq $LegacyPick) { '<无>' } else { $LegacyPick.tag_name }
$GithubPick = @($GithubReleases | Where-Object { (Get-ManifestAssets $_).Count -gt 0 })[0]
$GithubPickTag = if ($null -eq $GithubPick) { '<无>' } else { $GithubPick.tag_name }
Write-Host "老客户端会挑中的版本：Gitee = $LegacyPickTag，GitHub = $GithubPickTag"

if ($LegacyPickTag -eq $Tag -and $GithubPickTag -eq $Tag) {
    Write-Pass "0.0.73 / 0.0.74 老客户端能看见 $Tag"
}
else {
    $Message = "老客户端（0.0.73 / 0.0.74）挑不到 $Tag（Gitee=$LegacyPickTag、GitHub=$GithubPickTag）；" `
        + 'Gitee 挑不到就会掉到 GitHub，而 github.com 的附件域名在国内常常下不动'
    if ($AllowLegacyBlind) {
        Write-Warn "$Message（已按 -AllowLegacyBlind 放行）"
    }
    else {
        Write-Fail $Message
        $PruneCount = [Math]::Max(0, $GiteeReleases.Count - $LegacyPageSize)
        if ($PruneCount -gt 0) {
            $Prune = @($GiteeReleases | Select-Object -First $PruneCount | ForEach-Object { $_.tag_name })
            Write-Host ("  补救：Gitee 上删掉最老的 {0} 个 release，让 {1} 落进第一页：{2}" -f $PruneCount, $Tag, ($Prune -join ', '))
        }
        $OlderManifests = @($GiteeReleases | Where-Object {
                (Get-ManifestAssets $_).Count -gt 0 -and $_.tag_name -ne $Tag
            } | ForEach-Object { $_.tag_name })
        if ($OlderManifests.Count -gt 0) {
            Write-Host ("  补救：Gitee 上删掉这些更早版本的 update_v*.json，否则老客户端会先挑到它们：{0}" -f ($OlderManifests -join ', '))
        }
    }
}

if ($Failures.Count -gt 0) {
    Write-Host ("校验未通过：{0} 项失败，{1} 项警告" -f $Failures.Count, $Warnings.Count) -ForegroundColor Red
    exit 1
}

Write-Host ("校验通过：{0} 项警告" -f $Warnings.Count) -ForegroundColor Green
exit 0
