# 发布笔记与提交覆盖的统一规则，由打包脚本和发布脚本共用。
#
# 「发布笔记必须覆盖上一个正式版以来的全部提交」以前只写在文档里，全靠人记；
# 实际发布时最容易漏写，所以能自动化的部分全部交给脚本：
#   - 解析上一个正式版 tag，得到提交范围与条数
#   - 在产物目录生成 release_commits_v<X.Y.Z>.txt（逐条核对用的清单）
#   - 生成 / 保留 RELEASE_NOTES_v<X.Y.Z>.md，并校验模板占位符与必备段落
#   - 校验 update_v<X.Y.Z>.json 的标题与摘要是否还是占位内容

function Get-ReleaseNotesFileName {
    param([Parameter(Mandatory = $true)][string]$NormalizedVersion)

    return "RELEASE_NOTES_v$NormalizedVersion.md"
}

function Get-ReleaseCommitListFileName {
    param([Parameter(Mandatory = $true)][string]$NormalizedVersion)

    return "release_commits_v$NormalizedVersion.txt"
}

# 上一个正式版 tag：从当前发布 tag 往前找最近的 v<X.Y.Z>。
# 找不到（例如首个正式版）时返回空字符串，表示按仓库起点统计。
function Get-PreviousFormalReleaseTag {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    $previous = & git -C $RepoRoot describe --tags --abbrev=0 --match "v[0-9]*.[0-9]*.[0-9]*" "$ReleaseTag^" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($previous)) {
        return ""
    }
    return "$previous".Trim()
}

function Get-ReleaseCommitRange {
    param([string]$FromTag, [string]$ToRef = "HEAD")

    if ([string]::IsNullOrWhiteSpace($FromTag)) {
        return $ToRef
    }
    return "$FromTag..$ToRef"
}

function Get-ReleaseCommitSubjects {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$FromTag = "",
        [string]$ToRef = "HEAD"
    )

    $range = Get-ReleaseCommitRange -FromTag $FromTag -ToRef $ToRef
    $output = & git -C $RepoRoot log --no-merges --pretty=format:"%h %s" $range 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "git log 失败：$range"
    }
    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

# 生成逐条核对用的提交清单；文件只留本地，不上传。
function Write-ReleaseCommitChecklist {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$NormalizedVersion,
        [string]$FromTag = "",
        [string]$ToRef = "HEAD"
    )

    $range = Get-ReleaseCommitRange -FromTag $FromTag -ToRef $ToRef
    $subjects = Get-ReleaseCommitSubjects -RepoRoot $RepoRoot -FromTag $FromTag -ToRef $ToRef
    $fromLabel = if ([string]::IsNullOrWhiteSpace($FromTag)) { "（仓库起点）" } else { $FromTag }
    $path = Join-Path $PackageRoot (Get-ReleaseCommitListFileName -NormalizedVersion $NormalizedVersion)

    $lines = @()
    $lines += "发布提交清单（仅本地核对，不上传）"
    $lines += ""
    $lines += "范围：$range"
    $lines += "上一个正式版：$fromLabel"
    $lines += "提交数：$($subjects.Count)"
    $lines += ""
    $lines += "下面每条都要能在 $(Get-ReleaseNotesFileName -NormalizedVersion $NormalizedVersion) 里找到对应说明："
    $lines += "用户能感知的变化写进《功能与体验》《问题修复》，纯工程改动写进《兼容与工程》。"
    $lines += ""
    $lines += $subjects

    [System.IO.File]::WriteAllText(
        $path,
        ($lines -join [Environment]::NewLine) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    return @{
        Path = $path
        Range = $range
        Count = $subjects.Count
        FromTag = $FromTag
    }
}

