param(
    [Parameter(Mandatory = $true)][string]$FromVersion,
    [Parameter(Mandatory = $true)][string]$ToVersion,
    [Parameter(Mandatory = $true)][string]$BaselineAppDir,
    [Parameter(Mandatory = $true)][string]$TargetAppDir,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [Parameter(Mandatory = $true)][string]$CetPolicyAssemblyPath,
    [string]$UpdateManifestPath = "",
    [string]$LauncherUpdateManifestPath = "",
    [string]$FullDownloadPage = "https://gitee.com/PackingProof/PackingProof-Desktop/releases/tag/v0.0.101",
    [string]$PackageFileName = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "AppPatchRuntimeCompatibility.Common.ps1")
. (Join-Path $PSScriptRoot "LauncherBaseline.Common.ps1")
$normalizedFromVersion = $FromVersion.Trim().TrimStart("vV".ToCharArray())
$normalizedToVersion = $ToVersion.Trim().TrimStart("vV".ToCharArray())
foreach ($pair in @(
        @("FromVersion", $normalizedFromVersion),
        @("ToVersion", $normalizedToVersion))) {
    if ($pair[1] -notmatch "^\d+\.\d+\.\d+$") {
        throw "$($pair[0]) must use the numeric X.Y.Z form: $($pair[1])"
    }
}
if ([version]$normalizedFromVersion -ge [version]$normalizedToVersion) {
    throw "FromVersion must be lower than ToVersion"
}

