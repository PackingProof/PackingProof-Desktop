Set-StrictMode -Version Latest

# 共享运行时布局（安装目录下的 runtime\）：
#   runtime\dotnet.exe
#   runtime\host\fxr\<版本>\hostfxr.dll
#   runtime\shared\Microsoft.NETCore.App\<版本>\...
#   runtime\shared\Microsoft.WindowsDesktop.App\<版本>\...
#   runtime\layout.json（版本、文件清单与老布局迁移映射）
#
# 目的：桌面端与 QQ 机器人共用一份 .NET 运行时。自包含应用里的运行时是私有的，
# 别的程序借不到；只有这种“共享框架”布局才能被多个程序通过 DOTNET_ROOT 复用。

$script:SharedRuntimeLayoutSchemaVersion = 1
$script:SharedRuntimeLayoutFileName = 'layout.json'
# 主程序托盘已改用 Shell_NotifyIcon；随包扩展要么自带运行时（QQ 机器人单文件），
# 要么是 Node 脚本，没有任何组件依赖宿主共享运行时里的 WinForms。
$script:SharedRuntimeExcludedFileNames = @(
    'System.Windows.Forms.dll',
    'System.Windows.Forms.Primitives.dll',
    'System.Windows.Forms.Design.dll',
    'System.Windows.Forms.resources.dll',
    'System.Windows.Forms.Design.Editors.dll',
    'System.Windows.Forms.Design.resources.dll',
    'System.Windows.Forms.Primitives.resources.dll',
    'WindowsFormsIntegration.dll',
    'WindowsFormsIntegration.resources.dll',
    'Microsoft.VisualBasic.Forms.dll',
    'Microsoft.VisualBasic.Forms.resources.dll',
    'System.Design.dll',
    'System.Drawing.Design.dll'
)
# 主程序用硬链接落成布局时写下的标记（ExpressPackingMonitoring.UpdateCore\RuntimeLayoutSeeder.cs 里的同名常量）。
# 它不是布局载荷、不在清单里，但仍然算合法内容：安装目录里那份布局正是这样被落成的。
$script:SharedRuntimeLayoutSeedMarkerFileName = '.seeded-by-app'
# 运行时自己写的辅助文件/目录（对应 SharedRuntimeLayout.cs 的 StateFileName 与 LegacyBackupDirectoryName）。
$script:SharedRuntimeLayoutStateFileName = 'layout-state.json'
$script:SharedRuntimeLayoutLegacyDirName = '.legacy'

function Get-SharedRuntimeRequirements {
    param([Parameter(Mandatory = $true)][string]$AppDir)

    $appFullPath = [System.IO.Path]::GetFullPath($AppDir)
    if (-not (Test-Path -LiteralPath $appFullPath -PathType Container)) {
        throw "应用发布目录不存在：$appFullPath"
    }

    # 框架依赖发布写 frameworks，自包含发布写 includedFrameworks；两者都要能读，
    # 迁移期需要拿自包含产物的要求去准备共享运行时。
    $configFiles = @(Get-ChildItem -LiteralPath $appFullPath -File -Filter '*.runtimeconfig.json')
    if ($configFiles.Count -ne 1) {
        throw "发布目录里的 *.runtimeconfig.json 应恰好 1 个，实际 $($configFiles.Count) 个：$appFullPath"
    }

    $document = Get-Content -LiteralPath $configFiles[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $document.PSObject.Properties['runtimeOptions']) {
        throw "runtimeconfig.json 缺少 runtimeOptions：$($configFiles[0].FullName)"
    }
    # StrictMode 下访问不存在的属性会抛错，这里显式探测字段。
    $runtimeOptions = $document.runtimeOptions
    $frameworkItems = $null
    if ($null -ne $runtimeOptions.PSObject.Properties['frameworks']) { $frameworkItems = $runtimeOptions.frameworks }
    elseif ($null -ne $runtimeOptions.PSObject.Properties['includedFrameworks']) { $frameworkItems = $runtimeOptions.includedFrameworks }
    if ($null -eq $frameworkItems) {
        throw "runtimeconfig.json 缺少 frameworks/includedFrameworks：$($configFiles[0].FullName)"
    }

    $frameworks = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $frameworkItems) {
        $frameworks.Add([pscustomobject]@{ Name = [string]$item.name; Version = [string]$item.version })
    }
    if ($frameworks.Count -eq 0) {
        throw "runtimeconfig.json 里没有任何框架要求：$($configFiles[0].FullName)"
    }

    return [pscustomobject]@{
        RuntimeConfigPath = $configFiles[0].FullName
        Tfm               = [string]$runtimeOptions.tfm
        Frameworks        = $frameworks
    }
}

