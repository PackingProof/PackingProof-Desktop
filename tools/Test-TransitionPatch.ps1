[CmdletBinding()]
param(
    [switch]$KeepWorkspace
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 过渡补丁端到端验证：老装机（自包含、app\ 里平铺着整套运行时）只靠增量包升到共享运行时布局。
#   1) 用已发布的 0.0.81 自包含包当作“老装机”
#   2) 造一份新格式补丁：root=app 的应用差分 + root=runtime 的整份共享运行时
#   3) 把补丁放进启动器的 pending，跑启动器：应用补丁 → 迁移老平铺运行时 → 主程序从 runtime\ 起
#   4) 同一份补丁再用 tools\Apply-AppPatch.ps1（手动安装路径）应用到另一份精简老装机

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")
. (Join-Path $PSScriptRoot "AppPatchRuntimeCompatibility.Common.ps1")
. (Join-Path $PSScriptRoot "LauncherBaseline.Common.ps1")

# 直接复用打包脚本里的 New-AppPatchPackage，避免测试夹具和发布流程各写一套补丁格式。
# 这里只把函数定义和顶层常量取出来执行，不跑整条发布流程。
$packagingScriptPath = Join-Path $PSScriptRoot "Publish-CleanPackage.ps1"
$packagingTokens = $null
$packagingErrors = $null
$packagingAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $packagingScriptPath, [ref]$packagingTokens, [ref]$packagingErrors)
if ($packagingErrors.Count -gt 0) {
    throw "无法解析打包脚本：$packagingScriptPath（$($packagingErrors[0].Message)）"
}
$packagingFunctions = @($packagingAst.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
. ([scriptblock]::Create(($packagingFunctions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine))

function Import-PackagingStringConstants {
    param($Ast)

    foreach ($assignment in @($Ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst]
                }, $true))) {
        $expression = $assignment.Right
        if ($expression -is [System.Management.Automation.Language.PipelineAst]) {
            $elements = @($expression.PipelineElements)
            if ($elements.Count -ne 1) { continue }
            $expression = $elements[0]
        }
        if ($expression -is [System.Management.Automation.Language.CommandExpressionAst]) {
            $expression = $expression.Expression
        }
        if ($expression -isnot [System.Management.Automation.Language.ConstantExpressionAst]) { continue }
        Set-Variable -Name $assignment.Left.VariablePath.UserPath -Value $expression.Value -Scope Script
    }
}

Import-PackagingStringConstants -Ast $packagingAst
# Compress-PackageWithRetry 的参数默认值是 Optimal，这里补齐。
$ZipCompressionLevel = "Optimal"

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("epm-transition-patch-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "[OK]   $Message" }
    else { Write-Host "[FAIL] $Message"; $failures.Add($Message) }
}