$baselineFullPath = [System.IO.Path]::GetFullPath((Resolve-Path $BaselineAppDir).Path)
$targetFullPath = [System.IO.Path]::GetFullPath((Resolve-Path $TargetAppDir).Path)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "package"))
if (-not $outputFullPath.StartsWith(
        $packageRoot.TrimEnd('\') + '\',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must stay inside the repository package directory: $outputFullPath"
}

$requiredTargetFiles = @(
    "ExpressPackingMonitoring.exe",
    "ExpressPackingMonitoring.dll",
    "coreclr.dll",
    "runtime-seed\layout.json",
    "models\vision-obb.onnx"
)
foreach ($relativePath in $requiredTargetFiles) {
    $path = Join-Path $targetFullPath $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Target app payload is incomplete: $path"
    }
}
if (-not (Test-Path -LiteralPath $baselineFullPath -PathType Container)) {
    throw "Baseline app directory does not exist: $baselineFullPath"
}

$baselineVersion = [string](Get-Item -LiteralPath (
    Join-Path $baselineFullPath "ExpressPackingMonitoring.dll")).VersionInfo.ProductVersion
$targetVersion = [string](Get-Item -LiteralPath (
    Join-Path $targetFullPath "ExpressPackingMonitoring.dll")).VersionInfo.ProductVersion
if ($baselineVersion -notlike "$normalizedFromVersion*") {
    throw "Baseline app version is $baselineVersion, expected $normalizedFromVersion"
}
if ($targetVersion -notlike "$normalizedToVersion*") {
    throw "Target app version is $targetVersion, expected $normalizedToVersion"
}

$visionBaseline = Get-Content -LiteralPath (Join-Path $PSScriptRoot "vision-model-baseline.json") `
    -Raw -Encoding UTF8 | ConvertFrom-Json
$targetModelPath = Join-Path $targetFullPath "models\vision-obb.onnx"
$targetModelSize = (Get-Item -LiteralPath $targetModelPath).Length
$targetModelHash = (Get-FileHash -LiteralPath $targetModelPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($targetModelSize -ne [long]$visionBaseline.size -or
    -not [string]::Equals(
        $targetModelHash,
        [string]$visionBaseline.sha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Target visual model does not match tools/vision-model-baseline.json"
}
$baselineModelPath = Join-Path $baselineFullPath "models\vision-obb.onnx"
$baselineModelMatchesTarget =
    (Test-Path -LiteralPath $baselineModelPath -PathType Leaf) -and
    (Get-Item -LiteralPath $baselineModelPath).Length -eq $targetModelSize -and
    [string]::Equals(
        (Get-FileHash -LiteralPath $baselineModelPath -Algorithm SHA256).Hash.ToLowerInvariant(),
        $targetModelHash,
        [System.StringComparison]::OrdinalIgnoreCase)

if (-not (Test-Path -LiteralPath $CetPolicyAssemblyPath -PathType Leaf)) {
    throw "CET policy assembly does not exist: $CetPolicyAssemblyPath"
}

if ([string]::IsNullOrWhiteSpace($UpdateManifestPath)) {
    $UpdateManifestPath = Join-Path $repoRoot "package\PackingProof+v$normalizedToVersion\update_v$normalizedToVersion.json"
}
$updateManifestFullPath = [System.IO.Path]::GetFullPath((Resolve-Path $UpdateManifestPath).Path)

if ([string]::IsNullOrWhiteSpace($LauncherUpdateManifestPath)) {
    $launcherUpdateManifestPath = Join-Path $repoRoot "package\PackingProof+v0.0.101\update_v0.0.101.json"
}
else {
    $launcherUpdateManifestPath = $LauncherUpdateManifestPath
}
$launcherUpdateManifestFullPath = [System.IO.Path]::GetFullPath(
    (Resolve-Path $launcherUpdateManifestPath).Path)

$defaultPackageName =
    "PackingProof_AppPatch_v${normalizedToVersion}_migration_from_v${normalizedFromVersion}.zip"
$packageName = if ([string]::IsNullOrWhiteSpace($PackageFileName)) {
    $defaultPackageName
} else {
    $PackageFileName.Trim()
}
if ([System.IO.Path]::GetFileName($packageName) -ne $packageName -or
    [System.IO.Path]::GetExtension($packageName) -ine ".zip") {
    throw "PackageFileName must be a plain .zip file name: $packageName"
}

[System.IO.Directory]::CreateDirectory($outputFullPath) | Out-Null
$resolvedOutputRoot = $outputFullPath.TrimEnd('\')
$workRoot = Join-Path $resolvedOutputRoot (".legacy-migration-$normalizedFromVersion-to-$normalizedToVersion")
if (-not $workRoot.StartsWith(
        $resolvedOutputRoot + '\',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Migration work directory escaped OutputDir"
}
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($workRoot) | Out-Null

$preparedTargetAppDir = Join-Path $workRoot "app"
Copy-Item -LiteralPath $targetFullPath -Destination $preparedTargetAppDir -Recurse -Force

$targetExecutable = Join-Path $preparedTargetAppDir "ExpressPackingMonitoring.exe"
$policyAssembly = [System.Reflection.Assembly]::LoadFrom(
    [System.IO.Path]::GetFullPath($CetPolicyAssemblyPath))
$policyType = $policyAssembly.GetType(
    "ExpressPackingMonitoring.UpdateCore.PortableExecutableCetPolicy",
    $true)
$isCetEnabledMethod = $policyType.GetMethod("IsCetCompatibilityEnabled")
$disableCetMethod = $policyType.GetMethod("TryDisableCetCompatibility")
if ($null -eq $isCetEnabledMethod -or $null -eq $disableCetMethod) {
    throw "CET policy assembly does not expose the required methods"
}

$wasCetEnabled = [bool]$isCetEnabledMethod.Invoke(
    $null,
    [object[]]@([string]$targetExecutable))
$disableArguments = [object[]]::new(3)
$disableArguments[0] = [string]$targetExecutable
$disableArguments[1] = $false
$disableArguments[2] = $null
$cetApplied = [bool]$disableCetMethod.Invoke($null, $disableArguments)
if (-not $cetApplied) {
    throw "Failed to disable apphost CET compatibility: $($disableArguments[2])"
}
if ([bool]$isCetEnabledMethod.Invoke(
        $null,
        [object[]]@([string]$targetExecutable))) {
    throw "Apphost CET compatibility is still enabled after rewriting the PE header"
}

# Reuse the release packager's AppPatch implementation instead of maintaining a
# second copy of the manifest, retirement-list, compression and validation rules.
$packagingScriptPath = Join-Path $PSScriptRoot "Publish-CleanPackage.ps1"
$packagingTokens = $null
$packagingErrors = $null
$packagingAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $packagingScriptPath,
    [ref]$packagingTokens,
    [ref]$packagingErrors)
if ($packagingErrors.Count -gt 0) {
    throw "Cannot parse packaging script: $($packagingErrors[0].Message)"
}
$packagingFunctions = @($packagingAst.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] },
        $true))
. ([scriptblock]::Create(($packagingFunctions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine))

foreach ($assignment in @($packagingAst.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [System.Management.Automation.Language.VariableExpressionAst]
            },
            $true))) {
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
$script:repoRoot = $repoRoot
$script:ZipCompressionLevel = "SmallestSize"

$packagePath = Join-Path $outputFullPath $packageName
try {
    $ffmpegBaseline = Get-Content -LiteralPath (Join-Path $PSScriptRoot "ffmpeg-baseline.json") `
        -Raw -Encoding UTF8 | ConvertFrom-Json
    $runtimeCompatibility = Test-AppPatchRuntimeCompatibility `
        -CurrentAppDir $preparedTargetAppDir `
        -BaselineAppDir $baselineFullPath `
        -FFmpegBaseline $ffmpegBaseline
    foreach ($warning in @($runtimeCompatibility.Warnings)) {
        Write-Warning $warning
    }
    if (-not $runtimeCompatibility.Compatible) {
        throw "Cannot build migration AppPatch: $($runtimeCompatibility.Reason)"
    }

    New-AppPatchPackage `
        -CurrentAppDir $preparedTargetAppDir `
        -BaselineDir $baselineFullPath `
        -PatchZipPath $packagePath `
        -BaselineVersion $normalizedFromVersion `
        -LatestVersion $normalizedToVersion `
        -InstallerCmdPath (Join-Path $PSScriptRoot "Install-AppPatch.cmd") `
        -InstallerScriptPath (Join-Path $PSScriptRoot "Apply-AppPatch.ps1") `
        -ExcludeCompatibleRuntimes `
        -RetireListPath (Join-Path $PSScriptRoot "app-patch-retire-list.json")

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $patch = Get-Item -LiteralPath $packagePath
    $patchHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $zip = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $manifestEntry = $zip.Entries |
            Where-Object { $_.FullName -eq "patch_manifest.json" } |
            Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw "Migration package does not contain patch_manifest.json"
        }
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open(), [System.Text.Encoding]::UTF8)
        try {
            $patchManifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        if ([string]$patchManifest.patch_baseline_version -ne $normalizedFromVersion -or
            [string]$patchManifest.latest_version -ne $normalizedToVersion) {
            throw "Migration patch manifest versions do not match the requested migration"
        }
        $modelEntry = $zip.GetEntry("files/models/vision-obb.onnx")
        if ($baselineModelMatchesTarget -and $null -ne $modelEntry) {
            throw "Migration package repeats an unchanged visual model"
        }
        if (-not $baselineModelMatchesTarget -and $null -eq $modelEntry) {
            throw "Migration package is missing the changed target visual model"
        }
        if ($null -ne $modelEntry -and $modelEntry.Length -ne $targetModelSize) {
            throw "Migration package visual model size does not match the target payload"
        }
    }
    finally {
        $zip.Dispose()
    }

    $update = Get-Content -LiteralPath $updateManifestFullPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $update.latest_version = $normalizedToVersion
    $update.patch_baseline_version = $normalizedFromVersion
    $update.patch_supported = $true
    $update.download_source = "gitee"
    $update.full_download_page = $FullDownloadPage.Trim()
    $update.patch_package.type = "baseline_patch"
    $update.patch_package.url =
        "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v$normalizedToVersion/$packageName"
    $update.patch_package.github_url =
        "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v$normalizedToVersion/$packageName"
    $update.patch_package.gitee_url =
        "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v$normalizedToVersion/$packageName"
    $update.patch_package.sha256 = $patchHash
    $update.patch_package.size = $patch.Length
    $update.patch_package.required_launcher_protocol = 0

    $launcherUpdate = Get-Content -LiteralPath $launcherUpdateManifestFullPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $update.launcher_package = $launcherUpdate.launcher_package

    $updateOutputPath = Join-Path $outputFullPath "update_v$normalizedToVersion.migration.json"
    $updateJson = ($update | ConvertTo-Json -Depth 12) + "`n"
    [System.IO.File]::WriteAllText(
        $updateOutputPath,
        $updateJson,
        [System.Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        FromVersion = $normalizedFromVersion
        ToVersion = $normalizedToVersion
        PackagePath = $packagePath
        PackageSize = $patch.Length
        PackageSha256 = $patchHash
        UpdateManifestPath = $updateOutputPath
        CetWasEnabled = $wasCetEnabled
        VisionModelVersion = [string]$visionBaseline.version
        LauncherVersion = [string]$update.launcher_package.version
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
