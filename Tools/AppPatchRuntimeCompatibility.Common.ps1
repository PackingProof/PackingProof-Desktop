Set-StrictMode -Version Latest

function Get-AppPatchFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-IsAppPatchManagedRuntimePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $normalized = $RelativePath.Replace('/', '\').TrimStart('\')
    return [string]::Equals(
            $normalized,
            'tools\ffmpeg.exe',
            [StringComparison]::OrdinalIgnoreCase) -or
        $normalized.StartsWith('libvlc\', [StringComparison]::OrdinalIgnoreCase)
}

function Test-AppPatchRuntimeCompatibility {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentAppDir,
        [Parameter(Mandatory = $true)][string]$BaselineAppDir,
        [Parameter(Mandatory = $true)]$FFmpegBaseline
    )

    $currentRoot = [IO.Path]::GetFullPath($CurrentAppDir)
    $baselineRoot = [IO.Path]::GetFullPath($BaselineAppDir)
    $currentFFmpeg = Join-Path $currentRoot 'tools\ffmpeg.exe'
    $baselineFFmpeg = Join-Path $baselineRoot 'tools\ffmpeg.exe'

    if (-not (Test-Path -LiteralPath $currentFFmpeg -PathType Leaf)) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录缺少 FFmpeg，无法生成安全的 AppPatch' }
    }
    $currentFFmpegFile = Get-Item -LiteralPath $currentFFmpeg
    $currentFFmpegHash = Get-AppPatchFileSha256 -Path $currentFFmpeg
    if ($currentFFmpegFile.Length -ne [long]$FFmpegBaseline.package.executable_size -or
        -not [string]::Equals(
            $currentFFmpegHash,
            [string]$FFmpegBaseline.package.executable_sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录中的 FFmpeg 与锁定基线不一致' }
    }

    if (-not (Test-Path -LiteralPath $baselineFFmpeg -PathType Leaf)) {
        return [pscustomobject]@{ Compatible = $false; Reason = 'AppPatch 基线缺少 FFmpeg，需使用完整版本更新' }
    }
    $baselineFFmpegFile = Get-Item -LiteralPath $baselineFFmpeg
    $baselineFFmpegHash = Get-AppPatchFileSha256 -Path $baselineFFmpeg
    $acceptedFFmpeg = @($FFmpegBaseline.app_patch_compatible_executables | Where-Object {
        [long]$_.size -eq $baselineFFmpegFile.Length -and
        [string]::Equals(
            [string]$_.sha256,
            $baselineFFmpegHash,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($acceptedFFmpeg.Count -ne 1) {
        return [pscustomobject]@{ Compatible = $false; Reason = 'AppPatch 基线中的 FFmpeg 版本或哈希不在兼容白名单中，需使用完整版本更新' }
    }

    $currentVlcRoot = Join-Path $currentRoot 'libvlc\win-x64'
    $baselineVlcRoot = Join-Path $baselineRoot 'libvlc\win-x64'
    if (-not (Test-Path -LiteralPath $currentVlcRoot -PathType Container)) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录缺少 LibVLC，无法生成安全的 AppPatch' }
    }
    if (-not (Test-Path -LiteralPath $baselineVlcRoot -PathType Container)) {
        return [pscustomobject]@{ Compatible = $false; Reason = 'AppPatch 基线缺少 LibVLC，需使用完整版本更新' }
    }

    $currentVlcFiles = @(Get-ChildItem -LiteralPath $currentVlcRoot -Recurse -File)
    if ($currentVlcFiles.Count -eq 0) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录的 LibVLC 文件列表为空' }
    }
    foreach ($currentFile in $currentVlcFiles) {
        $relativePath = [IO.Path]::GetRelativePath($currentVlcRoot, $currentFile.FullName)
        $baselineFile = Join-Path $baselineVlcRoot $relativePath
        if (-not (Test-Path -LiteralPath $baselineFile -PathType Leaf)) {
            return [pscustomobject]@{ Compatible = $false; Reason = "AppPatch 基线缺少 LibVLC 必需文件：$relativePath" }
        }
        $baselineFileInfo = Get-Item -LiteralPath $baselineFile
        if ($baselineFileInfo.Length -ne $currentFile.Length -or
            -not [string]::Equals(
                (Get-AppPatchFileSha256 -Path $baselineFile),
                (Get-AppPatchFileSha256 -Path $currentFile.FullName),
                [StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Compatible = $false; Reason = "AppPatch 基线的 LibVLC 必需文件不兼容：$relativePath" }
        }
    }

    return [pscustomobject]@{
        Compatible = $true
        Reason = "FFmpeg $([string]$acceptedFFmpeg[0].version) 与 LibVLC 必需文件均可安全复用"
    }
}
