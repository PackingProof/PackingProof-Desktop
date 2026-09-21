# 把当前 tag 的桌面端产物发布到 GitHub 与 Gitee Release。
#
#   pwsh -NoProfile -File Tools\Publish-Releases.ps1 [-NotesFile <release-notes-file>] `
#       [-Title "<一句话内容>"] [-Prerelease] [-ConfirmCommitCoverage] `
#       [-UpdateNotes] [-ReplaceAssets] [-ValidateOnly]
#
# 约定（与 docs/development/RELEASE_AND_RUNTIME.md 的资产表一致）：
# - GitHub：Setup、update JSON、可选 AppPatch；仅新启动器基线时附 LauncherPatch
# - Gitee：update JSON、可选 AppPatch；仅新启动器基线时附 LauncherPatch，不上传 Setup
# - 完整 7z 与完整 ZIP 是本地产物，任何渠道都不上传
# - 产物必须已由 Tools\Publish-CleanPackage.ps1 生成并通过校验
# - 标题固定 `v<X.Y.Z> <一句话内容>`，两个平台保持一致
# - 发布笔记默认取 package 产物目录里的 RELEASE_NOTES_v<X.Y.Z>.md，且必须覆盖
#   release_commits_v<X.Y.Z>.txt 里的全部提交；确认后加 -ConfirmCommitCoverage
# - 已经发布过的版本要只更新正文时，用 -UpdateNotes 重跑
# - 已经发布过的版本要换附件（例如补写更新清单里的启动器摘要）时加 -ReplaceAssets；
#   Gitee 会先删掉同名附件再上传，不会留重复文件
# - GitHub 用 gh、Gitee 用 gitee CLI；Gitee 令牌固定取 .env 的 GITEE_TOKEN
#   注入环境变量后交给 CLI，脚本不打印也不落盘凭据

param(
    [Parameter(Position = 0)]
    [string]$NotesFile = "",
    [string]$Tag = "",
    [string]$Title = "",
    [switch]$Prerelease,
    [switch]$ConfirmCommitCoverage,
    [switch]$UpdateNotes,
    [switch]$ReplaceAssets,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$repoSlug = "PackingProof/PackingProof-Desktop"

. (Join-Path $PSScriptRoot "GiteeAuth.Common.ps1")
. (Join-Path $PSScriptRoot "ReleaseVersion.Common.ps1")
. (Join-Path $PSScriptRoot "ReleaseNotes.Common.ps1")

function Assert-Command {
    param([string]$Name, [string]$Hint)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "找不到 $Name，$Hint"
    }
}

function Test-IsInsidePackageDir {
    param([string]$Path, [string]$RepoRoot)

    $packageRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "package"))
    return $Path.StartsWith(($packageRoot.TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-LocalFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# 只挑内容真的变了的附件：GitHub 附件带 sha256 可以直接比对，
# Gitee 附件只有大小，用大小判断（更新清单这类文本文件大小一定会变）。
function Get-StaleGitHubAssets {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string[]]$Assets
    )

    $assetsJson = & gh release view $Tag --repo $Repository --json assets
    if ($LASTEXITCODE -ne 0) {
        throw "读取 GitHub Release 附件失败"
    }

    $uploadedHashes = @{}
    foreach ($asset in @((($assetsJson | ConvertFrom-Json).assets))) {
        $digest = "$($asset.digest)"
        $uploadedHashes["$($asset.name)"] = if ($digest.StartsWith("sha256:", [System.StringComparison]::OrdinalIgnoreCase)) {
            $digest.Substring(7).ToLowerInvariant()
        }
        else {
            ""
        }
    }

    $stale = @()
    foreach ($asset in $Assets) {
        $name = Split-Path -Leaf $asset
        if ($uploadedHashes.ContainsKey($name) -and
            $uploadedHashes[$name] -eq (Get-LocalFileSha256 -Path $asset)) {
            continue
        }
        $stale += $asset
    }
    return $stale
}

function Get-StaleGiteeAssets {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        [Parameter(Mandatory = $true)][string[]]$Assets
    )

    $uploadedSizes = @{}
    $attachments = @(Get-GiteeReleaseAttachments -Repository $Repository -ReleaseId $ReleaseId)
    foreach ($attachment in $attachments) {
        $uploadedSizes["$($attachment.name)"] = [long]$attachment.size
    }

    $stale = @()
    foreach ($asset in $Assets) {
        $name = Split-Path -Leaf $asset
        $size = (Get-Item -LiteralPath $asset).Length
        if ($uploadedSizes.ContainsKey($name) -and $uploadedSizes[$name] -eq $size) {
            continue
        }
        $stale += $asset
    }
    return $stale
}

