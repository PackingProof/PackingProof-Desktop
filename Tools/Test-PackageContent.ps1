# 复用工具：校验发布产物里到底装的是不是本次构建的内容。
#
# 历史教训：只看时间戳、只看"脚本没报错"都不够 —— 曾经出现过包看着是新的、内容却是旧的。
# 用法：
#   pwsh -NoProfile -File Tools\Test-PackageContent.ps1 -PackagePath <zip|dll|exe|目录> `
#        -Require "release list scanned" -Forbid "per_page=30"
#   AppPatch / DMG / 安装目录都能直接喂：zip 会自动解开，目录会递归扫 dll/exe。
# 退出码 0 = 通过；1 = 有 Required 缺失或 Forbidden 命中。
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string[]]$Require = @(),
    [string[]]$Forbid = @()
)

$ErrorActionPreference = "Stop"

# .NET 程序集的字符串字面量在元数据里是 UTF-16LE，所以按 UTF-16LE 字节做子串匹配
function Test-Utf16Marker {
    param([string]$FilePath, [string]$Marker)

    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    $needle = [System.Text.Encoding]::Unicode.GetBytes($Marker)
    if ($needle.Length -eq 0) { return $false }

    for ($i = 0; $i -le $bytes.Length - $needle.Length; $i++) {
        $matched = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($bytes[$i + $j] -ne $needle[$j]) { $matched = $false; break }
        }
        if ($matched) { return $true }
    }
    return $false
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "找不到产物：$PackagePath"
}

$workRoot = $PackagePath
$tempDir = $null
if (Test-Path -LiteralPath $PackagePath -PathType Leaf) {
    $extension = [System.IO.Path]::GetExtension($PackagePath).ToLowerInvariant()
    if ($extension -eq ".zip") {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("epm-content-" + [System.Guid]::NewGuid().ToString("N"))
        Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempDir -Force
        $workRoot = $tempDir
    }
}

try {
    $targets = if (Test-Path -LiteralPath $workRoot -PathType Container) {
        @(Get-ChildItem -LiteralPath $workRoot -Recurse -File -Include *.dll, *.exe)
    }
    else {
        @(Get-Item -LiteralPath $workRoot)
    }
    Write-Host "扫描文件数：$($targets.Count)（$PackagePath）"

    $failed = New-Object System.Collections.Generic.List[string]

    foreach ($marker in $Require) {
        $found = $false
        foreach ($file in $targets) {
            if (Test-Utf16Marker -FilePath $file.FullName -Marker $marker) { $found = $true; break }
        }
        if ($found) { Write-Host "[OK]   必须存在：$marker" }
        else { Write-Host "[FAIL] 缺少：$marker"; $failed.Add($marker) }
    }

    foreach ($marker in $Forbid) {
        $hit = $false
        foreach ($file in $targets) {
            if (Test-Utf16Marker -FilePath $file.FullName -Marker $marker) { $hit = $true; break }
        }
        if ($hit) { Write-Host "[FAIL] 不该存在：$marker"; $failed.Add($marker) }
        else { Write-Host "[OK]   已排除：$marker" }
    }

    if ($failed.Count -gt 0) {
        Write-Host "内容校验未通过：$($failed -join '、')"
        exit 1
    }

    Write-Host "内容校验通过"
    exit 0
}
finally {
    if ($tempDir -and (Test-Path -LiteralPath $tempDir)) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
