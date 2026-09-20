#requires -Version 7.2

<#
.SYNOPSIS
    现场诊断摄像头采集链路：确认新的采集后端与 GPU 转换有没有真的生效，并量出实际开销差。

.DESCRIPTION
    把本文件复制到现场电脑后运行（放在打包目录旁边时脚本会自动找到 app 目录）：

        pwsh -NoProfile -File .\Diagnose-CameraPipeline.ps1

    脚本只读，不改配置、不动录像、不写现场 runtime.log（探针日志写到输出目录）：

      1. 机器与摄像头环境：系统、显卡与驱动、摄像头设备（PnP / Media Foundation / DirectShow）
      2. 现场配置与历史结论：config.json 的采集相关项，runtime.log 里后端选择与 GPU 启停记录
    3. 本机实测：复用已安装程序集里的真实采集代码，在同一台设备上分别跑 GPU 与 CPU 转换
    4. 汇总成一个文本报告加探针日志，直接发回即可

    另外单独量一段"整帧拷贝成本"：处理循环以前每帧都克隆整帧（开水面录制时还要再克隆一份），
    现在改成所有权交接后这两次拷贝都没有了。这一段只读程序目录里的 OpenCvSharp.dll，
    不碰摄像头、也不用退出主程序，随手跑一次就知道这台机器到底省下多少。

    第 3 步要独占摄像头，请先退出正在运行的打包程序（脚本会检测并提示）

    常用参数：
      -AppDir       打包程序目录（含 ExpressPackingMonitoring.dll），默认自动查找
      -DeviceName   只测名字里含该片段的摄像头
      -Rounds       实测轮数，每轮 = GPU,CPU,CPU,GPU 四段，默认 1（想更稳可以 -Rounds 2）
      -SkipMeasure  只收集环境、配置与日志，不开摄像头
#>

[CmdletBinding()]
param(
    [string]$AppDir,
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "ExpressPackingMonitoring"),
    [string]$OutputDir = (Join-Path ([Environment]::GetFolderPath("Desktop")) "摄像头链路诊断"),
    [string]$DeviceName = "",
    [int]$MeasureSeconds = 6,
    [int]$FrameWidth = 0,
    [int]$FrameHeight = 0,
    [int]$Fps = 0,
    [int]$Rounds = 1,
    [switch]$SkipMeasure
)

$ErrorActionPreference = "Continue"

$script:Report = [System.Collections.Generic.List[string]]::new()
$script:ReportPath = ""
$script:CameraLogPath = ""
$script:ProbeDataDir = ""
$script:MeasureSummary = $null
$script:GpuCapability = $null
$script:TargetDeviceName = ""
$script:ConfiguredCameraInMf = $null
$script:OriginalPath = $env:PATH
$script:OriginalCurrentDirectory = [Environment]::CurrentDirectory

function Add-Line {
    param([string]$Text = "")

    $script:Report.Add($Text)
    Write-Host $Text
}

function Add-Section {
    param([string]$Title)

    Add-Line ""
    Add-Line ("=" * 62)
    Add-Line $Title
    Add-Line ("=" * 62)
}

function Save-Report {
    if ([string]::IsNullOrWhiteSpace($script:ReportPath)) {
        return
    }

    [IO.File]::WriteAllLines($script:ReportPath, $script:Report, [Text.UTF8Encoding]::new($true))
}

function Format-Number {
    param([double]$Value, [string]$Format = "F2")

    if ([double]::IsNaN($Value) -or [double]::IsInfinity($Value)) {
        return "n/a"
    }

    return $Value.ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}

function Resolve-AppDirectory {
    param([string]$ExplicitPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add($ExplicitPath)
    }

    foreach ($relative in @("app", "..\app", "..\..\app", ".")) {
        $candidates.Add((Join-Path $PSScriptRoot $relative))
    }

    $candidates.Add((Join-Path $env:LOCALAPPDATA "Programs\ExpressPackingMonitoring\app"))
    foreach ($programFiles in @(
        [Environment]::GetFolderPath("ProgramFiles"),
        [Environment]::GetFolderPath("ProgramFilesX86")
    )) {
        if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
            $candidates.Add((Join-Path $programFiles "ExpressPackingMonitoring\app"))
            $candidates.Add((Join-Path $programFiles "PackingProof\app"))
        }
    }

    $uninstallRoots = @(
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($root in $uninstallRoots) {
        Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object {
                $_.DisplayName -like "*PackingProof*" -or $_.DisplayName -like "*ExpressPackingMonitoring*"
            } |
            ForEach-Object {
                if (-not [string]::IsNullOrWhiteSpace($_.InstallLocation)) {
                    $candidates.Add($_.InstallLocation)
                    $candidates.Add((Join-Path $_.InstallLocation "app"))
                }
            }
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or -not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        if (Test-Path -LiteralPath (Join-Path $resolved "ExpressPackingMonitoring.dll")) {
            return $resolved
        }
    }

    return ""
}

function Read-ConfigDocument {
    param([string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        return $null
    }

    try {
        $json = [IO.File]::ReadAllText($ConfigPath, [Text.Encoding]::UTF8)
        return $json | ConvertFrom-Json -AsHashtable
    }
    catch {
        Write-Host "读取配置失败：$($_.Exception.Message)"
        return $null
    }
}

function Read-ConfigValue {
    param($Config, [string]$Name)

    if ($null -eq $Config -or -not $Config.ContainsKey($Name)) {
        return ""
    }

    return [string]$Config[$Name]
}

<#
设备实例键：DirectShow 的 moniker 与 Media Foundation 的符号链接，对同一台摄像头
除接口类 GUID 之外逐字符相同（DirectShow 报 KSCATEGORY_CAPTURE {65e8773d-…}，
Media Foundation 报 KSCATEGORY_VIDEO_CAMERA {e5323777-…}）。

把 GUID 这一段算进键里，每一台物理摄像头都会"配不上"，结论正好是反的
（之前主程序自己也踩过这个坑：GPU 代码全在，却一次都没跑起来）。
所以这里只保留 GUID 之前的物理实例路径，与 MfDeviceMatcher 的口径一致。
#>
function Get-DeviceInstanceKey {
    param([string]$Identifier)

    if ([string]::IsNullOrWhiteSpace($Identifier)) {
        return ""
    }

    $value = $Identifier.Trim()
    foreach ($prefix in @("@device:pnp:", "@device:sw:")) {
        if ($value.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $value = $value.Substring($prefix.Length)
            break
        }
    }

    $guidIndex = $value.IndexOf("#{", [StringComparison]::Ordinal)
    if ($guidIndex -ge 0) {
        $value = $value.Substring(0, $guidIndex)
    }

    return $value.Trim().ToLowerInvariant()
}

# 只存在于 DirectShow 的软件设备（OBS 虚拟摄像头这类）没有 Media Foundation 实现
function Test-SoftwareOnlyCamera {
    param([string]$Moniker)

    if ([string]::IsNullOrWhiteSpace($Moniker)) {
        return $false
    }

    return $Moniker.Trim().StartsWith("@device:sw:", [StringComparison]::OrdinalIgnoreCase)
}

function Get-PnpCameraDevices {
    $devices = Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.PNPClass -eq "Camera" -or $_.PNPClass -eq "Image" }

    foreach ($device in $devices) {
        [pscustomobject]@{
            Name = [string]$device.Name
            Class = [string]$device.PNPClass
            Status = [string]$device.Status
            DeviceId = [string]$device.DeviceID
        }
    }
}

function Test-VirtualCameraDevice {
    param([string]$Name, [string]$DeviceId)

    if ($DeviceId -like "ROOT\*") {
        return $true
    }

    foreach ($marker in @(
        "Iriun", "OBS", "Virtual", "虚拟", "e2eSoft", "e2eSoft", "Unity", "ManyCam",
        "DroidCam", "Snap Camera", "XSplit", "NVIDIA Broadcast", "Meta"
    )) {
        if ($Name -like "*$marker*") {
            return $true
        }
    }

    return $false
}