if (git status --porcelain --untracked-files=all) {
    throw "发布前 Git 工作区必须干净"
}

# 正常发布时 tag 就在当前提交上；补写已发布版本的正文时 HEAD 会领先于 tag，
# 这种情况显式传 -Tag <tag>（脚本会再校验 tag 是否真实存在）。
if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $tag = $Tag.Trim()
    & git rev-parse --verify --quiet "$tag^{commit}" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "找不到 tag：$tag"
    }
}
else {
    $tag = (git describe --tags --exact-match 2>$null)
    if ([string]::IsNullOrWhiteSpace($tag)) {
        throw "当前提交没有精确 tag：请先建 v<X.Y.Z> 标签，或为已发布版本补正文时显式传 -Tag <tag>"
    }
    $tag = $tag.Trim()
}

$headCommit = (git rev-parse HEAD).Trim()
$tagCommit = (git rev-parse "$tag^{commit}").Trim()
$tagMatchesHead = [string]::Equals($headCommit, $tagCommit, [System.StringComparison]::OrdinalIgnoreCase)
if (-not $tagMatchesHead -and -not ($UpdateNotes -or $ValidateOnly)) {
    throw "当前提交不是 $tag 指向的提交：正式发布请切到该 tag 再执行；只补正文用 -UpdateNotes，只校验用 -ValidateOnly"
}