# 发布笔记的硬性要求：按模板分段，且不能残留占位符。
function Get-ReleaseNotesProblems {
    param([Parameter(Mandatory = $true)][string]$NotesPath)

    $problems = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $NotesPath -PathType Leaf)) {
        $problems.Add("发布笔记不存在：$NotesPath（按 RELEASE_NOTES_TEMPLATE.md 编写，或先运行 Tools\Publish-CleanPackage.ps1 生成骨架）")
        return $problems
    }

    $text = [System.IO.File]::ReadAllText($NotesPath, [System.Text.Encoding]::UTF8)
    foreach ($placeholder in @("<X.Y.Z>", "<模块>", "<一句话描述>", "<仅新启动器基线时保留>")) {
        if ($text.Contains($placeholder, [System.StringComparison]::Ordinal)) {
            $problems.Add("发布笔记还留着模板占位符 $placeholder")
        }
    }
    if ($text.Contains("发布笔记模板", [System.StringComparison]::Ordinal)) {
        $problems.Add("发布笔记看起来还是模板原文（含「发布笔记模板」）")
    }
    foreach ($section in @(
        "## 更新内容",
        "### 功能与体验",
        "### 问题修复",
        "### 兼容与工程",
        "## 下载与更新说明",
        "## 未验证事项")) {
        if (-not $text.Contains($section, [System.StringComparison]::Ordinal)) {
            $problems.Add("发布笔记缺少段落 $section")
        }
    }
    return $problems
}

# update_v<X.Y.Z>.json 里的标题与摘要最容易忘记填写，发布前直接拦下来。
function Assert-UpdateManifestReady {
    param(
        [Parameter(Mandatory = $true)][string]$UpdateJsonPath,
        [Parameter(Mandatory = $true)][string]$ExpectedTitle,
        [string]$AppPatchPath = ""
    )

    if (-not (Test-Path -LiteralPath $UpdateJsonPath -PathType Leaf)) {
        throw "找不到更新清单：$UpdateJsonPath"
    }

    $manifest = Get-Content -LiteralPath $UpdateJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifestTitle = "$($manifest.title)".Trim()
    if ($manifestTitle -ne $ExpectedTitle) {
        throw "更新清单 title 与 Release 标题不一致：清单是「$manifestTitle」，应为「$ExpectedTitle」"
    }

    $notes = @($manifest.notes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($notes.Count -eq 0) {
        throw "更新清单 notes 为空：请填写面向用户的简洁变化说明（启动器直接显示这些内容）"
    }
    foreach ($note in $notes) {
        if ($note.Contains("请填写", [System.StringComparison]::Ordinal)) {
            throw "更新清单 notes 还是占位内容：$note"
        }
    }

    # 产物里已经生成了增量包时，清单必须带上它。现场踩过：打包后手工还原了"生成补丁包之前"
    # 的清单，patch_supported=false / patch_package=null，发布出去的清单不带补丁包，
    # 启动器就永远拿不到增量更新，只能靠人工发现（v0.0.69 出过一次）。
    if (-not [string]::IsNullOrWhiteSpace($AppPatchPath) -and
        (Test-Path -LiteralPath $AppPatchPath -PathType Leaf)) {
        $patchDeclared = ($manifest.patch_supported -eq $true) -and ($null -ne $manifest.patch_package)
        if (-not $patchDeclared) {
            throw "更新清单没有带上已生成的增量包：$UpdateJsonPath 里 patch_supported=$($manifest.patch_supported)、patch_package=$($manifest.patch_package)，但产物目录里存在 $(Split-Path -Leaf $AppPatchPath)。请把补丁包的 type、url、github_url、gitee_url、sha256、size 写进清单（可直接对已生成的补丁包算哈希）后再发布"
        }
    }

    # 启动器里逐条显示，太长没人看：按模块归并到 15-20 条，覆盖所有用户可见变化即可，
    # 工程与内部改动不写进 notes（留在发布笔记的《兼容与工程》）。
    $maxNotes = 20
    if ($notes.Count -gt $maxNotes) {
        throw "更新清单 notes 最多 $maxNotes 条，当前 $($notes.Count) 条：请按模块归并，不要把每个提交各写一条"
    }

    # 启动器虽然会折行，但一条太长折成多行后整页又乱又难读，所以这里卡死单条长度。
    $maxNoteLength = 40
    foreach ($note in $notes) {
        if ($note.Length -gt $maxNoteLength) {
            throw "更新清单 notes 单条最多 $maxNoteLength 个字，当前这条有 $($note.Length) 个字：$note"
        }
    }
}

# 人工写的发布笔记是"版本级"资产，但产物目录名是"提交级"的：标签之外多提交一次、
# 甚至只是工作区变脏，目录名就会多出 -2-g<sha> / -dirty 后缀（见 Publish-CleanPackage.ps1
# 的 Get-GitBuildSuffix）。只按"本次产物目录"找笔记，重打时一定找不到，于是把它当成
# "还没写"换成模板骨架 —— v0.0.74 现场就是这么丢笔记的（5736 字节被换成 4358 字节模板）。
#
# 所以除了本次产物目录，还要在"版本稳定位置"和同版本的历史产物目录里找，取最新的
# 一份非模板正文；找到后打包脚本会在稳定位置留一份，下次重打（换目录名）也不会再丢。
function Get-ReleaseNotesDraftPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$NormalizedVersion
    )

    return Join-Path $RepoRoot ("package\.release-notes\" +
        (Get-ReleaseNotesFileName -NormalizedVersion $NormalizedVersion))
}

