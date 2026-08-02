param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$UpdateCheckUrl = "",
    [string]$ManifestPath = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "LauncherBaseline.Common.ps1")

function Get-DotEnvValue {
    param([string]$Key)

    $dotEnvPath = Join-Path $repoRoot ".env"
    if (-not (Test-Path -LiteralPath $dotEnvPath -PathType Leaf)) {
        return ""
    }

    foreach ($line in Get-Content -Encoding UTF8 -LiteralPath $dotEnvPath) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
            continue
        }
        $separatorIndex = $trimmed.IndexOf("=")
        if ($separatorIndex -le 0 -or
            -not [string]::Equals($trimmed.Substring(0, $separatorIndex).Trim(), $Key, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $value = $trimmed.Substring($separatorIndex + 1).Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        return $value
    }

    return ""
}

function Compress-LauncherPackage {
    param([string]$SourceDir, [string]$DestinationZip)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $DestinationZip) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDir,
        $DestinationZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false,
        [System.Text.Encoding]::UTF8)
}

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}
if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Launcher baseline version must use X.Y.Z format: $Version"
}

if ([string]::IsNullOrWhiteSpace($UpdateCheckUrl)) {
    $UpdateCheckUrl = [Environment]::GetEnvironmentVariable("UPDATE_CHECK_URL")
}
if ([string]::IsNullOrWhiteSpace($UpdateCheckUrl)) {
    $UpdateCheckUrl = Get-DotEnvValue -Key "UPDATE_CHECK_URL"
}
if ([string]::IsNullOrWhiteSpace($UpdateCheckUrl)) {
    $UpdateCheckUrl = "https://gitee.com/api/v5/repos/chenjjian/ExpressPackingMonitoring/releases/latest"
}
$UpdateCheckUrl = $UpdateCheckUrl.Trim()

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $PSScriptRoot "launcher-baseline.json"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "package\launcher-baselines\v$normalizedVersion"
}
$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$cacheRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "package\launcher-baselines"))
if (-not $outputFullPath.StartsWith(($cacheRoot.TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Launcher baseline OutputDir must be inside package\launcher-baselines"
}

$fingerprint = Get-LauncherLogicalFingerprint `
    -RepositoryRoot $repoRoot `
    -Runtime $Runtime `
    -UpdateCheckUrl $UpdateCheckUrl
$launcherProject = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"
$baseOutput = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\bin_publish_tmp\launcher-baseline\"
$baseIntermediate = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\obj_publish_tmp\launcher-baseline\"
$publishDir = Join-Path $outputFullPath "publish"
$workDir = Join-Path $outputFullPath "package-work"
$launcherPath = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
$packageName = "PackingProof_LauncherPatch_v$normalizedVersion.zip"
$packagePath = Join-Path $outputFullPath $packageName

if (Test-Path -LiteralPath $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir,$workDir | Out-Null

& dotnet publish $launcherProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    "-p:BaseOutputPath=$baseOutput" `
    "-p:BaseIntermediateOutputPath=$baseIntermediate" `
    "-p:LauncherDefaultUpdateCheckUrl=$UpdateCheckUrl" `
    "-p:PublishDir=$publishDir\"
if ($LASTEXITCODE -ne 0) {
    throw "Launcher baseline publish failed with exit code $LASTEXITCODE"
}

$publishedLauncher = Join-Path $publishDir "ExpressPackingMonitoring.exe"
if (-not (Test-Path -LiteralPath $publishedLauncher -PathType Leaf)) {
    $publishedLauncher = Get-ChildItem -LiteralPath $baseOutput -Recurse -Filter "ExpressPackingMonitoring.exe" |
        Where-Object { $_.FullName -like "*\native\*" } |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($publishedLauncher) -or
    -not (Test-Path -LiteralPath $publishedLauncher -PathType Leaf)) {
    throw "Launcher baseline publish did not produce ExpressPackingMonitoring.exe"
}
Copy-Item -LiteralPath $publishedLauncher -Destination $launcherPath -Force

$launcherSize = (Get-Item -LiteralPath $launcherPath).Length
$launcherHash = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash.ToLowerInvariant()
Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $workDir "ExpressPackingMonitoring.exe") -Force
Copy-NormalizedCommandFile `
    -SourcePath (Join-Path $PSScriptRoot "Install-LauncherPatch.cmd") `
    -DestinationPath (Join-Path $workDir "双击更新启动器.cmd")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Apply-LauncherPatch.ps1") -Destination (Join-Path $workDir "apply_launcher_patch.ps1") -Force

[ordered]@{
    type = "launcher_patch"
    version = $normalizedVersion
    file = "ExpressPackingMonitoring.exe"
    size = $launcherSize
    sha256 = $launcherHash
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workDir "launcher_patch_manifest.json") -Encoding UTF8

@(
    "PackingProof 启动器更新",
    "",
    "正常情况下无需手动下载，主程序会自动校验并更新启动器。",
    "如需手动更新，请完整解压本 ZIP，再双击《双击更新启动器.cmd》。",
    "",
    "此脚本只替换软件根目录启动器，不会修改主程序、录像、配置或数据库。",
    "不要直接在压缩软件中运行，也不要单独移动包内文件。"
) | Set-Content -LiteralPath (Join-Path $workDir "启动器更新说明.txt") -Encoding UTF8

Compress-LauncherPackage -SourceDir $workDir -DestinationZip $packagePath
$packageSize = (Get-Item -LiteralPath $packagePath).Length
$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$baseline = [ordered]@{
    schema_version = $script:LauncherBaselineSchemaVersion
    protocol_version = $script:LauncherUpdateProtocolVersion
    version = $normalizedVersion
    tag = "launcher-v$normalizedVersion"
    release_tag = "v$normalizedVersion"
    runtime = $Runtime
    update_check_url = $UpdateCheckUrl
    source_fingerprint = $fingerprint
    fingerprint_files = @(Get-LauncherFingerprintFiles | ForEach-Object { $_.Replace('\', '/') })
    package = [ordered]@{
        file = $packageName
        size = $packageSize
        sha256 = $packageHash
        executable_size = $launcherSize
        executable_sha256 = $launcherHash
    }
}
$baseline | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestFullPath -Encoding UTF8
$loadedBaseline = Read-LauncherBaselineManifest -ManifestPath $manifestFullPath
Assert-LauncherPackage -PackagePath $packagePath -Baseline $loadedBaseline
Assert-LauncherFile -Path $launcherPath -ExpectedSize $launcherSize -ExpectedSha256 $launcherHash -Description "Launcher executable"

Remove-Item -LiteralPath $publishDir -Recurse -Force
Remove-Item -LiteralPath $workDir -Recurse -Force
Write-Host "Launcher baseline created: launcher-v$normalizedVersion"
Write-Host "Manifest: $manifestFullPath"
Write-Host "Package:  $packagePath"
Write-Host "SHA256:   $packageHash"
Write-Host "Create the Git component tag only after committing the manifest: launcher-v$normalizedVersion"