# 产物名由 Tools\Publish-CleanPackage.ps1 按归一化版本生成：目录与补丁包带 v<纯版本号>，
# Setup 与更新清单用不带 v 的纯版本号。这里必须走同一套归一化，不能拿 tag 直接拼：
# tag 少写 v（0.0.67）或带后缀（v0.0.67-rc1）时，产物名仍然是 v0.0.67，按原 tag 找必然落空。
$artifactNames = Get-ReleaseArtifactNames -Tag $tag -RepoRoot $repoRoot
$version = $artifactNames.NormalizedVersion
$releaseTag = $artifactNames.ReleaseTag
$packageRoot = $artifactNames.PackageRoot
if (-not (Test-Path -LiteralPath $packageRoot)) {
    $candidates = @(Get-ChildItem -Path (Join-Path $repoRoot "package") -Directory -Filter "PackingProof+*$version*" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
    $hint = if ($candidates.Count -gt 0) { "；package\ 下与 $version 相关的目录有：$($candidates -join '、')" } else { "" }
    throw "找不到产物目录：$packageRoot，请先执行 Tools\Publish-CleanPackage.ps1$hint"
}

# 发布笔记不入库，必须来自 package 下该版本自己的产物目录；不传路径时按版本自动推断。
$notesFileName = Get-ReleaseNotesFileName -NormalizedVersion $version
$notesFullPath = if ([string]::IsNullOrWhiteSpace($NotesFile)) {
    Join-Path $packageRoot $notesFileName
}
elseif (Test-Path -LiteralPath $NotesFile -PathType Leaf) {
    (Resolve-Path -LiteralPath $NotesFile).Path
}
else {
    throw "找不到发布笔记：$NotesFile"
}
if (-not (Test-IsInsidePackageDir -Path $notesFullPath -RepoRoot $repoRoot)) {
    throw "发布笔记必须放在 package\ 产物目录下：$notesFullPath"
}
$notesProblems = Get-ReleaseNotesProblems -NotesPath $notesFullPath
if ($notesProblems.Count -gt 0) {
    $details = ($notesProblems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
    throw "发布笔记校验未通过：$([Environment]::NewLine)$details"
}

# 按资产表挑文件：Setup 与 update JSON 必须存在，补丁包按本次是否生成决定。
$setupPath = Join-Path $packageRoot $artifactNames.SetupFileName
$updateJsonPath = Join-Path $packageRoot $artifactNames.UpdateJsonFileName
$appPatchPath = Join-Path $packageRoot $artifactNames.AppPatchFileName
$launcherPatchPath = Join-Path $packageRoot $artifactNames.LauncherPatchFileName

foreach ($required in @($setupPath, $updateJsonPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        $existing = @(Get-ChildItem -Path $packageRoot -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        $hint = if ($existing.Count -gt 0) { "；目录内容：$($existing -join '、')" } else { "" }
        throw "缺少必须上传的产物：$required$hint"
    }
}

$githubAssets = @($setupPath, $updateJsonPath)
$giteeAssets = @($updateJsonPath)
if (Test-Path -LiteralPath $appPatchPath) {
    $githubAssets += $appPatchPath
    $giteeAssets += $appPatchPath
}
if (Test-Path -LiteralPath $launcherPatchPath) {
    $githubAssets += $launcherPatchPath
    $giteeAssets += $launcherPatchPath
}

# 标题按文档固定成 v<X.Y.Z> <一句话内容>，用归一化后的版本号而不是原 tag，
# 这样 tag 少写 v 或带后缀时，标题仍然与产物名一致。
if ([string]::IsNullOrWhiteSpace($Title)) {
    if (-not $ValidateOnly) {
        throw "必须用 -Title 写一句话概括本版最核心的变化（例如「采集预览迁移 GPU 与存储判定重做」），不能只写版本号"
    }
    $releaseTitle = $releaseTag
}
else {
    $releaseTitle = "$releaseTag $Title"
}

# 更新清单的标题和摘要以前总要手工补，现在改成发布前必须已经是填好的内容。
# 顺带守卫：产物里已经有 AppPatch 时，清单必须带上它，否则启动器拿不到增量更新。
Assert-UpdateManifestReady -UpdateJsonPath $updateJsonPath -ExpectedTitle $releaseTitle -AppPatchPath $appPatchPath

# 发布笔记必须覆盖上一个正式版以来的全部提交：先生成清单，再要求人工确认。
$previousReleaseTag = Get-PreviousFormalReleaseTag -RepoRoot $repoRoot -ReleaseTag $releaseTag
$commitChecklist = Write-ReleaseCommitChecklist `
    -RepoRoot $repoRoot `
    -PackageRoot $packageRoot `
    -NormalizedVersion $version `
    -FromTag $previousReleaseTag `
    -ToRef $releaseTag

Write-Host ""
Write-Host "提交范围 $($commitChecklist.Range)（上一个正式版：$(if ([string]::IsNullOrWhiteSpace($previousReleaseTag)) { '无' } else { $previousReleaseTag })）"
Write-Host "提交数：$($commitChecklist.Count)，清单：$(Split-Path -Leaf $commitChecklist.Path)"
if (-not $ConfirmCommitCoverage) {
    Write-Host ""
    Write-Host "以下提交都必须能在 $notesFileName 里找到对应说明："
    Get-ReleaseCommitSubjects -RepoRoot $repoRoot -FromTag $previousReleaseTag -ToRef $releaseTag |
        ForEach-Object { Write-Host "    $_" }
    Write-Host ""
    throw "发布笔记尚未确认覆盖上述 $($commitChecklist.Count) 个提交；逐条核对后加 -ConfirmCommitCoverage 重新执行（只想先校验加 -ValidateOnly）"
}

Assert-Command -Name "gh" -Hint "GitHub Release 无法创建；安装后执行 gh auth login"
Assert-Command -Name "gitee" -Hint "Gitee Release 无法创建；安装后执行 gitee auth login --token <token>"

if ($ValidateOnly) {
    Write-Host ""
    Write-Host "仅校验模式：产物、发布笔记与更新清单均通过，未创建、未上传"
    exit 0
}

# Gitee 令牌固定来自 .env；CLI 的登录态可能停在失效的旧身份上，先做一次真实调用确认可用。
$giteeTokenSource = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
if (-not (Test-GiteeAuthentication -Repository $repoSlug -RepoRoot $repoRoot)) {
    $sourceHint = if ($giteeTokenSource) {
        "当前令牌来源：$giteeTokenSource"
    } else {
        "当前既没有 .env 的 GITEE_TOKEN，gitee CLI 的登录态也不可用"
    }
    throw "Gitee 认证失败，$sourceHint；请核对 .env 的 GITEE_TOKEN"
}

Write-Host "发布 $tag"
Write-Host "  标题   $releaseTitle"
Write-Host "  笔记   $notesFullPath"
Write-Host "  Gitee 令牌来源：$(if ($giteeTokenSource) { $giteeTokenSource } else { 'gitee CLI 登录态' })"
Write-Host "  GitHub 资产："
$githubAssets | ForEach-Object { Write-Host "    $(Split-Path -Leaf $_)" }
Write-Host "  Gitee 资产："
$giteeAssets | ForEach-Object { Write-Host "    $(Split-Path -Leaf $_)" }

Write-Host ""
Write-Host "==> GitHub Release"
& gh release view $tag --repo $repoSlug *> $null
if ($LASTEXITCODE -eq 0) {
    if ($UpdateNotes) {
        & gh release edit $tag --repo $repoSlug --title $releaseTitle --notes-file $notesFullPath
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub Release 正文更新失败"
        }
        Write-Host "GitHub 上 $tag 已存在，正文与标题已按发布笔记更新"
    }
    else {
        Write-Host "GitHub 上 $tag 已存在，跳过创建（要更新正文加 -UpdateNotes）"
    }

    if ($ReplaceAssets) {
        $staleGitHubAssets = @(Get-StaleGitHubAssets `
            -Repository $repoSlug `
            -Tag $tag `
            -Assets $githubAssets)
        foreach ($asset in $staleGitHubAssets) {
            & gh release upload $tag $asset --repo $repoSlug --clobber
            if ($LASTEXITCODE -ne 0) {
                throw "GitHub 附件替换失败：$(Split-Path -Leaf $asset)"
            }
        }
        if ($staleGitHubAssets.Count -eq 0) {
            Write-Host "GitHub 附件与本地产物一致，无需替换"
        }
        else {
            Write-Host "GitHub 附件已替换：$(($staleGitHubAssets | ForEach-Object { Split-Path -Leaf $_ }) -join '、')"
        }
    }
}
else {
    $ghArgs = @("release", "create", $tag) + $githubAssets +
        @("--repo", $repoSlug, "--title", $releaseTitle, "--notes-file", $notesFullPath)
    if ($Prerelease) { $ghArgs += "--prerelease" }

    # 公网到 GitHub 偶发连不上，重试几次再判失败。
    $created = $false
    foreach ($attempt in 1..5) {
        & gh @ghArgs
        if ($LASTEXITCODE -eq 0) { $created = $true; break }
        if ($attempt -lt 5) { Start-Sleep -Seconds 10 }
    }
    if (-not $created) {
        throw "GitHub Release 创建失败"
    }
}