function Get-HighestSharedVersionDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$MajorMinor
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return '' }
    $pattern = '^' + [regex]::Escape($MajorMinor) + '\.\d+$'
    $candidates = @(Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object { $_.Name -match $pattern } |
        Sort-Object -Property @{ Expression = { [version]$_.Name } } -Descending)
    if ($candidates.Count -eq 0) { return '' }
    return $candidates[0].FullName
}

function Resolve-SharedRuntimeSource {
    param(
        [Parameter(Mandatory = $true)]$Requirements,
        [string]$DotNetRoot = ""
    )

    if ([string]::IsNullOrWhiteSpace($DotNetRoot)) {
        $DotNetRoot = Join-Path $env:ProgramFiles 'dotnet'
    }
    $DotNetRoot = [System.IO.Path]::GetFullPath($DotNetRoot)
    if (-not (Test-Path -LiteralPath $DotNetRoot -PathType Container)) {
        throw "找不到 .NET 安装目录：$DotNetRoot"
    }

    $muxerPath = Join-Path $DotNetRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $muxerPath -PathType Leaf)) {
        throw "找不到 dotnet.exe：$muxerPath"
    }

    $resolved = [System.Collections.Generic.List[object]]::new()
    $majorMinor = ''
    foreach ($framework in $Requirements.Frameworks) {
        $required = [version]$framework.Version
        $prefix = "$($required.Major).$($required.Minor)"
        if ($majorMinor.Length -eq 0) { $majorMinor = $prefix }
        elseif ($majorMinor -ne $prefix) {
            throw "暂不支持一份共享运行时满足多个 major.minor：$majorMinor 与 $prefix"
        }

        $root = Join-Path (Join-Path $DotNetRoot 'shared') $framework.Name
        # 优先使用应用实际声明的精确补丁版本。机器上同时安装多个补丁版本时，
        # 不能擅自选择更高版本，否则自包含产物和 runtime-seed 会来自不同版本，
        # AppPatch 会意外携带整套运行时。
        $exactDirectory = Join-Path $root $required.ToString()
        $directory = if (Test-Path -LiteralPath $exactDirectory -PathType Container) {
            $exactDirectory
        }
        else {
            Get-HighestSharedVersionDirectory -Root $root -MajorMinor $prefix
        }
        if ($directory.Length -eq 0) {
            throw "找不到共享框架 $($framework.Name) $prefix.x：$root（请安装对应 .NET 运行时）"
        }
        $selectedVersion = [version](Split-Path -Leaf $directory)
        if ($selectedVersion -lt $required) {
            throw "共享框架 $($framework.Name) 版本过低：需要 >= $($framework.Version)，只有 $selectedVersion"
        }
        $resolved.Add([pscustomobject]@{
                Name            = $framework.Name
                Version         = $selectedVersion.ToString()
                RequiredVersion = $framework.Version
                SourceDirectory = $directory
            })
    }

    # hostfxr 与框架按同一 major.minor 搭配；优先与应用声明的补丁版本保持一致。
    $hostFxrRoot = Join-Path $DotNetRoot 'host\fxr'
    $preferredHostFxrVersion = [string]$Requirements.Frameworks[0].Version
    $exactHostFxrDirectory = Join-Path $hostFxrRoot $preferredHostFxrVersion
    $hostFxrDirectory = if (Test-Path -LiteralPath $exactHostFxrDirectory -PathType Container) {
        $exactHostFxrDirectory
    }
    else {
        Get-HighestSharedVersionDirectory -Root $hostFxrRoot -MajorMinor $majorMinor
    }
    if ($hostFxrDirectory.Length -eq 0) {
        throw "找不到 host\fxr\$majorMinor.x：$hostFxrRoot"
    }

    return [pscustomobject]@{
        DotNetRoot       = $DotNetRoot
        MuxerPath        = $muxerPath
        HostFxrVersion   = (Split-Path -Leaf $hostFxrDirectory)
        HostFxrDirectory = $hostFxrDirectory
        Frameworks       = $resolved
    }
}

