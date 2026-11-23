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
. (Join-Path $PSScriptRoot "Branding.Common.ps1")
$brand = Get-BrandProfile -RepoRoot $repoRoot

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
    $profileKey = "BRAND_" + (($brand.ProfileId -replace "[^A-Za-z0-9]", "_").ToUpperInvariant()) + "_UPDATE_CHECK_URL"
    $UpdateCheckUrl = [Environment]::GetEnvironmentVariable($profileKey)
}
if ([string]::IsNullOrWhiteSpace($UpdateCheckUrl) -and $brand.ProfileId -eq "packingproof") {
    $UpdateCheckUrl = [Environment]::GetEnvironmentVariable("UPDATE_CHECK_URL")
}
if ([string]::IsNullOrWhiteSpace($UpdateCheckUrl) -and $brand.ProfileId -eq "packingproof") {
    $UpdateCheckUrl = Get-DotEnvValue -Key "UPDATE_CHECK_URL"
}
if ([string]::IsNullOrWhiteSpace($UpdateCheckUrl)) {
    $UpdateCheckUrl = $brand.UpdateGiteeCheckUrl
}
$UpdateCheckUrl = $UpdateCheckUrl.Trim()

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = $brand.LauncherBaselinePath
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = if ($brand.ProfileId -eq "packingproof") {
        Join-Path $repoRoot "package\launcher-baselines\v$normalizedVersion"
    }
    else {
        Join-Path $repoRoot ("package\launcher-baselines\" + $brand.ArtifactPrefix + "\v$normalizedVersion")
    }
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
    -UpdateCheckUrl $UpdateCheckUrl `
    -BrandProfileId $brand.ProfileId `
    -BrandProfileContentSha256 $brand.ContentSha256
$launcherProject = Join-Path $repoRoot "src\ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"
$buildArtifacts = Join-Path $outputFullPath "build-artifacts"
$publishDir = Join-Path $outputFullPath "publish"
$workDir = Join-Path $outputFullPath "package-work"
$launcherPath = Join-Path $outputFullPath "ExpressPackingMonitoring.exe"
$packageName = "$($brand.ArtifactPrefix)_LauncherPatch_v$normalizedVersion.zip"
$packagePath = Join-Path $outputFullPath $packageName
$baselineTag = Get-LauncherBaselineTag -Version $normalizedVersion -ArtifactPrefix $brand.ArtifactPrefix

if (Test-Path -LiteralPath $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir,$workDir | Out-Null

& dotnet publish $launcherProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --artifacts-path $buildArtifacts `
    -o $publishDir `
    "-p:LauncherDefaultUpdateCheckUrl=$UpdateCheckUrl" `
    "-p:PublishAot=true"
if ($LASTEXITCODE -ne 0) {
    throw "Launcher baseline publish failed with exit code $LASTEXITCODE"
}

$publishedLauncher = Join-Path $publishDir "ExpressPackingMonitoring.exe"
if (-not (Test-Path -LiteralPath $publishedLauncher -PathType Leaf)) {
    $publishedLauncher = Get-ChildItem -LiteralPath $buildArtifacts -Recurse -Filter "ExpressPackingMonitoring.exe" |
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
    -DestinationPath (Join-Path $workDir "双击更新启动器.cmd") `
    -ArtifactPrefix $brand.ArtifactPrefix
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Apply-LauncherPatch.ps1") -Destination (Join-Path $workDir "apply_launcher_patch.ps1") -Force

[ordered]@{
    type = "launcher_patch"
    version = $normalizedVersion
    file = "ExpressPackingMonitoring.exe"
    size = $launcherSize
    sha256 = $launcherHash
    brand = [ordered]@{
        profile_id = $brand.ProfileId
        product_name = $brand.ProductName
        display_name = $brand.DisplayName
        artifact_prefix = $brand.ArtifactPrefix
        compatibility_id = $brand.CompatibilityId
        data_directory_name = $brand.DataDirectoryName
        instance_name_prefix = $brand.InstanceNamePrefix
        package_edition_marker = $brand.PackageEditionMarker
    }
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $workDir "launcher_patch_manifest.json") -Encoding UTF8

@(
    "$($brand.DisplayName) 启动器更新",
    "",
    "正常情况下无需手动下载，主程序会自动校验并更新启动器。",
    "如需手动更新，请完整解压本 ZIP，再双击《双击更新启动器.cmd》。",
    "",
    "本补丁只适用于 Setup 版（exe 安装版）。",
    "Microsoft Store 版没有根启动器，请直接使用 Microsoft Store 更新。",
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
    tag = $baselineTag
    artifact_prefix = $brand.ArtifactPrefix
    release_tag = "v$normalizedVersion"
    runtime = $Runtime
    update_check_url = $UpdateCheckUrl
    source_fingerprint = $fingerprint
    brand_profile_sha256 = $brand.ContentSha256
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
if (Test-Path -LiteralPath $buildArtifacts) {
    Remove-Item -LiteralPath $buildArtifacts -Recurse -Force
}
Write-Host "Launcher baseline created: $baselineTag"
Write-Host "Manifest: $manifestFullPath"
Write-Host "Package:  $packagePath"
Write-Host "SHA256:   $packageHash"
Write-Host "Create the Git component tag only after committing the manifest: $baselineTag"
