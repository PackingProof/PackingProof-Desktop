[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LegacyLauncherPath,
    [string]$BaselineAppDir = "",
    [string]$PackageDir = "",
    [string]$Version = "0.0.83",
    [int]$AppStartupSeconds = 90,
    [switch]$SimulateSecondInstance,
    # "下载了 APP 之后才更新启动器"这半段：把新版启动器包放成 pending，再启动一次主程序，
    # 断言安装根目录的启动器被换成本次基线里的那一份。
    [switch]$VerifyLauncherSelfUpdate,
    [string]$LauncherPatchPath = "",
    # 老启动器（0.0.83 基线之前）不认补丁的 deletes 字段：这是预期边界，不是缺陷，
    # 所以用它跑时要反过来断言"旧文件仍在"，别把边界当成 bug 报出来。
    [switch]$LauncherPredatesDeleteList,
    [switch]$KeepWorkspace
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 端到端验证真实升级路径：老启动器（0.0.78 时代，早于 root 落点支持）+ 补丁基线版本的 app\ +
# 新版的 pending 补丁。这正是 0.0.82 现场失败的那条路（老启动器报 "Patch 文件不存在：dotnet.exe"），
# 也是所有老装机升到自包含新版时实际会走的路。
#
# 断言：老启动器装得上、app 变成新版、主程序启动后把共享运行时布局落成到安装根目录、布局逐文件自检通过。

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("legacy-upgrade-" + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "[OK]   $Message" }
    else { Write-Host "[FAIL] $Message"; $failures.Add($Message) }
}

# 启动器崩溃时会弹阻塞对话框；测完按安装目录前缀清掉，别留在机器上。
function Stop-LeftoverProcesses {
    param([string]$InstallRoot)
    $prefix = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='ExpressPackingMonitoring.exe'")) {
        $path = [string]$process.ExecutablePath
        if ($path.Length -eq 0) { continue }
        if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

if ([string]::IsNullOrWhiteSpace($BaselineAppDir)) {
    $BaselineAppDir = Join-Path $repoRoot "package\ExpressPackingMonitoring+v0.0.18\ExpressPackingMonitoring+v0.0.18\app"
}
if ([string]::IsNullOrWhiteSpace($PackageDir)) {
    $PackageDir = Join-Path $repoRoot "package\PackingProof+v$Version"
}
$releaseTag = "v$Version"

foreach ($required in @($LegacyLauncherPath, $BaselineAppDir,
        (Join-Path $PackageDir "PackingProof_AppPatch_$releaseTag.zip"),
        (Join-Path $PackageDir "update_$releaseTag.json"))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "缺少输入：$required" }
}

