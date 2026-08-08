[CmdletBinding()]
param(
    [string]$PatchRoot = "",
    [string]$ConfigPath = "",
    [string]$LauncherRootPath = "",
    [switch]$SkipProcessCheck
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

if ([string]::IsNullOrEmpty($PatchRoot)) {
    if ($MyInvocation.MyCommand.Path) {
        $PatchRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    else {
        throw "无法确定补丁根目录，请通过 -PatchRoot 指定"
    }
}

function Write-UpdateHost {
    param(
        [string]$Message = "",
        [System.ConsoleColor]$ForegroundColor = [System.ConsoleColor]::Gray
    )
    try {
        if ($PSBoundParameters.ContainsKey('ForegroundColor')) {
            Write-Host $Message -ForegroundColor $ForegroundColor
        }
        else {
            Write-Host $Message
        }
    }
    catch {
        # Windows 7 PowerShell 2.0 控制台偶发 0x1F（设备未就绪），
        # 输出失败不应中断更新流程；降级到标准输出尽力显示。
        try { [Console]::Out.WriteLine($Message) } catch { }
    }
}

function Test-IsNullOrWhiteSpace {
    param([string]$Value)
    return [string]::IsNullOrEmpty($Value) -or $Value.Trim().Length -eq 0
}

function ConvertFrom-Json35 {
    param([string]$Json)
    if (Get-Command ConvertFrom-Json -ErrorAction SilentlyContinue) {
        return $Json | ConvertFrom-Json
    }
    Add-Type -AssemblyName System.Web.Extensions -ErrorAction SilentlyContinue
    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength = 104857600
    return $serializer.DeserializeObject($Json)
}

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) {
        return $null
    }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.ContainsKey($Name)) {
            return $Object[$Name]
        }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }
    return $null
}

function Get-FileSha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        # PowerShell 2.0 兼容：HashAlgorithm 用 Clear()，FileStream 用 Close()。
        $sha.Clear()
        $stream.Close()
    }
}

function Get-DefaultConfigPath {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (Test-IsNullOrWhiteSpace $localAppData) {
        $localAppData = $env:LOCALAPPDATA
    }
    if (Test-IsNullOrWhiteSpace $localAppData) {
        throw "无法定位 Windows 用户数据目录"
    }
    return Join-Path $localAppData "ExpressPackingMonitoring\config.json"
}

function Get-ShortcutTargetPath {
    param([string]$ShortcutPath)

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        return ([string]$shortcut.TargetPath).Trim()
    }
    finally {
        if ($null -ne $shortcut) {
            try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) } catch { }
        }
        if ($null -ne $shell) {
            try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) } catch { }
        }
    }
}

function Resolve-LauncherRootCandidate {
    param([string]$CandidatePath)

    $value = $CandidatePath.Trim().Trim('"')
    if (Test-IsNullOrWhiteSpace $value) {
        throw "安装位置不能为空"
    }
    if (-not [System.IO.Path]::IsPathRooted($value)) {
        throw "安装位置必须是完整路径"
    }
    $value = [System.IO.Path]::GetFullPath($value)
    if ([string]::Equals([System.IO.Path]::GetExtension($value), ".lnk", [System.StringComparison]::OrdinalIgnoreCase)) {
        if (-not (Test-Path -LiteralPath $value -PathType Leaf)) {
            throw "快捷方式不存在"
        }
        $value = Get-ShortcutTargetPath -ShortcutPath $value
        if (Test-IsNullOrWhiteSpace $value) {
            throw "无法读取快捷方式指向的软件位置"
        }
        $value = [System.IO.Path]::GetFullPath($value)
    }
    if (Test-Path -LiteralPath $value -PathType Leaf) {
        if (-not [string]::Equals(
            [System.IO.Path]::GetFileName($value),
            "ExpressPackingMonitoring.exe",
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "请选择软件根目录或 ExpressPackingMonitoring.exe"
        }
        $value = Split-Path -Parent $value
    }
    if (-not (Test-Path -LiteralPath $value -PathType Container)) {
        throw "安装位置不存在"
    }

    $root = $value.TrimEnd([char[]]"\/")
    if ([string]::Equals((Split-Path -Leaf $root), "app", [System.StringComparison]::OrdinalIgnoreCase)) {
        $root = Split-Path -Parent $root
    }
    $launcherPath = Join-Path $root "ExpressPackingMonitoring.exe"
    $appDirectory = Join-Path $root "app"
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appDirectory -PathType Container)) {
        throw "所选位置不是标准 PackingProof 安装目录"
    }
    return [System.IO.Path]::GetFullPath($root).TrimEnd([char[]]"\/")
}