function Get-DirectShowDeviceList {
    param([string]$ResolvedAppDir)

    $result = [pscustomobject]@{
        FfmpegPath = ""
        Names = @()
    }

    if ([string]::IsNullOrWhiteSpace($ResolvedAppDir)) {
        return $result
    }

    $ffmpeg = @(
        (Join-Path $ResolvedAppDir "tools\ffmpeg.exe"),
        (Join-Path $ResolvedAppDir "ffmpeg.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($ffmpeg)) {
        return $result
    }

    $result.FfmpegPath = $ffmpeg
    try {
        $output = & $ffmpeg -hide_banner -list_devices true -f dshow -i dummy 2>&1 | Out-String
    }
    catch {
        return $result
    }

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($output -split "`r?`n")) {
        $match = [regex]::Match($line, '"([^"]+)"\s+\(video\)')
        if ($match.Success) {
            $names.Add($match.Groups[1].Value)
        }
    }

    $result.Names = @($names)
    return $result
}

function Get-CameraLogLines {
    param([string]$LogDir)

    $patterns = @(
        "StartCamera success",
        "StartCamera skipped",
        "Media Foundation",
        "GPU 转换",
        "GPU 转换就绪",
        "回退到 CPU 转换",
        "Camera frame format=",
        "摄像头原生格式"
    )

    $result = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 2; $index++) {
        $fileName = if ($index -eq 0) { "runtime.old.log" } else { "runtime.log" }
        $path = Join-Path $LogDir $fileName
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        foreach ($line in [IO.File]::ReadLines($path, [Text.Encoding]::UTF8)) {
            foreach ($pattern in $patterns) {
                if ($line.Contains($pattern)) {
                    $result.Add([pscustomobject]@{
                        File = $fileName
                        Line = $line
                    })
                    break
                }
            }
        }
    }

    return @($result)
}

function Get-TypeMethod {
    param([Type]$Type, [string]$Name, [int]$ParameterCount = -1, [switch]$Static)

    if ($null -eq $Type) {
        throw "类型不存在：$Name"
    }

    $flags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
    $flags = if ($Static) {
        $flags -bor [Reflection.BindingFlags]::Static
    }
    else {
        $flags -bor [Reflection.BindingFlags]::Instance
    }

    $methods = @($Type.GetMethods($flags) | Where-Object { $_.Name -eq $Name })
    if ($ParameterCount -ge 0) {
        $methods = @($methods | Where-Object { $_.GetParameters().Count -eq $ParameterCount })
    }

    if ($methods.Count -eq 0) {
        throw "找不到方法 $($Type.FullName).$Name"
    }

    return $methods[0]
}

function Get-TypeProperty {
    param([Type]$Type, [string]$Name)

    $flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
    return $Type.GetProperty($Name, $flags)
}

<#
反射调用统一走这两个带类型的包装。

直接用 $MethodInfo.Invoke(...) 时 PowerShell 会拿运行时的重载去猜，参数里带 $null
或数组时可能绑到 Invoke(object, BindingFlags, Binder, object[], CultureInfo) 上去，
报成"无法把 Object[] 转成 UInt32"。参数声明成 MethodInfo / ConstructorInfo 之后
只会在正确的重载里选，现场不会因为绑定歧义失败。
#>
function Invoke-Method {
    param(
        [System.Reflection.MethodInfo]$Method,
        $Target,
        [object[]]$Arguments
    )

    return $Method.Invoke($Target, $Arguments)
}

function Invoke-Ctor {
    param(
        [System.Reflection.ConstructorInfo]$Ctor,
        [object[]]$Arguments
    )

    return $Ctor.Invoke($Arguments)
}

<#
进程 CPU 周期数取数。

Windows 的进程 CPU 时间是按 ~15.6ms 计时器节拍记账的，短窗口下量出来会带台阶、
甚至出现"自报 109ms、外部观察 0ms"这种噪声。QueryProcessCycleTime 给的是周期数，
没有这个台阶，跟仓库里 GPU/CPU 对照用例用的是同一路数据。
#>
function Enable-CycleCounter {
    if ($script:CycleCounterReady) {
        return $true
    }

    try {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace PackingProofProbe
{
    public static class CycleCounter
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

        public static ulong Read(IntPtr process)
        {
            if (!QueryProcessCycleTime(process, out ulong cycles))
                throw new InvalidOperationException("QueryProcessCycleTime 失败：" + Marshal.GetLastWin32Error());

            return cycles;
        }
    }
}
'@ -ErrorAction Stop | Out-Null
        $script:CycleCounterReady = $true
    }
    catch {
        $script:CycleCounterReady = $false
        Write-Host "CPU 周期计数不可用，退回进程 CPU 时间：$($_.Exception.Message)"
    }

    return $script:CycleCounterReady
}

function Read-ProcessCycles {
    param([IntPtr]$Handle)

    if (-not $script:CycleCounterReady) {
        return [long]0
    }

    return [long][PackingProofProbe.CycleCounter]::Read($Handle)
}

function New-CaptureContext {
    param([string]$ResolvedAppDir)

    $env:PATH = "$ResolvedAppDir;$script:OriginalPath"
    [Environment]::CurrentDirectory = $ResolvedAppDir

    $mfNamespace = "ExpressPackingMonitoring.Services.MediaFoundation"
    $gpuNamespace = "ExpressPackingMonitoring.Services.Gpu"

    $context = [pscustomobject]@{
        PlatformType = Get-CaptureType $mfNamespace "MfPlatform"
        DeviceType = Get-CaptureType $mfNamespace "MfCaptureDevice"
        SourceType = Get-CaptureType $mfNamespace "MfCameraSource"
        ProbeType = Get-CaptureType $mfNamespace "MfCaptureProbe"
        FormatType = Get-CaptureType $mfNamespace "MfNativeFormat"
        ConverterType = Get-CaptureType $gpuNamespace "GpuFrameConverter"
    }

    $context | Add-Member -NotePropertyName StartPlatform -NotePropertyValue (Get-TypeMethod $context.PlatformType "TryStart" 0 -Static)
    $context | Add-Member -NotePropertyName EnumerateDevices -NotePropertyValue (Get-TypeMethod $context.DeviceType "Enumerate" 0 -Static)
    $context | Add-Member -NotePropertyName ReadFormats -NotePropertyValue (Get-TypeMethod $context.DeviceType "ReadNativeFormats" 1 -Static)
    $context | Add-Member -NotePropertyName ProbeDevice -NotePropertyValue (Get-TypeMethod $context.ProbeType "Probe" 6 -Static)
    $context | Add-Member -NotePropertyName CreateConverter -NotePropertyValue (Get-TypeMethod $context.ConverterType "TryCreate" 6 -Static)

    $context | Add-Member -NotePropertyName DeviceName -NotePropertyValue (Get-TypeProperty $context.DeviceType "Name")
    $context | Add-Member -NotePropertyName DeviceLink -NotePropertyValue (Get-TypeProperty $context.DeviceType "SymbolicLink")
    $context | Add-Member -NotePropertyName FormatWidth -NotePropertyValue (Get-TypeProperty $context.FormatType "Width")
    $context | Add-Member -NotePropertyName FormatHeight -NotePropertyValue (Get-TypeProperty $context.FormatType "Height")
    $context | Add-Member -NotePropertyName FormatRate -NotePropertyValue (Get-TypeProperty $context.FormatType "FrameRate")
    $context | Add-Member -NotePropertyName FormatSubtype -NotePropertyValue (Get-TypeProperty $context.FormatType "SubtypeName")
    $context | Add-Member -NotePropertyName ConverterFailure -NotePropertyValue (Get-TypeProperty $context.ConverterType "LastCreateFailure")
    $context | Add-Member -NotePropertyName ConverterFeatureLevel -NotePropertyValue (Get-TypeProperty $context.ConverterType "FeatureLevel")

    $sourceFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $context | Add-Member -NotePropertyName SourceCtor -NotePropertyValue (
        $context.SourceType.GetConstructor(
            $sourceFlags,
            $null,
            [Type[]]@([string], [int], [int], [int], [string]),
            $null))
    $context | Add-Member -NotePropertyName Start -NotePropertyValue (Get-TypeMethod $context.SourceType "Start" 0)
    $context | Add-Member -NotePropertyName Stop -NotePropertyValue (Get-TypeMethod $context.SourceType "Stop" 0)
    $context | Add-Member -NotePropertyName DisableGpu -NotePropertyValue (Get-TypeMethod $context.SourceType "DisableGpuConversionUpFront" 1)
    $context | Add-Member -NotePropertyName ResetStages -NotePropertyValue (Get-TypeMethod $context.SourceType "ResetStageTimings" 0)
    $context | Add-Member -NotePropertyName ActualFormat -NotePropertyValue (Get-TypeProperty $context.SourceType "ActualFormat")
    $context | Add-Member -NotePropertyName ActualWidth -NotePropertyValue (Get-TypeProperty $context.SourceType "ActualWidth")
    $context | Add-Member -NotePropertyName ActualHeight -NotePropertyValue (Get-TypeProperty $context.SourceType "ActualHeight")
    $context | Add-Member -NotePropertyName ActualFps -NotePropertyValue (Get-TypeProperty $context.SourceType "ActualFps")
    $context | Add-Member -NotePropertyName UsesBt709 -NotePropertyValue (Get-TypeProperty $context.SourceType "UsesBt709")
    $context | Add-Member -NotePropertyName LastStartFailure -NotePropertyValue (Get-TypeProperty $context.SourceType "LastStartFailure")
    $context | Add-Member -NotePropertyName LastFrameFailure -NotePropertyValue (Get-TypeProperty $context.SourceType "LastFrameFailure")
    $context | Add-Member -NotePropertyName ReadStats -NotePropertyValue (Get-TypeProperty $context.SourceType "ReadStats")
    $context | Add-Member -NotePropertyName StageTimings -NotePropertyValue (Get-TypeProperty $context.SourceType "StageTimings")
    $context | Add-Member -NotePropertyName UsesGpu -NotePropertyValue (Get-TypeProperty $context.SourceType "UsesGpuConversion")
    $context | Add-Member -NotePropertyName GpuReason -NotePropertyValue (Get-TypeProperty $context.SourceType "GpuDisableReason")
    $context | Add-Member -NotePropertyName GpuConverterField -NotePropertyValue (
        $context.SourceType.GetField("_gpuConverter", $sourceFlags))

    return $context
}