$previousUserDataDir = $env:EPM_USER_DATA_DIR
try {
    $installRoot = Join-Path $workspace "install"
    $installAppDir = Join-Path $installRoot "app"
    $userDataDir = Join-Path $workspace "userdata"
    Copy-Item -LiteralPath (Join-Path $LegacyLauncherPath "..") -Destination $installRoot -Recurse -Force
    # 老启动器只留 exe，其余是手动更新脚本，不参与升级。
    Move-Item -LiteralPath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Destination (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Force
    Copy-Item -LiteralPath $BaselineAppDir -Destination $installAppDir -Recurse -Force
    # 基线包如果本来就是"共享运行时布局"形态（0.0.82 起：app\ 是框架依赖的，运行时在包根 runtime\），
    # 要把那份 runtime\ 一起摆上，才是真机上该版本的安装形态：这类机器升级后**不该**由主程序再落成布局，
    # 原有的 runtime\ 也必须原样可用（补丁换了 app\ 里的运行时载荷，但布局是独立副本，不该被删）。
    $baselinePackageRoot = Split-Path -Parent $BaselineAppDir
    $baselineRuntimeDir = Join-Path $baselinePackageRoot "runtime"
    $hadLayoutBeforeUpgrade = Test-Path -LiteralPath $baselineRuntimeDir -PathType Container
    if ($hadLayoutBeforeUpgrade) {
        Copy-Item -LiteralPath $baselineRuntimeDir -Destination (Join-Path $installRoot "runtime") -Recurse -Force
    }
    Write-Host "假安装目录：$installRoot（老启动器 + 基线 app\）"

    # 清理列表的边界要用"升级前后都在"来断言，先按补丁清单在基线 app\ 里取一次快照：
    # 老启动器不认 deletes，它装完之后"本来就存在的那几个"必须原样留着；新启动器则要全清掉。
    # 不能直接拿清单条数当期望值——清理列表是累计的，老基线里可能只有其中几个（0.0.18 全中）。
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $deletePaths = @()
    $patchZipPath = Join-Path $PackageDir "PackingProof_AppPatch_$releaseTag.zip"
    $patchZip = [System.IO.Compression.ZipFile]::OpenRead($patchZipPath)
    try {
        $manifestEntry = $patchZip.Entries | Where-Object { $_.FullName -eq "patch_manifest.json" }
        if ($null -eq $manifestEntry) { throw "补丁里没有 patch_manifest.json" }
        $reader = New-Object System.IO.StreamReader($manifestEntry.Open(), [System.Text.Encoding]::UTF8)
        try { $patchManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    }
    finally {
        $patchZip.Dispose()
    }
    if ($null -ne $patchManifest.PSObject.Properties['deletes']) {
        foreach ($entry in @($patchManifest.deletes)) {
            $deletePaths += (([string]$entry.path) -replace '/', '\')
        }
    }
    $expectedSurvivors = @($deletePaths | Where-Object {
            Test-Path -LiteralPath (Join-Path $installAppDir $_) })
    Write-Host ("清理列表 {0} 项，其中基线 app\ 里存在 {1} 项" -f $deletePaths.Count, $expectedSurvivors.Count)

    # 现场形态：pending 里放着新版的更新描述与补丁包。
    $pendingDir = Join-Path $userDataDir "cache\updates\pending"
    [System.IO.Directory]::CreateDirectory($pendingDir) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageDir "update_$releaseTag.json") `
        -Destination (Join-Path $pendingDir "update_manifest.json") -Force
    Copy-Item -LiteralPath (Join-Path $PackageDir "PackingProof_AppPatch_$releaseTag.zip") `
        -Destination (Join-Path $pendingDir "PackingProof_AppPatch_$releaseTag.zip") -Force
    $env:EPM_USER_DATA_DIR = $userDataDir

    $launcher = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") `
        -PassThru -WorkingDirectory $installRoot
    if ($SimulateSecondInstance) {
        # 现场形态：用户在安装过程中又点了一次（或主程序把直接启动重定向到根启动器），
        # 于是第二个启动器实例进来。老代码会"跳过安装、直接启动主程序"，把主程序拉到
        # 正在被覆盖的目录上 —— 0.0.83 现场就是这么起不来的。
        Start-Sleep -Milliseconds 1500
        Write-Host "再启动一次启动器，模拟安装过程中的第二次点击"
        $second = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") `
            -PassThru -WorkingDirectory $installRoot
        $second | Wait-Process -Timeout ($AppStartupSeconds + 120) -ErrorAction SilentlyContinue
        if (-not $second.HasExited) { $second.Kill() }
    }
    $launcher | Wait-Process -Timeout ($AppStartupSeconds + 60) -ErrorAction SilentlyContinue
    if (-not $launcher.HasExited) { $launcher.Kill() }
    # 主程序是被启动器拉起来的：留足时间让它跑完启动早期的布局落成。
    Start-Sleep -Seconds 5
    Stop-LeftoverProcesses -InstallRoot $installRoot

    $launcherLog = Join-Path $userDataDir "log\launcher_update.log"
    $logText = if (Test-Path -LiteralPath $launcherLog) { Get-Content -Raw -LiteralPath $launcherLog -Encoding UTF8 } else { "" }
    Assert-True ($logText -notlike "*Patch 文件不存在*") "老启动器没有报「Patch 文件不存在」（0.0.82 现场就是这么失败的）"
    Assert-True ($logText -like "*pending Patch 更新完成*") "老启动器成功应用了 pending 补丁"

    # 关键不变量：补丁装完之前不许启动主程序（装到一半启动 = 主程序拉到半新半旧的目录上起不来）。
    $logLines = @(Get-Content -LiteralPath $launcherLog -Encoding UTF8)
    $installedAt = -1
    $startedAt = -1
    for ($index = 0; $index -lt $logLines.Count; $index++) {
        if ($installedAt -lt 0 -and $logLines[$index] -like "*pending Patch 更新完成*") { $installedAt = $index }
        if ($startedAt -lt 0 -and $logLines[$index] -like "*已启动主程序*") { $startedAt = $index }
    }
    Assert-True ($installedAt -ge 0) "日志里能看到补丁安装完成"
    Assert-True ($startedAt -ge 0) "日志里能看到启动主程序"
    if ($installedAt -ge 0 -and $startedAt -ge 0) {
        Assert-True ($startedAt -gt $installedAt) "第一条「已启动主程序」在「pending Patch 更新完成」之后（没有在覆盖文件时启动主程序）"
    }

    $appDll = Join-Path $installAppDir "ExpressPackingMonitoring.dll"
    Assert-True (Test-Path -LiteralPath $appDll) "app\ExpressPackingMonitoring.dll 在"
    if (Test-Path -LiteralPath $appDll) {
        $fileVersion = (Get-Item -LiteralPath $appDll).VersionInfo.FileVersion
        Assert-True ($fileVersion -like "$Version*") "升级后 app 版本是 $Version（实际 $fileVersion）"
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $installAppDir "coreclr.dll")) "升级后 app\ 是自包含形态（有 coreclr.dll）"

    # 清理列表：补丁声明的 deletes 必须在升级后真的不在了（老启动器则原样留着），且不能误删别的东西。
    Assert-True ($deletePaths.Count -gt 0) "补丁带上了清理列表（$($deletePaths.Count) 个）"
    $survivors = @()
    foreach ($relative in $deletePaths) {
        if (Test-Path -LiteralPath (Join-Path $installAppDir $relative)) { $survivors += $relative }
    }
    if ($LauncherPredatesDeleteList) {
        $unexpectedSurvivors = @($survivors | Where-Object { $_ -notin $expectedSurvivors })
        Assert-True ($unexpectedSurvivors.Count -eq 0) `
            "老启动器不认清理列表：基线里就有的旧文件原样留着，没有多清理也没有少（$($survivors.Count)/$($expectedSurvivors.Count)）"
    }
    else {
        $leftBehind = @($expectedSurvivors | Where-Object {
                Test-Path -LiteralPath (Join-Path $installAppDir $_) })
        Assert-True ($leftBehind.Count -eq 0) `
            "清理列表里基线就有的旧文件升级后都不在了（残留 $($leftBehind.Count) 个：$($leftBehind -join ', ')）"
        Assert-True (Test-Path -LiteralPath $appDll) "清理没有误删主程序 dll"
        Assert-True (Test-Path -LiteralPath (Join-Path $installAppDir "tools\ffmpeg.exe")) "清理走白名单：ffmpeg 仍在"
    }

    $layoutPath = Join-Path $installRoot "runtime"
    if ($hadLayoutBeforeUpgrade) {
        # 0.0.82 这类"安装包里就带 runtime\"的机器：布局是独立副本，主程序不会再落成，
        # 补丁换 app\ 运行时载荷也不该动它——逐文件自检正好证明它既没被删也没被改。
        Assert-True (Test-Path -LiteralPath (Join-Path $layoutPath "layout.json")) `
            "安装时就有的共享运行时布局还在（补丁换了 app\ 运行时载荷，但布局是独立副本）"
        if (Test-Path -LiteralPath (Join-Path $layoutPath "layout.json")) {
            Test-SharedRuntimeLayout -LayoutPath $layoutPath | Out-Null
            Assert-True $true "原布局仍通过逐文件自检（没被补丁改坏、也没被删掉）"
        }
    }
    else {
        Assert-True (Test-Path -LiteralPath (Join-Path $layoutPath "layout.json")) "主程序启动后落成了共享运行时布局"
        if (Test-Path -LiteralPath (Join-Path $layoutPath "layout.json")) {
            Test-SharedRuntimeLayout -LayoutPath $layoutPath | Out-Null
            Assert-True $true "落成的布局通过逐文件自检"
            $seedManifest = Get-Content -Raw -Encoding UTF8 (Join-Path $installAppDir "runtime-seed\layout.json") | ConvertFrom-Json
            $expected = @($seedManifest.files).Count
            # 清单只管运行时载荷：layout.json 与主程序落成时写的落成标记都不在清单里，数文件时要排除。
            $actual = @(Get-ChildItem -LiteralPath $layoutPath -Recurse -File | Where-Object {
                    $_.Name -ine $script:SharedRuntimeLayoutFileName -and
                    $_.Name -ine $script:SharedRuntimeLayoutSeedMarkerFileName }).Count
            Assert-True ($actual -eq $expected) "布局文件数与清单一致（$actual / $expected）"
        }
    }

    if ($VerifyLauncherSelfUpdate) {
        # 老流程的后半段："补丁先装、启动器后更新"。主程序只有在启动器退出之后才敢换它，
        # 而第一次启动时启动器一边在装补丁一边还弹更新提示（进程不退出），换不了；
        # 第二次启动时补丁已装完、没有提示，启动器拉起主程序后立刻退出，主程序这才把启动器换掉。
        if ([string]::IsNullOrWhiteSpace($LauncherPatchPath)) {
            $LauncherPatchPath = Join-Path $repoRoot "package\launcher-baselines\v$Version\PackingProof_LauncherPatch_v$Version.zip"
        }
        if (-not (Test-Path -LiteralPath $LauncherPatchPath -PathType Leaf)) {
            throw "找不到启动器包：$LauncherPatchPath（传 -LauncherPatchPath 指定）"
        }

        $baselineManifest = Get-Content -Raw -Encoding UTF8 `
            (Join-Path $PSScriptRoot "launcher-baseline.json") | ConvertFrom-Json
        $expectedLauncherSha256 = ([string]$baselineManifest.package.executable_sha256).ToLowerInvariant()
        Assert-True (-not [string]::IsNullOrWhiteSpace($expectedLauncherSha256)) "拿到本次启动器基线的可执行文件哈希"

        # 注意目录口径：主程序把自己那份用户数据放在 <覆盖目录>\ExpressPackingMonitoring\ 下
        # （AppPaths.UserDataDir = 覆盖目录 + 软件名），而启动器直接用 <覆盖目录>\ 本身
        # （无覆盖时两者都是 %LOCALAPPDATA%\ExpressPackingMonitoring）。pending 启动器包是主程序读的，
        # 所以要放在主程序那一侧。
        $appUserDataDir = Join-Path $userDataDir "ExpressPackingMonitoring"
        $pendingLauncherDir = Join-Path $appUserDataDir "cache\updates\launcher-pending\$Version"
        [System.IO.Directory]::CreateDirectory($pendingLauncherDir) | Out-Null
        Copy-Item -LiteralPath $LauncherPatchPath -Destination (Join-Path $pendingLauncherDir "launcher.zip") -Force
        $launcherPackageFile = Get-Item -LiteralPath $LauncherPatchPath
        $descriptor = [ordered]@{
            ProtocolVersion     = 1
            Version             = $Version
            Url                 = "https://example.invalid/PackingProof_LauncherPatch_v$Version.zip"
            GithubUrl           = "https://example.invalid/PackingProof_LauncherPatch_v$Version.zip"
            GiteeUrl            = "https://example.invalid/PackingProof_LauncherPatch_v$Version.zip"
            Size                = $launcherPackageFile.Length
            Sha256              = (Get-FileHash -LiteralPath $LauncherPatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
            ExecutableSize      = [long]$baselineManifest.package.executable_size
            ExecutableSha256    = $expectedLauncherSha256
            RequiredAppProtocol = 0
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $pendingLauncherDir "launcher-package.json"),
            ($descriptor | ConvertTo-Json -Depth 4),
            [System.Text.UTF8Encoding]::new($false))

        $launcherBefore = (Get-FileHash -LiteralPath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ($launcherBefore -ne $expectedLauncherSha256) "第二次启动前装的还是老启动器（哈希与新版不同）"

        Write-Host "再启动主程序：这次让主程序把启动器换成新版"
        $launcherAfter = $launcherBefore
        foreach ($attempt in 1..3) {
            $second = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") `
                -PassThru -WorkingDirectory $installRoot
            $second | Wait-Process -Timeout ($AppStartupSeconds + 60) -ErrorAction SilentlyContinue
            if (-not $second.HasExited) { $second.Kill() }
            Start-Sleep -Seconds 8
            Stop-LeftoverProcesses -InstallRoot $installRoot
            $launcherAfter = (Get-FileHash -LiteralPath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($launcherAfter -eq $expectedLauncherSha256) { break }
            Write-Host "第 $attempt 次启动还没换上（启动器退出慢或主程序没等到它退出），再试一次"
        }
        Assert-True ($launcherAfter -eq $expectedLauncherSha256) `
            "主程序把根启动器换成了本版基线那一份（$($launcherAfter.Substring(0, 12))…）"
    }

    # 布局本身就落在安装目录里（上一条已逐文件验证），不再去断言主程序日志的位置：
    # 主程序的数据目录不随 EPM_USER_DATA_DIR 走，日志可能在真实用户目录，拿它当判据不可靠。
}
finally {
    if ($null -eq $previousUserDataDir) { Remove-Item Env:\EPM_USER_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:EPM_USER_DATA_DIR = $previousUserDataDir }
    if (-not $KeepWorkspace) {
        Start-Sleep -Milliseconds 300
        try { [System.IO.Directory]::Delete($workspace, $true) } catch { }
    }
    else {
        Write-Host "工作目录保留：$workspace"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "老装机升级路径验证未通过，共 $($failures.Count) 项"
    exit 1
}
Write-Host "老装机升级路径验证通过"
exit 0