function Stop-LeftoverProcesses {
    param([string]$InstallRoot)
    $prefix = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='ExpressPackingMonitoring.exe'")) {
        $path = [string]$process.ExecutablePath
        if ($path.Length -gt 0 -and $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Publish-FrameworkDependentApp {
    param(
        [string]$Destination,
        [string]$Version
    )

    # 补丁里的应用文件必须是待升级版本：启动器安装完会校验 app 里的软件版本等于补丁的 latest_version。
    & dotnet publish (Join-Path $repoRoot "src\ExpressPackingMonitoring\ExpressPackingMonitoring.csproj") `
        -c Release -r win-x64 --self-contained false --nologo `
        "-p:InformationalVersion=$Version" `
        "-p:IncludeSourceRevisionInInformationalVersion=false" `
        -o $Destination | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "应用发布失败，退出码 $LASTEXITCODE"
    }
    $dllPath = Join-Path $Destination "ExpressPackingMonitoring.dll"
    $publishedVersion = (Get-Item -LiteralPath $dllPath).VersionInfo.ProductVersion
    if ($publishedVersion -ne $Version) {
        throw "发布产物版本号不是 $Version，而是 $publishedVersion"
    }
}

try {
    $oldPackage = Join-Path $repoRoot "package\PackingProof+v0.0.81\PackingProof+v0.0.81"
    if (-not (Test-Path -LiteralPath (Join-Path $oldPackage "app\coreclr.dll"))) {
        throw "找不到自包含形态的老发布包（需要 package\PackingProof+v0.0.81）：$oldPackage"
    }
    $targetVersion = "0.0.90"
    $newAppDir = Join-Path $workspace "app-publish"
    Publish-FrameworkDependentApp -Destination $newAppDir -Version $targetVersion
    Write-Host "新应用目录：$newAppDir"

    # 新运行时布局（10.x），整份进补丁的 runtime 位置
    $newLayout = Join-Path $workspace "new-layout"
    New-SharedRuntimeLayout -AppDir $newAppDir -DestinationPath $newLayout | Out-Null
    Test-SharedRuntimeLayout -LayoutPath $newLayout | Out-Null

    $patchZip = Join-Path $workspace "PackingProof_AppPatch_v$targetVersion.zip"
    # 用发布流程的同一段逻辑造过渡补丁：app\ 放与新应用不同的文件，runtime\ 放整份布局
    New-AppPatchPackage `
        -CurrentAppDir $newAppDir `
        -BaselineDir (Join-Path $oldPackage "app") `
        -PatchZipPath $patchZip `
        -BaselineVersion "0.0.18" `
        -LatestVersion $targetVersion `
        -InstallerCmdPath (Join-Path $PSScriptRoot "Install-AppPatch.cmd") `
        -InstallerScriptPath (Join-Path $PSScriptRoot "Apply-AppPatch.ps1") `
        -ExcludeCompatibleRuntimes `
        -RuntimeDir $newLayout `
        -RetireListPath (Join-Path $workspace "app-patch-retire-list.json")

    $patchInfo = Get-Item -LiteralPath $patchZip
    Write-Host ("补丁包：{0:N1} MB" -f ($patchInfo.Length / 1MB))

    # 先验补丁包本身：清单的 root 落点、运行时物理位置、条目数都要对得上
    $verifyDir = Join-Path $workspace "patch-verify"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($patchZip, $verifyDir)
    $patchManifest = Get-Content -Raw -Encoding UTF8 (Join-Path $verifyDir "patch_manifest.json") | ConvertFrom-Json
    $runtimeEntries = @($patchManifest.files | Where-Object { $_.PSObject.Properties['root'] -and $_.root -eq 'runtime' })
    $appEntries = @($patchManifest.files | Where-Object { -not $_.PSObject.Properties['root'] })
    $layoutFileCount = @(Get-ChildItem -LiteralPath $newLayout -File -Recurse).Count
    Assert-True ($runtimeEntries.Count -eq $layoutFileCount) "过渡补丁带上了整份运行时（$($runtimeEntries.Count) 个条目）"
    Assert-True ($appEntries.Count -gt 0) "过渡补丁里的应用文件按缺省 app\ 落点（$($appEntries.Count) 个条目）"
    Assert-True (Test-Path -LiteralPath (Join-Path $verifyDir "files\.runtime\layout.json") -PathType Leaf) "运行时文件放在 files\.runtime\ 下"

    # 场景一：启动器自动安装 pending 补丁（无感升级路径）
    $installA = Join-Path $workspace "install-auto"
    Copy-Item -LiteralPath $oldPackage -Destination $installA -Recurse -Force
    # 现场顺序是“主程序先把根启动器换成新的”，补丁再由新启动器应用；这里直接放新启动器表示该前提已满足。
    $launcherPublish = Join-Path $workspace "launcher-out"
    & dotnet publish (Join-Path $repoRoot "src\ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj") -c Release --nologo -r win-x64 -o $launcherPublish | Out-Null
    Copy-Item -LiteralPath (Join-Path $launcherPublish "ExpressPackingMonitoring.exe") -Destination (Join-Path $installA "ExpressPackingMonitoring.exe") -Force

    $userDataA = Join-Path $workspace "userdata-auto"
    $pendingDir = Join-Path $userDataA "cache\updates\pending"
    New-Item -ItemType Directory -Force -Path $pendingDir | Out-Null
    Copy-Item -LiteralPath $patchZip -Destination (Join-Path $pendingDir "PackingProof_AppPatch_v$targetVersion.zip") -Force
    $updateManifest = [ordered]@{
        latest_version         = $targetVersion
        patch_baseline_version = "0.0.18"
        patch_supported        = $true
        patch_package          = [ordered]@{
            type   = "baseline_patch"
            url    = "https://example.invalid/patch.zip"
            sha256 = (Get-FileHash -LiteralPath $patchZip -Algorithm SHA256).Hash.ToLowerInvariant()
            size   = $patchInfo.Length
        }
    }
    [System.IO.File]::WriteAllText((Join-Path $pendingDir "update_manifest.json"), ($updateManifest | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))

    $trace = Join-Path $workspace "host-trace.log"
    $env:EPM_USER_DATA_DIR = $userDataA
    $env:COREHOST_TRACE = '1'
    $env:COREHOST_TRACEFILE = $trace
    $launcher = Start-Process -FilePath (Join-Path $installA "ExpressPackingMonitoring.exe") -ArgumentList '--viewer' -WorkingDirectory $installA -PassThru
    Start-Sleep -Seconds 60
    $appAlive = @(Get-CimInstance Win32_Process -Filter "Name='ExpressPackingMonitoring.exe'" | Where-Object { $_.ExecutablePath -like "$installA*" })

    $logPath = Join-Path $userDataA "log\launcher_update.log"
    $logText = if (Test-Path -LiteralPath $logPath) { Get-Content -Raw -Encoding UTF8 -LiteralPath $logPath } else { "" }
    Assert-True ($logText -like "*pending Patch 更新完成*") "启动器应用了 pending 过渡补丁"
    Assert-True ($logText -like "*共享运行时布局可用*") "补丁落地后布局可用"
    Assert-True (Test-Path -LiteralPath (Join-Path $installA "runtime\layout.json")) "共享运行时被投递到包根 runtime\"
    Assert-True (Test-Path -LiteralPath (Join-Path $installA "runtime\shared\Microsoft.WindowsDesktop.App")) "runtime\shared 下有框架目录"
    $installedVersionA = (Get-Item -LiteralPath (Join-Path $installA "app\ExpressPackingMonitoring.dll")).VersionInfo.ProductVersion
    Assert-True ($installedVersionA -eq $targetVersion) "老装机的应用文件已升到 v$targetVersion（实际 $installedVersionA）"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installA "app\hostfxr.dll"))) "老装机的 app\hostfxr.dll 已迁移走"
    Assert-True (Test-Path -LiteralPath (Join-Path $installA "runtime\.legacy\hostfxr.dll")) "迁移留了回滚备份"
    Assert-True ($logText -like "*本次迁移旧运行时文件*") "启动器日志记录了迁移"
    Assert-True ($appAlive.Count -gt 0) "主程序在补丁后正常启动"
    if (Test-Path -LiteralPath $trace) {
        $traceText = Get-Content -Raw -Encoding UTF8 -LiteralPath $trace
        Assert-True ($traceText -like "*$installA\runtime\shared\Microsoft.WindowsDesktop.App*") "主程序实际加载的运行时来自 runtime\ 布局"
    }
    else { Assert-True $false "没有拿到宿主跟踪日志" }
    Stop-LeftoverProcesses -InstallRoot $installA
    if (-not $launcher.HasExited) { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue }

    # 场景二：手动安装器用同一份补丁（离线路径）
    # 注意手动安装器优先读 config.json 里的 AppRootDirectory，必须把配置指向沙箱，
    # 否则会读到本机真实用户目录，把测试补丁打进正在用的安装目录。
    $installB = Join-Path $workspace "install-manual"
    New-Item -ItemType Directory -Force -Path (Join-Path $installB "app") | Out-Null
    Copy-Item -LiteralPath (Join-Path $oldPackage "app\ExpressPackingMonitoring.exe") -Destination (Join-Path $installB "app") -Force
    Copy-Item -LiteralPath (Join-Path $oldPackage "app\ExpressPackingMonitoring.dll") -Destination (Join-Path $installB "app") -Force
    foreach ($name in @("hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll")) {
        Copy-Item -LiteralPath (Join-Path $oldPackage "app\$name") -Destination (Join-Path $installB "app") -Force
    }
    $userDataB = Join-Path $workspace "userdata-manual"
    New-Item -ItemType Directory -Force -Path $userDataB | Out-Null
    $configB = Join-Path $userDataB "config.json"
    [System.IO.File]::WriteAllText(
        $configB,
        ('{"AppRootDirectory":"' + (Join-Path $installB "app").Replace('\', '\\') + '"}'),
        [System.Text.UTF8Encoding]::new($false))
    $expectedAppDllHash = (Get-FileHash -LiteralPath (Join-Path $newAppDir "ExpressPackingMonitoring.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
    $extractedPatch = Join-Path $workspace "patch-extracted"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($patchZip, $extractedPatch)
    & pwsh -NoProfile -File (Join-Path $extractedPatch "apply_app_patch.ps1") `
        -PatchRoot $extractedPatch `
        -ConfigPath $configB `
        -SkipProcessCheck 2>&1 | Select-Object -Last 4
    Assert-True (Test-Path -LiteralPath (Join-Path $installB "runtime\layout.json")) "手动安装器把运行时装到了包根 runtime\"
    Assert-True (Test-Path -LiteralPath (Join-Path $installB "runtime\dotnet.exe")) "手动安装器把 dotnet.exe 也带上了"
    $installedAppDllHash = (Get-FileHash -LiteralPath (Join-Path $installB "app\ExpressPackingMonitoring.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($installedAppDllHash -eq $expectedAppDllHash) "手动安装器把应用文件换成了补丁里的版本"
    $manualLogPath = Join-Path $userDataB "log\manual_update.log"
    $manualLogText = if (Test-Path -LiteralPath $manualLogPath) { Get-Content -Raw -Encoding UTF8 -LiteralPath $manualLogPath } else { "" }
    Assert-True ($manualLogText -like "*Manual patch completed*") "手动安装器日志记录了成功"
}
finally {
    Remove-Item Env:\EPM_USER_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:\COREHOST_TRACE -ErrorAction SilentlyContinue
    Remove-Item Env:\COREHOST_TRACEFILE -ErrorAction SilentlyContinue
    if ($KeepWorkspace) { Write-Host "工作目录保留：$workspace" }
    else {
        Start-Sleep -Milliseconds 500
        try { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "过渡补丁验证未通过，共 $($failures.Count) 项"
    exit 1
}
Write-Host "过渡补丁验证通过"
exit 0
