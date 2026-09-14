# 发布版本号与产物名的统一规则。
#
# 打包脚本 Tools\Publish-CleanPackage.ps1 与发布脚本 Tools\Publish-Releases.ps1
# 必须用同一套归一化，否则就会出现「有时带 v、有时不带 v」的错配：
#   - tag 可能写成 v0.0.67、0.0.67，或带后缀的 v0.0.67-rc1
#   - 产物名固定是：目录、补丁包、本地校验清单用 v<纯版本号>；
#     Setup 与更新清单用不带 v 的纯版本号
# 任何一边自己用 tag 拼名字，都会在 tag 形式不同的时候找不到文件。

function Get-NormalizedReleaseVersion {
    param([string]$RawVersion)

    $value = "$RawVersion".Trim()
    if ($value.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    $suffixIndex = $value.IndexOfAny(@('+', '-'))
    if ($suffixIndex -ge 0) {
        $value = $value.Substring(0, $suffixIndex)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return "0.0.0"
    }

    return $value
}

# 由精确 tag 推断本地产物名，返回：
#   NormalizedVersion / ReleaseTag / PackageRoot
#   SetupFileName / UpdateJsonFileName / AppPatchFileName / LauncherPatchFileName
function Get-ReleaseArtifactNames {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $normalizedVersion = Get-NormalizedReleaseVersion $Tag
    if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "tag $Tag 的版本号不是 X.Y.Z 形式，无法推断产物名"
    }

    $releaseTag = "v$normalizedVersion"
    return @{
        NormalizedVersion = $normalizedVersion
        ReleaseTag = $releaseTag
        PackageRoot = Join-Path $RepoRoot "package\PackingProof+$releaseTag"
        SetupFileName = "PackingProof_Setup_v$normalizedVersion.exe"
        UpdateJsonFileName = "update_v$normalizedVersion.json"
        AppPatchFileName = "PackingProof_AppPatch_$releaseTag.zip"
        LauncherPatchFileName = "PackingProof_LauncherPatch_$releaseTag.zip"
    }
}