function Request-LauncherRootDirectory {
    Write-UpdateHost ""
    Write-UpdateHost "未能自动找到软件位置。" -ForegroundColor Yellow
    Write-UpdateHost "请把软件根目录、根目录 ExpressPackingMonitoring.exe 或桌面快捷方式拖到此窗口，然后按 Enter。"
    return Resolve-LauncherRootCandidate -CandidatePath (Read-Host "安装位置")
}

function Write-UpdateLog {
    param([string]$LogPath, [string]$Message)

    try {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
        Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value (
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}" -f [DateTime]::Now, $Message)
    }
    catch { }
}

function Stop-InstalledLauncher {
    param([string]$LauncherPath)

    $normalizedTarget = [System.IO.Path]::GetFullPath($LauncherPath)
    foreach ($process in @(Get-Process -Name "ExpressPackingMonitoring" -ErrorAction SilentlyContinue)) {
        $processPath = ""
        try { $processPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName) } catch { }
        if (-not [string]::Equals($processPath, $normalizedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        $process.WaitForExit(10000)
    }
}

if (Test-IsNullOrWhiteSpace $ConfigPath) {
    $ConfigPath = Get-DefaultConfigPath
}
$PatchRoot = [System.IO.Path]::GetFullPath($PatchRoot)
$ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
$userDataDirectory = Split-Path -Parent $ConfigPath
$logPath = Join-Path $userDataDirectory "log\manual_update.log"
$manifestPath = Join-Path $PatchRoot "launcher_patch_manifest.json"
$sourceLauncherPath = Join-Path $PatchRoot "ExpressPackingMonitoring.exe"
$temporaryPath = ""
$adjacentBackupPath = ""
$exitCode = 0

try {
    Write-UpdateHost "正在检查启动器更新包..." -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sourceLauncherPath -PathType Leaf)) {
        throw "LauncherPatch 缺少启动器或校验清单"
    }
    $manifest = ConvertFrom-Json35 -Json ([System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8))
    if (-not [string]::Equals([string](Get-JsonProperty $manifest "type"), "launcher_patch", [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string](Get-JsonProperty $manifest "file"), "ExpressPackingMonitoring.exe", [System.StringComparison]::Ordinal)) {
        throw "启动器更新清单无效"
    }
    $expectedSize = [long](Get-JsonProperty $manifest "size")
    $expectedHash = ([string](Get-JsonProperty $manifest "sha256")).Trim().ToLowerInvariant()
    if ((Get-Item -LiteralPath $sourceLauncherPath).Length -ne $expectedSize -or
        (Get-FileSha256 -Path $sourceLauncherPath) -ne $expectedHash) {
        throw "启动器文件大小或 SHA256 校验失败"
    }

    $launcherRoot = ""
    if (-not (Test-IsNullOrWhiteSpace $LauncherRootPath)) {
        $launcherRoot = Resolve-LauncherRootCandidate -CandidatePath $LauncherRootPath
    }
    else {
        try {
            $config = ConvertFrom-Json35 -Json ([System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8))
            $configRoot = [string](Get-JsonProperty $config "AppRootDirectory")
            $launcherRoot = Resolve-LauncherRootCandidate -CandidatePath $configRoot
        }
        catch {
            Write-UpdateLog -LogPath $logPath -Message "Unable to resolve launcher root from config: $($_.Exception.Message)"
        }
    }
    if (Test-IsNullOrWhiteSpace $launcherRoot) {
        $launcherRoot = Request-LauncherRootDirectory
    }

    $targetLauncherPath = Join-Path $launcherRoot "ExpressPackingMonitoring.exe"
    if ((Get-FileSha256 -Path $targetLauncherPath) -eq $expectedHash) {
        Write-UpdateHost "当前启动器已经是此版本，无需重复更新" -ForegroundColor Green
        return
    }
    if (-not $SkipProcessCheck) {
        Stop-InstalledLauncher -LauncherPath $targetLauncherPath
    }

    $temporaryPath = Join-Path $launcherRoot (".launcher-manual-update-" + [Guid]::NewGuid().ToString("N") + ".tmp")
    $adjacentBackupPath = Join-Path $launcherRoot (".launcher-manual-backup-" + [Guid]::NewGuid().ToString("N") + ".bak")
    Copy-Item -LiteralPath $sourceLauncherPath -Destination $temporaryPath
    if ((Get-FileSha256 -Path $temporaryPath) -ne $expectedHash) {
        throw "复制后的启动器 SHA256 校验失败"
    }

    [System.IO.File]::Replace($temporaryPath, $targetLauncherPath, $adjacentBackupPath, $true)
    $temporaryPath = ""
    try {
        if ((Get-FileSha256 -Path $targetLauncherPath) -ne $expectedHash) {
            throw "更新后的启动器 SHA256 校验失败"
        }
    }
    catch {
        if (Test-Path -LiteralPath $adjacentBackupPath -PathType Leaf) {
            [System.IO.File]::Replace($adjacentBackupPath, $targetLauncherPath, $null, $true)
            $adjacentBackupPath = ""
        }
        throw
    }

    $backupDirectory = Join-Path $userDataDirectory "cache\launcher_backups"
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    $retainedBackup = Join-Path $backupDirectory ("manual-launcher-{0:yyyyMMdd-HHmmssfff}.bak" -f [DateTime]::Now)
    Move-Item -LiteralPath $adjacentBackupPath -Destination $retainedBackup
    $adjacentBackupPath = ""
    $retainedBackups = @(Get-ChildItem -LiteralPath $backupDirectory -Filter "manual-launcher-*.bak" |
        Where-Object { -not $_.PSIsContainer } |
        Sort-Object LastWriteTimeUtc -Descending)
    for ($backupIndex = 3; $backupIndex -lt $retainedBackups.Count; $backupIndex++) {
        Remove-Item -LiteralPath $retainedBackups[$backupIndex].FullName -Force
    }

    Write-UpdateLog -LogPath $logPath -Message "Manual launcher patch completed: root=$launcherRoot"
    Write-UpdateHost ""
    Write-UpdateHost "启动器更新完成" -ForegroundColor Green
    Write-UpdateHost "下次从原来的入口打开 PackingProof 即可"
}
catch {
    $failure = $_.Exception.Message
    Write-UpdateLog -LogPath $logPath -Message "Manual launcher patch failed: $failure"
    Write-UpdateHost ""
    Write-UpdateHost "启动器更新失败：$failure" -ForegroundColor Red
    Write-UpdateHost "现有主程序、录像、配置和数据库均未修改"
    $exitCode = 1
}
finally {
    if ((-not (Test-IsNullOrWhiteSpace $temporaryPath)) -and (Test-Path -LiteralPath $temporaryPath)) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    if ((-not (Test-IsNullOrWhiteSpace $adjacentBackupPath)) -and (Test-Path -LiteralPath $adjacentBackupPath)) {
        Write-UpdateLog -LogPath $logPath -Message "Retained adjacent launcher backup after failure: $adjacentBackupPath"
    }
}

exit $exitCode