function Get-RelativePosixPath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$FullPath
    )

    $base = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + '\'
    $full = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $full.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径不在布局目录内：$full"
    }
    return $full.Substring($base.Length).Replace('\', '/')
}

function New-SharedRuntimeLayout {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [string]$DotNetRoot = ""
    )

    $requirements = Get-SharedRuntimeRequirements -AppDir $AppDir
    $source = Resolve-SharedRuntimeSource -Requirements $requirements -DotNetRoot $DotNetRoot

    $destination = [System.IO.Path]::GetFullPath($DestinationPath)
    if (Test-Path -LiteralPath $destination) {
        throw "共享运行时目标目录已存在，请先清理，避免残留旧文件：$destination"
    }
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null

    Copy-Item -LiteralPath $source.MuxerPath -Destination (Join-Path $destination 'dotnet.exe') -Force
    $hostFxrDestination = Join-Path $destination ("host\fxr\" + $source.HostFxrVersion)
    [System.IO.Directory]::CreateDirectory($hostFxrDestination) | Out-Null
    Copy-Item -Path (Join-Path $source.HostFxrDirectory '*') -Destination $hostFxrDestination -Recurse -Force

    $frameworkEntries = [System.Collections.Generic.List[object]]::new()
    $appFileMap = @{}
    foreach ($framework in $source.Frameworks) {
        $target = Join-Path $destination ("shared\" + $framework.Name + "\" + $framework.Version)
        [System.IO.Directory]::CreateDirectory($target) | Out-Null
        foreach ($sourceFile in Get-ChildItem -LiteralPath $framework.SourceDirectory -Recurse -File) {
            if ($script:SharedRuntimeExcludedFileNames -contains $sourceFile.Name) { continue }
            $relativePath = Get-RelativePosixPath -BasePath $framework.SourceDirectory -FullPath $sourceFile.FullName
            $destinationFile = Join-Path $target ($relativePath -replace '/', '\')
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destinationFile)) | Out-Null
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
        }
        $frameworkEntries.Add([pscustomobject]@{
                name            = $framework.Name
                version         = $framework.Version
                requiredVersion = $framework.RequiredVersion
            })

        # 老的自包含布局是平铺的，这里记录“平铺文件名 -> 新布局候选路径”，供迁移使用。
        # 个别程序集（例如 Microsoft.VisualBasic.dll）在两个共享框架里都有，所以是列表。
        foreach ($file in Get-ChildItem -LiteralPath $target -File) {
            if (-not $appFileMap.ContainsKey($file.Name)) {
                $appFileMap[$file.Name] = [System.Collections.Generic.List[string]]::new()
            }
            $appFileMap[$file.Name].Add((Get-RelativePosixPath -BasePath $destination -FullPath $file.FullName))
        }
    }
    $hostFxrFile = Get-ChildItem -LiteralPath $hostFxrDestination -File | Where-Object { $_.Name -ieq 'hostfxr.dll' } | Select-Object -First 1
    if ($null -eq $hostFxrFile) {
        throw "host\fxr 目录里没有 hostfxr.dll：$hostFxrDestination"
    }
    if (-not $appFileMap.ContainsKey($hostFxrFile.Name)) {
        $appFileMap[$hostFxrFile.Name] = [System.Collections.Generic.List[string]]::new()
    }
    $appFileMap[$hostFxrFile.Name].Add((Get-RelativePosixPath -BasePath $destination -FullPath $hostFxrFile.FullName))

    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
        $relativePath = Get-RelativePosixPath -BasePath $destination -FullPath $file.FullName
        if ($relativePath -ieq $script:SharedRuntimeLayoutFileName) { continue }
        $files.Add([pscustomobject]@{
                path   = $relativePath
                size   = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            })
    }

    $manifest = [ordered]@{
        schemaVersion           = $script:SharedRuntimeLayoutSchemaVersion
        hostFxrVersion          = $source.HostFxrVersion
        frameworks              = $frameworkEntries
        sourceRequirements      = $requirements.Frameworks
        sourceRuntimeConfigPath = $requirements.RuntimeConfigPath
        files                   = $files
        appFileMap              = $appFileMap
    }
    $manifestPath = Join-Path $destination $script:SharedRuntimeLayoutFileName
    [System.IO.File]::WriteAllText(
        $manifestPath,
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [System.Text.UTF8Encoding]::new($false))

    return [pscustomobject]@{
        LayoutPath       = $destination
        ManifestPath     = $manifestPath
        HostFxrVersion   = $source.HostFxrVersion
        Frameworks       = $frameworkEntries
        FileCount        = $files.Count
        TotalBytes       = ($files | Measure-Object -Property size -Sum).Sum
        LegacyMappedFiles = $appFileMap.Count
    }
}

