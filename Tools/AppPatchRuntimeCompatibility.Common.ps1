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

    # .NET/WPF 运行时文件不在 AppPatch 的受管排除名单内（见 Test-IsAppPatchManagedRuntimePath），
    # 运行时漂移时它们会被正常打进补丁，补丁本身仍然正确，只是体积显著变大。
    # 因此这里只告警、不阻断；真正会装出坏程序的 FFmpeg/LibVLC 才在下面拒绝生成。
    $warnings = @()
    foreach ($runtimeMarker in @('System.Private.CoreLib.dll', 'coreclr.dll', 'PresentationFramework.dll')) {
        $currentMarker = Join-Path $currentRoot $runtimeMarker
        $baselineMarker = Join-Path $baselineRoot $runtimeMarker
        if (-not (Test-Path -LiteralPath $currentMarker -PathType Leaf) -or
            -not (Test-Path -LiteralPath $baselineMarker -PathType Leaf)) {
            $warnings += "AppPatch 基线缺少 .NET/WPF 运行时标记文件：$runtimeMarker，无法确认运行时是否一致"
            continue
        }
        if ((Get-AppPatchFileSha256 -Path $currentMarker) -ne (Get-AppPatchFileSha256 -Path $baselineMarker)) {
            $warnings += "检测到 .NET/WPF 运行时发生变化（$runtimeMarker），增量包将包含整套运行时，体积显著增大"
        }
    }
    $currentFFmpeg = Join-Path $currentRoot 'tools\ffmpeg.exe'
    $baselineFFmpeg = Join-Path $baselineRoot 'tools\ffmpeg.exe'

    if (-not (Test-Path -LiteralPath $currentFFmpeg -PathType Leaf)) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录缺少 FFmpeg，无法生成安全的 AppPatch'; Warnings = $warnings }
    }
    $currentFFmpegFile = Get-Item -LiteralPath $currentFFmpeg
    $currentFFmpegHash = Get-AppPatchFileSha256 -Path $currentFFmpeg
    if ($currentFFmpegFile.Length -ne [long]$FFmpegBaseline.package.executable_size -or
        -not [string]::Equals(
            $currentFFmpegHash,
            [string]$FFmpegBaseline.package.executable_sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录中的 FFmpeg 与锁定基线不一致'; Warnings = $warnings }
    }

    if (-not (Test-Path -LiteralPath $baselineFFmpeg -PathType Leaf)) {
        return [pscustomobject]@{ Compatible = $false; Reason = 'AppPatch 基线缺少 FFmpeg，需使用完整版本更新'; Warnings = $warnings }
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
        return [pscustomobject]@{ Compatible = $false; Reason = 'AppPatch 基线中的 FFmpeg 版本或哈希不在兼容白名单中，需使用完整版本更新'; Warnings = $warnings }
    }

    $currentVlcRoot = Join-Path $currentRoot 'libvlc\win-x64'
    $baselineVlcRoot = Join-Path $baselineRoot 'libvlc\win-x64'
    if (-not (Test-Path -LiteralPath $currentVlcRoot -PathType Container)) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录缺少 LibVLC，无法生成安全的 AppPatch'; Warnings = $warnings }
    }
    if (-not (Test-Path -LiteralPath $baselineVlcRoot -PathType Container)) {
        return [pscustomobject]@{ Compatible = $false; Reason = 'AppPatch 基线缺少 LibVLC，需使用完整版本更新'; Warnings = $warnings }
    }

    $currentVlcFiles = @(Get-ChildItem -LiteralPath $currentVlcRoot -Recurse -File)
    if ($currentVlcFiles.Count -eq 0) {
        return [pscustomobject]@{ Compatible = $false; Reason = '当前发布目录的 LibVLC 文件列表为空'; Warnings = $warnings }
    }
    foreach ($currentFile in $currentVlcFiles) {
        $relativePath = [IO.Path]::GetRelativePath($currentVlcRoot, $currentFile.FullName)
        $baselineFile = Join-Path $baselineVlcRoot $relativePath
        if (-not (Test-Path -LiteralPath $baselineFile -PathType Leaf)) {
            return [pscustomobject]@{ Compatible = $false; Reason = "AppPatch 基线缺少 LibVLC 必需文件：$relativePath"; Warnings = $warnings }
        }
        $baselineFileInfo = Get-Item -LiteralPath $baselineFile
        if ($baselineFileInfo.Length -ne $currentFile.Length -or
            -not [string]::Equals(
                (Get-AppPatchFileSha256 -Path $baselineFile),
                (Get-AppPatchFileSha256 -Path $currentFile.FullName),
                [StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Compatible = $false; Reason = "AppPatch 基线的 LibVLC 必需文件不兼容：$relativePath"; Warnings = $warnings }
        }
    }

    return [pscustomobject]@{
        Compatible = $true
        Reason = "FFmpeg $([string]$acceptedFFmpeg[0].version) 与 LibVLC 必需文件均可安全复用"
        Warnings = $warnings
    }
}