function Get-CaptureType {
    param([string]$Namespace, [string]$Name)

    $type = $script:CaptureAssembly.GetType("$Namespace.$Name")
    if ($null -eq $type) {
        throw "当前安装的程序集里没有 $Namespace.$Name"
    }

    return $type
}

function Format-NativeFormat {
    param($Context, $Format)

    return "{0}x{1}@{2} {3}" -f `
        $Context.FormatWidth.GetValue($Format), `
        $Context.FormatHeight.GetValue($Format), `
        (Format-Number ([double]$Context.FormatRate.GetValue($Format)) "F0"), `
        $Context.FormatSubtype.GetValue($Format)
}

function Measure-CaptureRun {
    param(
        $Context,
        [string]$SymbolicLink,
        [int]$Width,
        [int]$Height,
        [int]$TargetFps,
        [string]$ColorMatrix,
        [bool]$UseGpu,
        [int]$Seconds)

    $run = [ordered]@{
        Label = if ($UseGpu) { "GPU" } else { "CPU" }
        Started = $false
        GotFrame = $false
        GpuActive = $false
        GpuReason = ""
        FeatureLevel = ""
        Format = ""
        Width = 0
        Height = 0
        NegotiatedFps = 0.0
        Bt709 = $false
        Frames = 0
        Seconds = 0.0
        MeasuredFps = 0.0
        MegaCyclesPerFrame = 0.0
        CpuMsPerFrame = 0.0
        ReadMs = 0.0
        ConvertMs = 0.0
        PublishMs = 0.0
        TimedFrames = 0
        Failure = ""
    }

    $source = $null
    try {
        $source = Invoke-Ctor $Context.SourceCtor ([object[]]@($SymbolicLink, $Width, $Height, $TargetFps, $ColorMatrix))
        if (-not $UseGpu) {
            Invoke-Method $Context.DisableGpu $source ([object[]]@("现场诊断：强制 CPU 对照")) | Out-Null
        }

        if (-not [bool](Invoke-Method $Context.Start $source @())) {
            $run.Failure = "启动失败：$($Context.LastStartFailure.GetValue($source))"
            return [pscustomobject]$run
        }

        $run.Started = $true

        # 与主程序一致：协商成功不算可用，必须真的等到一帧
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        $samples = 0
        while ([DateTime]::UtcNow -lt $deadline) {
            $samples = [int]$Context.ReadStats.GetValue($source).Item2
            if ($samples -gt 0) {
                break
            }
            Start-Sleep -Milliseconds 100
        }

        if ($samples -le 0) {
            $run.Failure = "协商成功但没有出帧（主程序同样会判定为不可用）"
            return [pscustomobject]$run
        }

        $run.GotFrame = $true
        $run.Format = [string]$Context.ActualFormat.GetValue($source)
        $run.Width = [int]$Context.ActualWidth.GetValue($source)
        $run.Height = [int]$Context.ActualHeight.GetValue($source)
        $run.NegotiatedFps = [double]$Context.ActualFps.GetValue($source)
        $run.Bt709 = [bool]$Context.UsesBt709.GetValue($source)
        $run.GpuActive = [bool]$Context.UsesGpu.GetValue($source)
        $run.GpuReason = [string]$Context.GpuReason.GetValue($source)
        $converter = $Context.GpuConverterField.GetValue($source)
        if ($null -ne $converter) {
            $run.FeatureLevel = [string]$Context.ConverterFeatureLevel.GetValue($converter)
        }

        # 丢掉预热：建立采集图、首帧、着色器编译、D3D 设备与回读暂存纹理的首次使用
        # 都发生在这一段。预热不足时第一段会虚高（实测 35M vs 稳定后的 11M 周期/帧），
        # 在只有两段 GPU 数据的对照里足以把结论带偏。
        Start-Sleep -Seconds 3
        Invoke-Method $Context.ResetStages $source @() | Out-Null

        $process = [Diagnostics.Process]::GetCurrentProcess()
        $samplesBegin = [int]$Context.ReadStats.GetValue($source).Item2
        $cpuBegin = $process.TotalProcessorTime.TotalMilliseconds
        $cyclesBegin = Read-ProcessCycles $process.Handle
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $Seconds
        $stopwatch.Stop()
        $cyclesEnd = Read-ProcessCycles $process.Handle
        $cpuEnd = $process.TotalProcessorTime.TotalMilliseconds
        $samplesEnd = [int]$Context.ReadStats.GetValue($source).Item2
        $stages = $Context.StageTimings.GetValue($source)

        $frames = $samplesEnd - $samplesBegin
        $run.Frames = $frames
        $run.Seconds = $stopwatch.Elapsed.TotalSeconds
        $run.ReadMs = [double]$stages.Item1
        $run.ConvertMs = [double]$stages.Item2
        $run.PublishMs = [double]$stages.Item3
        $run.TimedFrames = [long]$stages.Item4

        if ($frames -gt 0 -and $run.Seconds -gt 0) {
            $run.MeasuredFps = $frames / $run.Seconds
            $run.CpuMsPerFrame = ($cpuEnd - $cpuBegin) / $frames
            if ($cyclesEnd -gt $cyclesBegin) {
                $run.MegaCyclesPerFrame = ($cyclesEnd - $cyclesBegin) / $frames / 1e6
            }
        }
    }
    catch {
        $run.Failure = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $source) {
            try { Invoke-Method $Context.Stop $source @() | Out-Null } catch { }
            try { $source.Dispose() } catch { }
        }
    }

    return [pscustomobject]$run
}