function New-RuntimeLayoutSeed {
    <#
      .SYNOPSIS
      从生成的共享运行时布局推导"运行时种子"，放进自包含产物的 app\runtime-seed\。

      .DESCRIPTION
      自包含发布把运行时平铺在 app\ 里，主程序首次启动时会按布局清单给这些文件在安装根目录
      runtime\ 下建硬链接（RuntimeLayoutSeeder），扩展就能复用同一份 .NET。种子里只需要放
      **自包含产物里没有**的那些文件（框架的 deps/runtimeconfig、宿主附属文件等）：布局文件在
      app\ 里能找到同名且哈希一致的文件时就不进种子，首次启动直接建硬链接，不重复占空间。

      这里同时是构建期守卫：任何布局文件既没有同名同哈希来源、布局里又找不到时直接抛错，
      避免发出一个"种子不全、布局落不成"的包。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$LayoutPath,
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    $layout = [System.IO.Path]::GetFullPath($LayoutPath)
    $app = [System.IO.Path]::GetFullPath($AppDir)
    if (-not (Test-Path -LiteralPath $layout -PathType Container)) {
        throw "共享运行时布局不存在：$layout"
    }
    if (-not (Test-Path -LiteralPath $app -PathType Container)) {
        throw "应用发布目录不存在：$app"
    }

    $manifestPath = Join-Path $layout $script:SharedRuntimeLayoutFileName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "布局清单不存在：$manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $manifest.PSObject.Properties['files'] -or @($manifest.files).Count -eq 0) {
        throw "布局清单里没有文件条目：$manifestPath"
    }

    $seedDirectory = Join-Path $app 'runtime-seed'
    if (Test-Path -LiteralPath $seedDirectory) {
        [System.IO.Directory]::Delete($seedDirectory, $true)
    }
    [System.IO.Directory]::CreateDirectory($seedDirectory) | Out-Null

    $fromAppDir = 0
    $fromSeed = 0
    foreach ($entry in @($manifest.files)) {
        $relativePath = [string]$entry.path
        $expectedSize = [long]$entry.size
        $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
        $name = [System.IO.Path]::GetFileName($relativePath)
        $appLocal = Join-Path $app $name
        if ((Test-Path -LiteralPath $appLocal -PathType Leaf) -and
            ((Get-Item -LiteralPath $appLocal).Length -eq $expectedSize) -and
            ((Get-FileHash -LiteralPath $appLocal -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedHash)) {
            $fromAppDir++
            continue
        }

        $source = Join-Path $layout ($relativePath -replace '/', '\')
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "布局文件既没有同名同哈希的自包含来源、布局里也找不到：$relativePath"
        }
        $destination = Join-Path $seedDirectory ($relativePath -replace '/', '\')
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $fromSeed++
    }

    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $seedDirectory $script:SharedRuntimeLayoutFileName) -Force

    # 反向守卫：种子里写的文件必须和布局清单里的哈希一致（复制出错当场拦住）。
    foreach ($entry in @($manifest.files)) {
        $seedFile = Join-Path $seedDirectory ([string]$entry.path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $seedFile -PathType Leaf)) { continue }
        if ((Get-FileHash -LiteralPath $seedFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "运行时种子里的文件与布局清单不一致：$($entry.path)"
        }
    }

    return [pscustomobject]@{
        SeedDirectory = $seedDirectory
        LayoutFiles   = @($manifest.files).Count
        FromAppDir    = $fromAppDir
        FromSeed      = $fromSeed
    }
}