Write-Host ""
Write-Host "==> Gitee Release"
& gitee release view $tag --repo $repoSlug *> $null
$giteeReleaseExisted = ($LASTEXITCODE -eq 0)
if ($LASTEXITCODE -eq 0) {
    if ($UpdateNotes) {
        $existingNotesText = Get-Content -LiteralPath $notesFullPath -Raw
        & gitee release edit --repo $repoSlug --name $releaseTitle --notes $existingNotesText $tag *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Gitee Release 正文更新失败"
        }
        Write-Host "Gitee 上 $tag 已存在，正文与标题已按发布笔记更新"
    }
    else {
        Write-Host "Gitee 上 $tag 已存在，跳过创建（要更新正文加 -UpdateNotes）"
    }
}
else {
    $notesText = Get-Content -LiteralPath $notesFullPath -Raw
    $giteeArgs = @(
        "release", "create",
        "--repo", $repoSlug,
        "--tag", $tag,
        "--target", "main",
        "--name", $releaseTitle,
        "--notes", $notesText)
    if ($Prerelease) { $giteeArgs += "--prerelease" }

    & gitee @giteeArgs *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee Release 创建失败"
    }
}

# 只更新正文时不要重复上传附件，避免 Gitee 上出现同名重复文件。
if ($ReplaceAssets -and $giteeReleaseExisted) {
    $giteeReleaseId = Get-GiteeReleaseId -Repository $repoSlug -Tag $tag
    $staleGiteeAssets = @(Get-StaleGiteeAssets `
        -Repository $repoSlug `
        -ReleaseId $giteeReleaseId `
        -Assets $giteeAssets)
    foreach ($asset in $staleGiteeAssets) {
        $assetName = Split-Path -Leaf $asset
        $removed = Remove-GiteeReleaseAttachmentByName `
            -Repository $repoSlug `
            -ReleaseId $giteeReleaseId `
            -FileName $assetName
        Write-Host "Gitee 旧附件 $(if ($removed -gt 0) { "已删除 $removed 个" } else { "不存在" })：$assetName"
        & gitee release upload --repo $repoSlug $tag $asset *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Gitee 附件替换失败：$assetName"
        }
    }
    if ($staleGiteeAssets.Count -eq 0) {
        Write-Host "Gitee 附件与本地产物一致，无需替换"
    }
    else {
        Write-Host "Gitee 附件已替换：$(($staleGiteeAssets | ForEach-Object { Split-Path -Leaf $_ }) -join '、')"
    }
}
elseif ($giteeReleaseExisted -and -not $ReplaceAssets) {
    Write-Host "Gitee 附件保持原样（要替换附件加 -ReplaceAssets）"
}
else {
    foreach ($asset in $giteeAssets) {
        & gitee release upload --repo $repoSlug $tag $asset *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Gitee 附件上传失败：$(Split-Path -Leaf $asset)"
        }
    }
    Write-Host "Gitee 附件已上传（Gitee 会把文件名里的 + 显示成空格，属正常）"
}

Write-Host ""
Write-Host "GitHub 与 Gitee Release 均已就绪：$tag"
Write-Host "Setup 只在 GitHub 提供，Gitee 侧按资产表不上传"
Write-Host ""
Write-Host "还差一步（Gitee 专属，每次发布都要做，漏了 Gitee 就没有双击安装入口）："
Write-Host "  pwsh -NoProfile -File Tools\Publish-NoRuntimePackage.ps1 -Tag $tag -UploadGitee"