function Write-CaptureRun {
    param($Run)

    if (-not [string]::IsNullOrWhiteSpace($Run.Failure)) {
        Add-Line "$($Run.Label)：不可用 —— $($Run.Failure)"
        return
    }

    Add-Line ("$($Run.Label)：gpuActive={0} 格式={1} {2}x{3}@{4} bt709={5}" -f `
        $Run.GpuActive, $Run.Format, $Run.Width, $Run.Height,
        (Format-Number $Run.NegotiatedFps "F0"), $Run.Bt709)
    if (-not $Run.GpuActive -and -not [string]::IsNullOrWhiteSpace($Run.GpuReason)) {
        Add-Line "$($Run.Label)：GPU 未生效原因=$($Run.GpuReason)"
    }
    if (-not [string]::IsNullOrWhiteSpace($Run.FeatureLevel)) {
        Add-Line "$($Run.Label)：D3D 功能级别=$($Run.FeatureLevel)"
    }

    Add-Line ("$($Run.Label)：fps={0} 每帧 CPU 周期={1} M（进程 CPU 时间 {2} ms/帧，{3} 帧 / {4} 秒）" -f `
        (Format-Number $Run.MeasuredFps "F1"), `
        (Format-Number $Run.MegaCyclesPerFrame "F3"), `
        (Format-Number $Run.CpuMsPerFrame "F3"), `
        $Run.Frames, (Format-Number $Run.Seconds "F1"))
    Add-Line ("$($Run.Label)：分段耗时 read={0} convert={1} publish={2} ms/帧（{3} 帧）" -f `
        (Format-Number $Run.ReadMs "F3"), `
        (Format-Number $Run.ConvertMs "F3"), `
        (Format-Number $Run.PublishMs "F3"), `
        $Run.TimedFrames)
}

function Get-MedianValue {
    param([double[]]$Values)

    $sorted = @($Values | Where-Object { $_ -gt 0 } | Sort-Object)
    if ($sorted.Count -eq 0) {
        return 0.0
    }
    if ($sorted.Count % 2 -eq 1) {
        return [double]$sorted[[int][Math]::Floor($sorted.Count / 2)]
    }

    $middle = $sorted.Count / 2
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Write-CaptureComparison {
    param($GpuRuns, $CpuRuns)

    $gpuOk = @($GpuRuns | Where-Object { $_.Frames -gt 0 })
    $cpuOk = @($CpuRuns | Where-Object { $_.Frames -gt 0 })

    Add-Line ""
    if ($gpuOk.Count -eq 0 -or $cpuOk.Count -eq 0) {
        Add-Line "对比：两种模式没有都拿到帧，无法比较"
        return
    }

    $gpuCycles = Get-MedianValue @($gpuOk | ForEach-Object { $_.MegaCyclesPerFrame })
    $cpuCycles = Get-MedianValue @($cpuOk | ForEach-Object { $_.MegaCyclesPerFrame })
    $gpuMs = Get-MedianValue @($gpuOk | ForEach-Object { $_.CpuMsPerFrame })
    $cpuMs = Get-MedianValue @($cpuOk | ForEach-Object { $_.CpuMsPerFrame })

    Add-Line "对比（各模式取中位数，GPU $($gpuOk.Count) 段 / CPU $($cpuOk.Count) 段）："
    Add-Line ("  每帧 CPU 周期：GPU {0} M vs CPU {1} M" -f `
        (Format-Number $gpuCycles "F3"), (Format-Number $cpuCycles "F3"))
    Add-Line ("  每帧进程 CPU 时间：GPU {0} ms vs CPU {1} ms" -f `
        (Format-Number $gpuMs "F3"), (Format-Number $cpuMs "F3"))
    if ($gpuCycles -gt 0) {
        Add-Line ("  CPU 路径的每帧 CPU 是 GPU 路径的 {0} 倍" -f `
            (Format-Number ($cpuCycles / $gpuCycles) "F2"))
    }
    if ($cpuCycles -gt 0 -and $gpuCycles -gt 0) {
        Add-Line ("  换成 GPU 之后每帧少用 {0}% 的 CPU" -f `
            (Format-Number ((1 - ($gpuCycles / $cpuCycles)) * 100) "F1"))
    }

    if (@($gpuOk | Where-Object { $_.GpuActive }).Count -eq 0) {
        Add-Line "注意：GPU 那几段并没有真的走 GPU，上面的差值只反映采集本身的波动，不能当成 GPU 收益"
    }

    return [pscustomobject]@{
        GpuCycles = $gpuCycles
        CpuCycles = $cpuCycles
        GpuMs = $gpuMs
        CpuMs = $cpuMs
        GpuWindows = $gpuOk.Count
        CpuWindows = $cpuOk.Count
        GpuActive = @($gpuOk | Where-Object { $_.GpuActive }).Count -gt 0
        Fps = Get-MedianValue @($gpuOk | ForEach-Object { $_.MeasuredFps })
        Format = [string]$gpuOk[0].Format
        Width = [int]$gpuOk[0].Width
        Height = [int]$gpuOk[0].Height
    }
}

function Invoke-GpuCapabilityCheck {
    param($Context, [int]$Width, [int]$Height)

    Add-Line ""
    Add-Line "GPU 转换能力自检（不碰摄像头，直接建 D3D11 设备、编译着色器）"
    $capable = $false
    $featureLevel = ""
    foreach ($mode in @(
        [pscustomobject]@{ Name = "YUY2"; IsNv12 = $false },
        [pscustomobject]@{ Name = "NV12"; IsNv12 = $true }
    )) {
        $converter = $null
        try {
            $converter = Invoke-Method $Context.CreateConverter $null `
                ([object[]]@($Width, $Height, $Width, $Height, $mode.IsNv12, $false))
            if ($null -eq $converter) {
                Add-Line ("  {0} {1}x{2}：不可用 —— {3}" -f `
                    $mode.Name, $Width, $Height, $Context.ConverterFailure.GetValue($null))
            }
            else {
                $capable = $true
                $featureLevel = [string]$Context.ConverterFeatureLevel.GetValue($converter)
                Add-Line ("  {0} {1}x{2}：可用，D3D 功能级别 {3}" -f `
                    $mode.Name, $Width, $Height, $featureLevel)
            }
        }
        catch {
            Add-Line ("  {0}：自检异常 —— {1}" -f $mode.Name, $_.Exception.Message)
        }
        finally {
            if ($null -ne $converter) {
                try { $converter.Dispose() } catch { }
            }
        }
    }

    return [pscustomobject]@{
        Capable = $capable
        FeatureLevel = $featureLevel
    }
}

function Invoke-CaptureProbe {
    param(
        $Context,
        $Device,
        [int]$Width,
        [int]$Height,
        [int]$TargetFps,
        [string]$ColorMatrix,
        [int]$Seconds,
        [int]$Rounds)

    $link = [string]$Context.DeviceLink.GetValue($Device)

    Add-Line "先按主程序的方式探测这台设备能否走新后端"
    $probeResult = Invoke-Method $Context.ProbeDevice $null `
        ([object[]]@($link, $Width, $Height, $TargetFps, $ColorMatrix, [TimeSpan]::FromSeconds(3)))
    $probeType = $probeResult.GetType()
    $usable = [bool]$probeType.GetProperty("Usable").GetValue($probeResult)
    Add-Line ("  探测结果：usable={0} 格式={1} {2}x{3}@{4} bt709={5} 失败原因={6}" -f `
        $usable, `
        $probeType.GetProperty("Format").GetValue($probeResult), `
        $probeType.GetProperty("Width").GetValue($probeResult), `
        $probeType.GetProperty("Height").GetValue($probeResult), `
        (Format-Number ([double]$probeType.GetProperty("Fps").GetValue($probeResult)) "F0"), `
        $probeType.GetProperty("UsesBt709").GetValue($probeResult), `
        $probeType.GetProperty("Failure").GetValue($probeResult))

    # 交替跑：GPU,CPU,CPU,GPU。温度、后台负载这类单调漂移会同时落在两种模式上，
    # 只是顺序跑"先 GPU 后 CPU"的话，两个数之间的差值很容易被漂移冒充成收益。
    $order = [System.Collections.Generic.List[bool]]::new()
    for ($round = 1; $round -le [Math]::Max(1, $Rounds); $round++) {
        $order.Add($true)
        $order.Add($false)
        $order.Add($false)
        $order.Add($true)
    }

    Add-Line ""
    Add-Line "同一台设备上交替跑 GPU 与 CPU 转换（目标 $Width x $Height @ $TargetFps，每段 $Seconds 秒，共 $($order.Count) 段）"
    Add-Line "说明：探针不订阅 FrameReady，所以 publish 段为 0；主程序真实路径每帧还要复制一份帧给订阅方，"
    Add-Line "      两种模式都要付这笔钱，不影响两者之间的差"

    $gpuRuns = [System.Collections.Generic.List[object]]::new()
    $cpuRuns = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $order.Count; $index++) {
        $useGpu = $order[$index]
        $modeName = if ($useGpu) { "GPU 转换" } else { "CPU 转换（对照）" }
        Add-Line ""
        Add-Line "第 $($index + 1)/$($order.Count) 段：$modeName"
        $run = Measure-CaptureRun $Context $link $Width $Height $TargetFps $ColorMatrix $useGpu $Seconds
        Write-CaptureRun $run
        if ($useGpu) {
            $gpuRuns.Add($run)
        }
        else {
            $cpuRuns.Add($run)
        }
        Save-Report
    }

    return Write-CaptureComparison $gpuRuns $cpuRuns
}

