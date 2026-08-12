param(
    [string]$Share = "\\127.0.0.1\NASSim",
    [string]$AssemblyPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\ExpressPackingMonitoring\bin\Debug\net8.0-windows\win-x64\ExpressPackingMonitoring.dll"),
        (Join-Path $PSScriptRoot "..\TestResults\ReleaseBuild\Release\bin\ExpressPackingMonitoring\release_win-x64\ExpressPackingMonitoring.dll")
    )
    $AssemblyPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($AssemblyPath) -or -not (Test-Path -LiteralPath $AssemblyPath)) {
    throw "未找到 ExpressPackingMonitoring.dll，请先 dotnet build"
}

$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
$storageType = $assembly.GetType("ExpressPackingMonitoring.Config.StorageVolumeInfo", $true)
$classify = $storageType.GetMethods([System.Reflection.BindingFlags] "Public,Static") |
    Where-Object { $_.Name -eq "ClassifyStorageLocation" -and $_.GetParameters().Length -eq 1 } |
    Select-Object -First 1
if ($null -eq $classify) {
    throw "找不到 ClassifyStorageLocation(string)"
}

function Classify([string]$Path) {
    return [string]$classify.Invoke($null, @($Path))
}

function Find-FreeDrive {
    $usedLetters = @(
        net use 2>$null |
            ForEach-Object {
                if ($_ -match '^\s*\S+\s+([A-Z]):\s') { $Matches[1] }
            }
    )
    for ($letter = [char]'Y'; $letter -ge [char]'A'; $letter--) {
        $drive = "$letter`:"
        if ($usedLetters -notcontains "$letter") { return $drive }
    }
    throw "没有空闲盘符"
}

$drive = Find-FreeDrive
$target = "$drive\epm-mount-lifecycle"
$created = $false
try {
    Write-Host "映射 $drive -> $Share"
    net use $drive $Share | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "net use 映射失败（退出码 $LASTEXITCODE）" }
    $created = $true

    $networkKind = Classify $target
    Write-Host "挂载正常分类=$networkKind"
    if ($networkKind -ne "Network") { throw "期望 Network，实际 $networkKind" }

    Write-Host "断开 $drive"
    net use $drive /delete | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "net use 断开失败（退出码 $LASTEXITCODE）" }
    $created = $false

    $disconnectedKind = Classify $target
    Write-Host "断开后分类=$disconnectedKind"
    if ($disconnectedKind -ne "Unknown") {
        throw "期望 Unknown（fail-closed），实际 $disconnectedKind"
    }

    Write-Host "恢复 $drive -> $Share"
    net use $drive $Share | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "net use 恢复失败（退出码 $LASTEXITCODE）" }
    $created = $true

    $restoredKind = Classify $target
    Write-Host "恢复后分类=$restoredKind"
    if ($restoredKind -ne "Network") { throw "期望 Network，实际 $restoredKind" }

    Write-Host "MOUNT_LIFECYCLE_PASS share=$Share drive=$drive"
}
finally {
    if ($created) {
        try { net use $drive /delete | Out-Null } catch { }
    }
}