# 还没填过的模板骨架不算"保留的笔记"，否则会把骨架当成人工内容抄来抄去。
function Test-ReleaseNotesIsTemplate {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$Text = ""
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $true
    }
    if ($Text.Contains("发布笔记模板", [System.StringComparison]::Ordinal)) {
        return $true
    }

    $templatePath = Join-Path $RepoRoot "RELEASE_NOTES_TEMPLATE.md"
    if (Test-Path -LiteralPath $templatePath -PathType Leaf) {
        $template = [System.IO.File]::ReadAllText($templatePath, [System.Text.Encoding]::UTF8)
        if (-not [string]::IsNullOrWhiteSpace($template) -and
            $Text.Trim() -eq $template.Trim()) {
            return $true
        }
    }
    return $false
}

# 找上一份人工写的发布笔记，返回 Text 与来源 Path；都没找到时 Text 为空。
function Resolve-PreservedReleaseNotes {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$NormalizedVersion,
        [string]$OutputDir = ""
    )

    $notesFileName = Get-ReleaseNotesFileName -NormalizedVersion $NormalizedVersion
    $candidates = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $directPaths = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($OutputDir)) {
        $directPaths.Add((Join-Path $OutputDir $notesFileName))
    }
    $directPaths.Add((Get-ReleaseNotesDraftPath -RepoRoot $RepoRoot -NormalizedVersion $NormalizedVersion))
    foreach ($path in $directPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            if ($seen.Add($item.FullName)) {
                $candidates.Add($item)
            }
        }
    }

    # 同版本的其它产物目录：目录名带提交后缀，重打一次就换一个名字。
    $packagePath = Join-Path $RepoRoot "package"
    if (Test-Path -LiteralPath $packagePath -PathType Container) {
        $versionDirs = @(Get-ChildItem -LiteralPath $packagePath -Directory -Filter "PackingProof+v$NormalizedVersion*" -ErrorAction SilentlyContinue)
        foreach ($dir in $versionDirs) {
            $found = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Filter $notesFileName -ErrorAction SilentlyContinue |
                Select-Object -First 1)
            if ($found.Count -gt 0 -and $seen.Add($found[0].FullName)) {
                $candidates.Add($found[0])
            }
        }
    }

    $bestText = ""
    $bestTime = [System.DateTime]::MinValue
    $bestPath = ""
    foreach ($candidate in $candidates) {
        $text = [System.IO.File]::ReadAllText($candidate.FullName, [System.Text.Encoding]::UTF8)
        if (Test-ReleaseNotesIsTemplate -RepoRoot $RepoRoot -Text $text) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($bestPath) -or $candidate.LastWriteTime -gt $bestTime) {
            $bestText = $text
            $bestTime = $candidate.LastWriteTime
            $bestPath = $candidate.FullName
        }
    }

    return @{
        Text = $bestText
        Path = $bestPath
    }
}