function New-OutputDirectory {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    return (Resolve-Path -LiteralPath $Path).Path
}

function Write-FrameCopyCost {
    param($Cost)

    if (-not $Cost.Available) {
        Add-Line "整帧拷贝实测跳过：$($Cost.Failure)"
        return
    }

    $frameMb = $Cost.FrameBytes / 1MB
    Add-Line ("在这台机器上用真实帧尺寸 {0}x{1}（{2} MB/帧）实测，" -f `
        $Cost.Width, $Cost.Height, (Format-Number $frameMb "F2"))
    Add-Line ("  每档跑 {0} 遍 × {1} 次，取每遍平均耗时的中位数：" -f $Cost.Rounds, $Cost.Iterations)
    Add-Line ("  Clone（分配 + 拷贝）：{0} ms/帧" -f (Format-Number $Cost.CloneMs "F3"))
    if ($Cost.CloneCycles -gt 0) {
        Add-Line ("    每帧 CPU 周期 {0} M" -f (Format-Number ($Cost.CloneCycles / 1e6) "F3"))
    }
    if ($Cost.CloneMaxMs -gt 0 -and $Cost.CloneMaxMs -gt $Cost.CloneMinMs) {
        Add-Line ("    各遍区间 {0}–{1} ms（区间越宽说明当时机器越吵，关掉别的程序再量一次更准）" -f `
            (Format-Number $Cost.CloneMinMs "F3"), (Format-Number $Cost.CloneMaxMs "F3"))
    }
    $pixelAllocMs = [Math]::Max(0.0, $Cost.CloneMs - $Cost.CopyMs)
    Add-Line ("    其中纯拷贝 {0} ms（{1} GB/s），剩下 ≈ {2} ms 是每帧重新申请像素缓冲区再触碰新页" -f `
        (Format-Number $Cost.CopyMs "F3"), `
        (Format-Number $Cost.GigaBytesPerSecond "F1"), `
        (Format-Number $pixelAllocMs "F3"))
    Add-Line ("  CopyTo（复用缓冲，只拷贝）：{0} ms/帧" -f (Format-Number $Cost.CopyMs "F3"))
    Add-Line ("  只建 Mat 头、不申请像素：{0} ms/帧（像素要等第一次写入才分配）" -f (Format-Number $Cost.AllocMs "F3"))
    Add-Line "  说明：整帧 Clone 的大头不是 memcpy，而是每帧新申请一块几 MB 内存再把新页碰一遍；"
    Add-Line "        复用缓冲的 CopyTo 才是纯拷贝那点钱。"

    $fps = if ($Cost.Fps -gt 0) { $Cost.Fps } else { 30 }
    Add-Line ""
    Add-Line "折算（处理循环旧版每帧克隆：不录制 1 次，录制并开水面 2 次；新版 0 次）："
    foreach ($case in @(
        [pscustomobject]@{ Name = "不录制（只看预览）"; Clones = 1 },
        [pscustomobject]@{ Name = "录制 + 水印       "; Clones = 2 }
    )) {
        $msPerSecond = $Cost.CloneMs * $case.Clones * $fps
        $mbPerSecond = $frameMb * $case.Clones * $fps
        Add-Line ("  {0} @{1}fps：每秒省 {2} ms（单核 {3}%），每秒少拷贝 {4} MB" -f `
            $case.Name, `
            $fps, `
            (Format-Number $msPerSecond "F1"), `
            (Format-Number ($msPerSecond / 10.0) "F1"), `
            (Format-Number $mbPerSecond "F0"))
    }
    Add-Line "  说明：上面是省下来的纯分配与拷贝开销。采集线程原来要等这次整帧拷贝做完才能发布下一帧，"
    Add-Line "        分辨率和帧率越高，延迟与抖动上的收益越明显。"

    Add-Line ""
    Add-Line "内存（顺带）：旧版除录像队列之外还常驻 1 份整帧（处理循环的克隆源），录制或发预览时"
    Add-Line ("  同一时刻最多活着 3 份；新版不常驻、最多 2 份，恒常少 1 份 ≈ {0} MB（{1}x{2}）" -f `
        (Format-Number $frameMb "F2"), $Cost.Width, $Cost.Height)
    Add-Line "  录像队列里待编码帧的份数和事件预录缓冲都没变，这两处不省内存。"
}

<#
整帧拷贝成本实测。

处理循环以前每轮都 _latestFrame.Clone()，开水面录制时水印还要再克隆一份：1080p 一帧 6MB，
60fps 就是 360MB/s，两处合计 720MB/s。这笔钱有多大取决于机器、分配器和内存带宽，
所以在现场机器上用真实帧尺寸直接量：Clone（分配 + 拷贝）、CopyTo（复用缓冲只拷贝）、
只分配不拷贝，各量若干次取中位数，再按每帧克隆次数和实际帧率折算成每秒开销。

这一段不碰摄像头、不需要退出主程序，只要程序目录里有 OpenCvSharp.dll 就能跑。
#>
function Measure-FrameCopyCost {
    param(
        [string]$ResolvedAppDir,
        [int]$Width,
        [int]$Height,
        [int]$Fps,
        [int]$Iterations = 200,
        [int]$Rounds = 3
    )

    $result = [ordered]@{
        Available = $false
        Failure = ""
        Width = $Width
        Height = $Height
        Fps = $Fps
        Iterations = $Iterations
        Rounds = [Math]::Max(1, $Rounds)
        FrameBytes = 0
        CloneMs = 0.0
        CloneMinMs = 0.0
        CloneMaxMs = 0.0
        CloneCycles = 0.0
        CopyMs = 0.0
        AllocMs = 0.0
        GigaBytesPerSecond = 0.0
    }

    if ($Width -le 0 -or $Height -le 0) {
        $result.Failure = "帧尺寸未知（配置里没有 FrameWidth/FrameHeight，也没用 -FrameWidth/-FrameHeight 指定），跳过整帧拷贝实测"
        return [pscustomobject]$result
    }

    if ([string]::IsNullOrWhiteSpace($ResolvedAppDir)) {
        $result.Failure = "找不到打包程序目录（缺 OpenCvSharp.dll），跳过整帧拷贝实测"
        return [pscustomobject]$result
    }

    $openCvPath = Join-Path $ResolvedAppDir "OpenCvSharp.dll"
    if (-not (Test-Path -LiteralPath $openCvPath)) {
        $result.Failure = "程序目录里没有 OpenCvSharp.dll，跳过整帧拷贝实测"
        return [pscustomobject]$result
    }

    $source = $null
    $destination = $null
    $spare = $null
    $warmup = $null
    try {
        # 与实测段一致：让 OpenCvSharpExtern.dll 能从程序目录加载
        $env:PATH = "$ResolvedAppDir;$script:OriginalPath"
        [Environment]::CurrentDirectory = $ResolvedAppDir

        $openCv = [Reflection.Assembly]::LoadFrom($openCvPath)
        $matType = $openCv.GetType("OpenCvSharp.Mat")
        $typeType = $openCv.GetType("OpenCvSharp.MatType")
        if ($null -eq $matType -or $null -eq $typeType) {
            $result.Failure = "OpenCvSharp.dll 里没有 Mat/MatType，跳过整帧拷贝实测"
            return [pscustomobject]$result
        }

        $colorType = $typeType.GetField(
            "CV_8UC3",
            [Reflection.BindingFlags]'Public,NonPublic,Static')
        if ($null -eq $colorType) {
            $result.Failure = "OpenCvSharp.MatType 里没有 CV_8UC3，跳过整帧拷贝实测"
            return [pscustomobject]$result
        }

        $typeValue = $colorType.GetValue($null)
        $ctor = $matType.GetConstructor(
            [Reflection.BindingFlags]'Public,NonPublic,Instance',
            $null,
            [Type[]]@([int], [int], $typeType),
            $null)
        if ($null -eq $ctor) {
            $result.Failure = "OpenCvSharp.Mat 的 (rows, cols, type) 构造函数与预期不符，跳过整帧拷贝实测"
            return [pscustomobject]$result
        }

        $frameBytes = [long]$Height * [long]$Width * 3
        $result.FrameBytes = $frameBytes

        $source = $ctor.Invoke([object[]]@($Height, $Width, $typeValue))
        $destination = $ctor.Invoke([object[]]@($Height, $Width, $typeValue))
        $spare = $null

        # 预热：首次分配要拿内存、首次拷贝要碰页，直接计时会虚高
        $warmup = $null
        for ($index = 0; $index -lt 5; $index++) {
            $warmup = $source.Clone()
            $warmup.Dispose()
        }
        $source.CopyTo($destination)

        $process = [Diagnostics.Process]::GetCurrentProcess()
        $cloneMs = 0.0
        $cloneCycles = 0.0
        $copyMs = 0.0
        $allocMs = 0.0

        # 每档跑若干遍再取中位数：单遍测量会被别的进程、GC、电源调度带偏
        $rounds = [Math]::Max(1, $Rounds)
        $cloneSamples = [System.Collections.Generic.List[double]]::new()
        $copySamples = [System.Collections.Generic.List[double]]::new()
        $allocSamples = [System.Collections.Generic.List[double]]::new()
        $cycleSamples = [System.Collections.Generic.List[double]]::new()
        for ($round = 0; $round -lt $rounds; $round++) {
            $cyclesBegin = Read-ProcessCycles $process.Handle
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            for ($index = 0; $index -lt $Iterations; $index++) {
                $spare = $source.Clone()
                $spare.Dispose()
            }
            $stopwatch.Stop()
            $cyclesEnd = Read-ProcessCycles $process.Handle
            $cloneSamples.Add($stopwatch.Elapsed.TotalMilliseconds / $Iterations)
            if ($cyclesEnd -gt $cyclesBegin) {
                $cycleSamples.Add(($cyclesEnd - $cyclesBegin) / [double]$Iterations)
            }

            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            for ($index = 0; $index -lt $Iterations; $index++) {
                $source.CopyTo($destination)
            }
            $stopwatch.Stop()
            $copySamples.Add($stopwatch.Elapsed.TotalMilliseconds / $Iterations)

            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            for ($index = 0; $index -lt $Iterations; $index++) {
                $spare = $ctor.Invoke([object[]]@($Height, $Width, $typeValue))
                $spare.Dispose()
            }
            $stopwatch.Stop()
            $allocSamples.Add($stopwatch.Elapsed.TotalMilliseconds / $Iterations)
        }

        $cloneMs = Get-MedianValue @($cloneSamples)
        $copyMs = Get-MedianValue @($copySamples)
        $allocMs = Get-MedianValue @($allocSamples)
        $cloneCycles = Get-MedianValue @($cycleSamples)
        $positiveCloneSamples = @($cloneSamples | Where-Object { $_ -gt 0 })
        if ($positiveCloneSamples.Count -gt 0) {
            $result.CloneMinMs = ($positiveCloneSamples | Measure-Object -Minimum).Minimum
            $result.CloneMaxMs = ($positiveCloneSamples | Measure-Object -Maximum).Maximum
        }

        $result.CloneMs = $cloneMs
        $result.CloneCycles = $cloneCycles
        $result.CopyMs = $copyMs
        $result.AllocMs = $allocMs
        if ($copyMs -gt 0) {
            $result.GigaBytesPerSecond = ($frameBytes / 1e9) / ($copyMs / 1000.0)
        }
        $result.Available = $true
    }
    catch {
        $result.Failure = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    }
    finally {
        foreach ($frame in @($source, $destination, $spare, $warmup)) {
            if ($null -ne $frame) {
                try { $frame.Dispose() } catch { }
            }
        }
    }

    return [pscustomobject]$result
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$resolvedOutputDir = New-OutputDirectory $OutputDir
$script:ReportPath = Join-Path $resolvedOutputDir "camera-pipeline-$timestamp.txt"
$script:CameraLogPath = Join-Path $resolvedOutputDir "camera-log-$timestamp.txt"
$script:ProbeDataDir = Join-Path $resolvedOutputDir "probe-data"

Add-Line "摄像头采集链路现场诊断"
Add-Line "报告时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
Add-Line "报告文件：$script:ReportPath"
Save-Report

try {
    Add-Section "1. 机器与显卡"

    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
    $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
    $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1

    Add-Line "系统：$($os.Caption) $($os.Version) build $($os.BuildNumber) $($os.OSArchitecture)"
    Add-Line "处理器：$($processor.Name)，$($processor.NumberOfCores) 核 / $($processor.NumberOfLogicalProcessors) 线程"
    if ($null -ne $computer) {
        Add-Line ("内存：{0} GB" -f (Format-Number ($computer.TotalPhysicalMemory / 1GB) "F1"))
    }
    Add-Line "PowerShell：$($PSVersionTable.PSVersion)，Apartment=$([Threading.Thread]::CurrentThread.GetApartmentState())"

    foreach ($gpu in (Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue)) {
        Add-Line ("显卡：{0}｜驱动 {1}｜驱动日期 {2}｜状态 {3}" -f `
            $gpu.Name, $gpu.DriverVersion, $gpu.DriverDate, $gpu.Status)
    }

    Save-Report

    Add-Section "2. 摄像头设备"

    $pnpDevices = @(Get-PnpCameraDevices)
    Add-Line "系统识别到的摄像头设备（PnP）：$($pnpDevices.Count) 台"
    foreach ($device in $pnpDevices) {
        $virtual = Test-VirtualCameraDevice $device.Name $device.DeviceId
        Add-Line ("  {0}｜{1}｜{2}｜DeviceID={3}" -f `
            $device.Name, $device.Class, $device.Status, $device.DeviceId)
        if ($virtual) {
            Add-Line "    提示：这台是虚拟摄像头"
        }
    }

    $appDirectory = Resolve-AppDirectory $AppDir
    if ([string]::IsNullOrWhiteSpace($appDirectory)) {
        Add-Line ""
        Add-Line "找不到打包程序目录（缺 ExpressPackingMonitoring.dll），Media Foundation 设备清单与实测都会跳过"
        Add-Line "把本脚本放到打包根目录（与 app 目录同级）或加 -AppDir 指定路径"
    }
    else {
        Add-Line ""
        Add-Line "程序目录：$appDirectory"
        $versionInfo = (Get-Item -LiteralPath (Join-Path $appDirectory "ExpressPackingMonitoring.dll")).VersionInfo
        Add-Line "程序版本：$($versionInfo.ProductVersion)｜文件版本：$($versionInfo.FileVersion)"
    }

    $directShow = Get-DirectShowDeviceList $appDirectory
    Add-Line ""
    if ([string]::IsNullOrWhiteSpace($directShow.FfmpegPath)) {
        Add-Line "DirectShow 采集设备清单：跳过（程序目录里没有 tools\ffmpeg.exe，正式包里才有）"
    }
    else {
        Add-Line "DirectShow 采集设备清单（旧后端能看到的名字，由包内 ffmpeg 枚举）：$($directShow.Names.Count) 台"
        foreach ($name in $directShow.Names) {
            Add-Line "  $name"
        }
    }

    Save-Report

    Add-Section "3. 现场配置"

    $configPath = Join-Path $DataDir "config.json"
    Add-Line "用户数据目录：$DataDir"
    Add-Line "配置文件：$configPath｜存在：$(Test-Path -LiteralPath $configPath)"
    $config = Read-ConfigDocument $configPath
    if ($null -ne $config) {
        foreach ($key in @(
            "CameraBackend", "CameraSourceKind", "CameraMonikerString", "CameraIndex",
            "FrameWidth", "FrameHeight", "Fps", "CameraColorMatrix", "CameraRotate180",
            "Gpu", "VideoCodec"
        )) {
            Add-Line ("  {0} = {1}" -f $key, (Read-ConfigValue $config $key))
        }
    }

    $running = @(Get-Process -Name "ExpressPackingMonitoring" -ErrorAction SilentlyContinue)
    Add-Line ""
    if ($running.Count -gt 0) {
        Add-Line "打包程序正在运行（PID $($running.Id -join ', ')）"
        Add-Line "实测阶段需要独占摄像头，请先退出主程序后再跑一次；本次只收集环境与日志"
    }
    else {
        Add-Line "打包程序未运行，可以进行实测"
    }

    Save-Report

    Add-Section "4. runtime.log 里的采集结论"

    $logDir = Join-Path $DataDir "log"
    $cameraLines = @(Get-CameraLogLines $logDir)
    Add-Line "匹配到的采集相关日志：$($cameraLines.Count) 行（$logDir）"
    if ($cameraLines.Count -gt 0) {
        $mfStarts = @($cameraLines | Where-Object { $_.Line.Contains("StartCamera success（Media Foundation）") })
        $oldStarts = @($cameraLines | Where-Object {
            $_.Line.Contains("StartCamera success") -and -not $_.Line.Contains("Media Foundation")
        })
        $mfUnavailable = @($cameraLines | Where-Object {
            $_.Line -match "Media Foundation 后端不可用|Media Foundation 协商成功但没有出帧|Media Foundation 里找不到对应设备|Media Foundation 无法枚举|Media Foundation 采集启动失败"
        })
        $gpuEnabled = @($cameraLines | Where-Object { $_.Line.Contains("GPU 转换已启用") })
        $gpuDisabled = @($cameraLines | Where-Object { $_.Line -match "GPU 转换不可用|回退到 CPU 转换" })

        Add-Line ""
        Add-Line "统计（runtime.old.log 与 runtime.log 的全部匹配行）："
        Add-Line "  新后端启动成功：$($mfStarts.Count) 次"
        Add-Line "  旧后端启动成功：$($oldStarts.Count) 次"
        Add-Line "  新后端不可用或回退旧后端：$($mfUnavailable.Count) 次"
        Add-Line "  GPU 转换启用：$($gpuEnabled.Count) 次"
        Add-Line "  GPU 不可用或中途回退 CPU：$($gpuDisabled.Count) 次"

        $tail = $cameraLines | Select-Object -Last 40
        Add-Line ""
        Add-Line "最近 40 行（时间顺序，runtime.old.log 在前）："
        foreach ($entry in $tail) {
            Add-Line "  [$($entry.File)] $($entry.Line)"
        }

        $logText = [System.Collections.Generic.List[string]]::new()
        $logText.Add("摄像头采集相关日志，来自 $logDir")
        $logText.Add("生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
        foreach ($entry in $cameraLines) {
            $logText.Add("[$($entry.File)] $($entry.Line)")
        }
        [IO.File]::WriteAllLines($script:CameraLogPath, $logText, [Text.UTF8Encoding]::new($true))
        Add-Line ""
        Add-Line "完整匹配日志：$script:CameraLogPath"
    }

    Save-Report

    Add-Section "5. 本机实测"

    $shouldMeasure = -not $SkipMeasure -and $running.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($appDirectory)
    if ($shouldMeasure) {
        # 现场装的可能是还没有新采集后端的旧版本，先确认类型在不在，别把后面的结论段整段带走
        try {
            $script:CaptureAssembly = [Reflection.Assembly]::LoadFrom(
                (Join-Path $appDirectory "ExpressPackingMonitoring.dll"))
            if ($null -eq $script:CaptureAssembly.GetType(
                    "ExpressPackingMonitoring.Services.MediaFoundation.MfCameraSource")) {
                Add-Line "当前安装的程序集里没有新采集后端（MfCameraSource），跳过实测"
                Add-Line "  现场装的是较旧的版本，第 1–4 节的环境、设备与日志结论不受影响"
                $shouldMeasure = $false
            }
        }
        catch {
            Add-Line "加载程序集失败，跳过实测：$($_.Exception.Message)"
            $shouldMeasure = $false
        }
    }

    if (-not $shouldMeasure) {
        Add-Line "跳过实测（-SkipMeasure、程序正在运行、找不到程序目录，或当前版本没有新采集后端）"
    }
    else {
        # 探针自己的日志单独落盘，不写现场的 runtime.log
        $env:EPM_USER_DATA_DIR = $script:ProbeDataDir
        $context = New-CaptureContext $appDirectory

        $measureWidth = if ($FrameWidth -gt 0) { $FrameWidth } else { [int](Read-ConfigValue $config "FrameWidth") }
        $measureHeight = if ($FrameHeight -gt 0) { $FrameHeight } else { [int](Read-ConfigValue $config "FrameHeight") }
        $measureFps = if ($Fps -gt 0) { $Fps } else { [int](Read-ConfigValue $config "Fps") }
        $colorMatrix = Read-ConfigValue $config "CameraColorMatrix"
        if ($measureWidth -le 0) { $measureWidth = 1280 }
        if ($measureHeight -le 0) { $measureHeight = 720 }
        if ($measureFps -le 0) { $measureFps = 15 }
        if ([string]::IsNullOrWhiteSpace($colorMatrix)) { $colorMatrix = "auto" }

        $null = Enable-CycleCounter
        Add-Line "CPU 周期计数：$(if ($script:CycleCounterReady) { '可用' } else { '不可用，只能用进程 CPU 时间' })"
        Add-Line "Media Foundation 可用性：$([bool]($null -ne (Invoke-Method $Context.StartPlatform $null @())))"

        $script:GpuCapability = Invoke-GpuCapabilityCheck $context $measureWidth $measureHeight

        Add-Line ""
        Add-Line "Media Foundation 设备清单："
        $mfDevices = @(Invoke-Method $Context.EnumerateDevices $null @())
        foreach ($device in $mfDevices) {
            $formats = @(Invoke-Method $Context.ReadFormats $null ([object[]]@($device)))
            $names = @($formats | ForEach-Object { $Context.FormatSubtype.GetValue($_) } | Select-Object -Unique)
            Add-Line ("  {0}｜格式 {1} 种（{2}）" -f `
                $Context.DeviceName.GetValue($device), $formats.Count, ($names -join "/"))
            Add-Line "    $($Context.DeviceLink.GetValue($device))"
            Add-Line ("    原生格式：{0}" -f ((
                $formats | Select-Object -First 12 | ForEach-Object { Format-NativeFormat $Context $_ }
            ) -join "，"))
        }

        # 现场正在用的那台：按设备实例键和主程序一样匹配（去掉接口类 GUID 再比）
        $configuredMoniker = Read-ConfigValue $config "CameraMonikerString"
        $configuredDevice = $null
        if (-not [string]::IsNullOrWhiteSpace($configuredMoniker)) {
            if (Test-SoftwareOnlyCamera $configuredMoniker) {
                $script:ConfiguredCameraInMf = $false
                Add-Line ""
                Add-Line "现场配置的是 DirectShow 软件设备（OBS 虚拟摄像头这类）：Media Foundation 里本来就不存在这种设备，"
                Add-Line "  主程序会走旧后端，新后端与 GPU 转换在这台设备上不会生效"
                Add-Line "  moniker=$configuredMoniker"
            }
            else {
                $configuredKey = Get-DeviceInstanceKey $configuredMoniker
                $configuredDevice = @($mfDevices | Where-Object {
                    (Get-DeviceInstanceKey ([string]$Context.DeviceLink.GetValue($_))) -eq $configuredKey
                }) | Select-Object -First 1
                $script:ConfiguredCameraInMf = $null -ne $configuredDevice

                if ($null -eq $configuredDevice) {
                    Add-Line ""
                    Add-Line "注意：现场配置的摄像头在 Media Foundation 清单里找不到（按设备实例键匹配）"
                    Add-Line "  moniker=$configuredMoniker"
                    Add-Line "  key=$configuredKey"
                    Add-Line "  主程序遇到这种情况会回退旧后端，新后端与 GPU 转换都不会生效"
                }
            }
        }

        $targetDevice = $null
        if (-not [string]::IsNullOrWhiteSpace($DeviceName)) {
            $targetDevice = @($mfDevices | Where-Object {
                ([string]$Context.DeviceName.GetValue($_)) -like "*$DeviceName*"
            }) | Select-Object -First 1
        }

        if ($null -eq $targetDevice -and $null -ne $configuredDevice) {
            $targetDevice = $configuredDevice
        }

        if ($null -eq $targetDevice -and $mfDevices.Count -gt 0) {
            $targetDevice = $mfDevices[0]
        }

        Add-Line ""
        if ($null -eq $targetDevice) {
            Add-Line "Media Foundation 看不到任何采集设备，无法实测"
        }
        else {
            $script:TargetDeviceName = [string]$Context.DeviceName.GetValue($targetDevice)
            Add-Line "实测设备：$script:TargetDeviceName"
            if ($null -eq $configuredDevice) {
                Add-Line "  注意：这不是现场配置的那台摄像头（配置的那台不在 Media Foundation 清单里），数字只代表这台"
            }
            Save-Report
            try {
                $script:MeasureSummary = Invoke-CaptureProbe `
                    $context $targetDevice $measureWidth $measureHeight $measureFps $colorMatrix $MeasureSeconds $Rounds
            }
            catch {
                Add-Line ""
                Add-Line "实测中断：$($_.Exception.GetType().Name): $($_.Exception.Message)"
                Add-Line "  摄像头可能被别的程序占用，或设备在实测中途被拔掉；环境与日志结论仍然有效"
            }
        }

        # 探针把用户数据目录重定向到了输出目录，日志落在那里，现场 runtime.log 不被写入
        $probeLogPath = Join-Path $script:ProbeDataDir "ExpressPackingMonitoring\log\runtime.log"
        if (Test-Path -LiteralPath $probeLogPath) {
            Add-Line ""
            Add-Line "探针自己产生的采集日志（$probeLogPath）："
            foreach ($line in ([IO.File]::ReadLines($probeLogPath, [Text.Encoding]::UTF8) | Select-Object -Last 24)) {
                Add-Line "  $line"
            }
        }
    }

    Add-Section "6. 整帧拷贝成本（处理循环旧版每帧克隆的那两次）"

    $copyWidth = if ($FrameWidth -gt 0) { $FrameWidth } else { [int](Read-ConfigValue $config "FrameWidth") }
    $copyHeight = if ($FrameHeight -gt 0) { $FrameHeight } else { [int](Read-ConfigValue $config "FrameHeight") }
    $copyFps = if ($Fps -gt 0) { $Fps } else { [int](Read-ConfigValue $config "Fps") }
    if ($copyWidth -le 0) { $copyWidth = 1920 }
    if ($copyHeight -le 0) { $copyHeight = 1080 }
    if ($copyFps -le 0) { $copyFps = 30 }

    # 这一段不碰摄像头：主程序正在运行时也能量，量的是这台机器的分配与拷贝能力
    $script:FrameCopyCost = Measure-FrameCopyCost `
        -ResolvedAppDir $appDirectory `
        -Width $copyWidth `
        -Height $copyHeight `
        -Fps $copyFps
    Write-FrameCopyCost $script:FrameCopyCost
    Save-Report

    Add-Section "7. 结论速览"

    $backendLine = @($cameraLines | Where-Object { $_.Line.Contains("StartCamera success") }) | Select-Object -Last 1
    if ($null -ne $backendLine) {
        $backendName = if ($backendLine.Line.Contains("Media Foundation")) {
            "新后端（Media Foundation）"
        }
        else {
            "旧后端（DirectShow/AForge）"
        }
        Add-Line "现场日志里最近一次采集走的是：$backendName"
        Add-Line "  $($backendLine.Line)"
    }
    else {
        Add-Line "现场日志里没有 StartCamera success 记录，判断不了当前实际走哪条后端"
    }

    $gpuLogLine = @($cameraLines | Where-Object {
        $_.Line -match "GPU 转换已启用|GPU 转换不可用|回退到 CPU 转换"
    }) | Select-Object -Last 1
    if ($null -ne $gpuLogLine) {
        Add-Line "现场日志里最近一次 GPU 转换状态："
        Add-Line "  $($gpuLogLine.Line)"
    }

    if ($null -ne $script:GpuCapability) {
        $capabilityText = if ($script:GpuCapability.Capable) {
            "可用（$($script:GpuCapability.FeatureLevel)）"
        }
        else {
            "不可用，只能走 CPU 转换"
        }
        Add-Line "这台机器的 D3D11 GPU 转换能力：$capabilityText"
    }

    if ($script:ConfiguredCameraInMf -eq $false) {
        Add-Line "现场配置的摄像头配不上 Media Foundation（DirectShow 软件设备，或按设备实例键找不到）："
        Add-Line "  主程序在这台设备上会走旧后端，新后端与 GPU 转换用不上"
    }

    if ($null -ne $script:FrameCopyCost -and $script:FrameCopyCost.Available) {
        $copyFps = if ($script:FrameCopyCost.Fps -gt 0) { $script:FrameCopyCost.Fps } else { 30 }
        $idleMsPerSecond = $script:FrameCopyCost.CloneMs * $copyFps
        Add-Line ("整帧拷贝：这台机器 @{0}fps 每去除一次整帧克隆，每秒省 {1} ms CPU（单核 {2}%）；" -f `
            $copyFps, `
            (Format-Number $idleMsPerSecond "F1"), `
            (Format-Number ($idleMsPerSecond / 10.0) "F1"))
        Add-Line ("  处理循环以前每帧克隆一次（录制并开水面时两次），现在一次都不克隆")
    }

    if ($null -ne $script:MeasureSummary) {
        $summary = $script:MeasureSummary
        if ($summary.GpuActive -and $summary.GpuCycles -gt 0) {
            Add-Line ("实测对比（$script:TargetDeviceName，$($summary.Width)x$($summary.Height) $($summary.Format)）：CPU 路径每帧 CPU 是 GPU 路径的 {0} 倍" -f `
                (Format-Number ($summary.CpuCycles / $summary.GpuCycles) "F2"))
        }
        else {
            Add-Line "实测里 GPU 转换没有生效，这台设备上量不出 GPU 收益（原因见第 5 节）"
        }
    }
    else {
        Add-Line "本次没有跑实测（程序在运行、找不到程序目录，或指定了 -SkipMeasure）"
    }

    if (@($pnpDevices | Where-Object { Test-VirtualCameraDevice $_.Name $_.DeviceId }).Count -gt 0) {
        Add-Line ""
        Add-Line "提示：这台机器上有虚拟摄像头。虚拟摄像头的帧由软件生成，像素格式与帧率都由它自己决定，"
        Add-Line "      拿不到 YUY2/NV12 时新后端与 GPU 转换都不会生效，这种机器上的数字不能代表真实摄像头现场"
    }

    Save-Report
}
catch {
    Add-Line ""
    Add-Line "诊断中断：$($_.Exception.GetType().Name): $($_.Exception.Message)"
    Add-Line $_.ScriptStackTrace
}
finally {
    $env:PATH = $script:OriginalPath
    [Environment]::CurrentDirectory = $script:OriginalCurrentDirectory
    Save-Report
    Write-Host ""
    Write-Host "报告：$script:ReportPath"
    Write-Host "采集日志：$script:CameraLogPath"
}