function Test-SharedRuntimeLayout {
    param([Parameter(Mandatory = $true)][string]$LayoutPath)

    $layout = [System.IO.Path]::GetFullPath($LayoutPath)
    $failures = [System.Collections.Generic.List[string]]::new()
    $manifestPath = Join-Path $layout $script:SharedRuntimeLayoutFileName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "布局清单不存在：$manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

    if ([int]$manifest.schemaVersion -ne $script:SharedRuntimeLayoutSchemaVersion) {
        $failures.Add("schemaVersion 不是 $($script:SharedRuntimeLayoutSchemaVersion)：$($manifest.schemaVersion)")
    }
    $hostFxrRelativePath = 'host\fxr\' + [string]$manifest.hostFxrVersion + '\hostfxr.dll'
    foreach ($required in @('dotnet.exe', $hostFxrRelativePath)) {
        if (-not (Test-Path -LiteralPath (Join-Path $layout $required) -PathType Leaf)) {
            $failures.Add("缺少关键文件：$required")
        }
    }
    foreach ($framework in $manifest.frameworks) {
        $frameworkRoot = Join-Path $layout ("shared\" + [string]$framework.name + "\" + [string]$framework.version)
        if (-not (Test-Path -LiteralPath $frameworkRoot -PathType Container)) {
            $failures.Add("缺少共享框架目录：$($framework.name) $($framework.version)")
            continue
        }
        $frameworkName = [string]$framework.name
        $depsMarker = $frameworkName + '.deps.json'
        $runtimeConfigMarker = $frameworkName + '.runtimeconfig.json'
        foreach ($marker in @($depsMarker, $runtimeConfigMarker)) {
            if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot $marker) -PathType Leaf)) {
                $failures.Add("共享框架缺少标记文件：$($framework.name)/$marker")
            }
        }
        $selected = [version]([string]$framework.version)
        $required = [version]([string]$framework.requiredVersion)
        if ($selected.Major -ne $required.Major -or $selected.Minor -ne $required.Minor -or $selected -lt $required) {
            $failures.Add("共享框架版本不满足要求：$($framework.name) 需要 $required，实际 $selected")
        }
    }

    $listed = @{}
    foreach ($entry in $manifest.files) {
        $relativePath = [string]$entry.path
        $fullPath = Join-Path $layout ($relativePath -replace '/', '\')
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            $failures.Add("清单里的文件不存在：$relativePath")
            continue
        }
        $listed[$relativePath] = $true
        $file = Get-Item -LiteralPath $fullPath
        if ($file.Length -ne [long]$entry.size) {
            $failures.Add("文件大小与清单不一致：$relativePath（清单 $($entry.size)，实际 $($file.Length)）")
            continue
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne [string]$entry.sha256) {
            $failures.Add("文件哈希与清单不一致：$relativePath")
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $layout -Recurse -File) {
        $relativePath = Get-RelativePosixPath -BasePath $layout -FullPath $file.FullName
        if ($relativePath -ieq $script:SharedRuntimeLayoutFileName) { continue }
        if ($relativePath -ieq $script:SharedRuntimeLayoutSeedMarkerFileName) { continue }
        # 运行时自己写的辅助文件不算"清单之外"：启动器每次处理布局都会写 layout-state.json
        # （迁移计数与启动次数），搬走的老装机平铺副本放在 .legacy\ 里留作回滚。
        # 真机上装好跑过的布局本来就有它们，拿这份自检去核安装目录时必须放行。
        if ($relativePath -ieq $script:SharedRuntimeLayoutStateFileName) { continue }
        if ($relativePath -ieq $script:SharedRuntimeLayoutLegacyDirName -or
            $relativePath.StartsWith(($script:SharedRuntimeLayoutLegacyDirName + '/'), [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $listed.ContainsKey($relativePath)) {
            $failures.Add("布局里有清单之外的文件：$relativePath")
        }
    }

    $mappedNames = 0
    foreach ($name in $manifest.appFileMap.PSObject.Properties.Name) {
        $targets = @($manifest.appFileMap.$name)
        if ($targets.Count -eq 0) {
            $failures.Add("迁移映射没有目标：$name")
            continue
        }
        $mappedNames++
        foreach ($target in $targets) {
            if (-not $listed.ContainsKey([string]$target)) {
                $failures.Add("迁移映射的目标不在清单里：$name -> $target")
            }
        }
    }

    if ($failures.Count -gt 0) {
        foreach ($failure in $failures) { Write-Host "[FAIL] $failure" }
        throw "共享运行时布局校验未通过，共 $($failures.Count) 项"
    }

    return [pscustomobject]@{
        LayoutPath     = $layout
        HostFxrVersion = [string]$manifest.hostFxrVersion
        Frameworks     = $manifest.frameworks
        FileCount      = $manifest.files.Count
        LegacyMappedFiles = $mappedNames
    }
}
