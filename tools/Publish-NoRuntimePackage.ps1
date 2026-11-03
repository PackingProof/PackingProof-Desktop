# 生成「不带 .NET 运行时」的清爽包，仅在完整 Setup 达到 Gitee 的 100MB 附件上限时使用。
#
#   pwsh -NoProfile -File tools\Publish-NoRuntimePackage.ps1 -Tag v0.0.70 [-UploadGitee] [-IncludeZip]
#
# 约定：
# - 默认只生成并上传**安装向导**（Gitee 用户和 GitHub 用户一样双击安装）；ZIP 免安装包是可选本地产物，
#   只有显式加 -IncludeZip 时才生成并上传。
# - 根启动器是 AOT 原生程序，不需要运行时，直接复用已发布清爽包里的那一份（保证字节一致）；
# - `app\` 由本脚本重新做一次**框架依赖**发布（正式包是自包含发布，直接复用会把 140MB 运行时一起带进去），
#   并**不带**包根的 `runtime\`、也不带 `app\runtime-seed\`：共享布局只在自包含包里由主程序落成，
#   这一版就是给愿意自己装 .NET 10 Desktop Runtime 的用户准备的；
# - 目标机器必须已安装 .NET 10 Desktop Runtime (x64)，包内附说明文件；
# - 如果标准 Setup 本身已经严格小于 100MB，脚本会跳过 no-runtime 构建；带 -UploadGitee 时直接上传标准 Setup。
# - 更新方式和标准包一致：增量包只包含与基线不同的应用文件，运行时没变时不含运行时，
#   所以 no-runtime 安装目录同样能被启动器的 AppPatch 正常更新。
param(
    [string]$Tag = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipSetup,
    [switch]$IncludeZip,
    [switch]$UploadGitee,
    [string]$GiteeRepository = "PackingProof/PackingProof-Desktop"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

. (Join-Path $PSScriptRoot "ReleaseVersion.Common.ps1")
. (Join-Path $PSScriptRoot "GiteeAuth.Common.ps1")
. (Join-Path $PSScriptRoot "VisionModelBaseline.Common.ps1")

$releaseTag = if ([string]::IsNullOrWhiteSpace($Tag)) { (git describe --tags --exact-match 2>$null) } else { $Tag }
if ([string]::IsNullOrWhiteSpace($releaseTag)) {
    throw "必须用 -Tag 指定已发布的版本标签（例如 -Tag v0.0.70）"
}

$normalizedVersion = Get-NormalizedReleaseVersion $releaseTag
$artifactNames = Get-ReleaseArtifactNames -Tag $releaseTag -RepoRoot $repoRoot
$packageRoot = $artifactNames.PackageRoot

$standardSetupPath = Join-Path $packageRoot $artifactNames.SetupFileName
if (-not $IncludeZip -and (Test-Path -LiteralPath $standardSetupPath -PathType Leaf) -and
    (Test-GiteeAttachmentFits -Size (Get-Item -LiteralPath $standardSetupPath).Length)) {
    $standardSetupSize = (Get-Item -LiteralPath $standardSetupPath).Length
    Write-Host ("==> 完整 Setup 已小于 100MB（{0:N1} MB），跳过 no-runtime 构建" -f ($standardSetupSize / 1MB))
    if ($UploadGitee -and -not $SkipSetup) {
        $null = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
        $setupName = Split-Path -Leaf $standardSetupPath
        $noRuntimeName = "PackingProof_Setup_no-runtime_$releaseTag.exe"
        Remove-GiteeReleaseAttachmentsForTag `
            -Repository $GiteeRepository `
            -Tag $releaseTag `
            -FileNames @($setupName, $noRuntimeName)
        & gitee release upload --repo $GiteeRepository $releaseTag $standardSetupPath
        if ($LASTEXITCODE -ne 0) {
            throw "Gitee 完整 Setup 上传失败，退出码 $LASTEXITCODE"
        }
        Write-Host "Gitee 已直接上传：$setupName"
    }
    Write-Host "完成"
    exit 0
}

$cleanPackageDir = Join-Path $packageRoot "PackingProof+$releaseTag"
$launcherExe = Join-Path $cleanPackageDir "ExpressPackingMonitoring.exe"
$sourceAppDir = Join-Path $cleanPackageDir "app"
if (-not (Test-Path -LiteralPath $launcherExe -PathType Leaf)) {
    throw "找不到已发布清爽包里的根启动器：$launcherExe（先生成正式包）"
}

$workRoot = Join-Path $repoRoot "package\.no-runtime-work"
$packageDir = Join-Path $workRoot "package"
$zipName = "PackingProof_no-runtime_$releaseTag.zip"
$zipPath = Join-Path $packageRoot $zipName

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

Write-Host "==> 自己构建一份框架依赖产物（正式包是自包含发布，直接复用会把运行时也带进去）"
if (-not (Test-Path -LiteralPath $sourceAppDir -PathType Container)) {
    throw "找不到正式包里的 app 目录：$sourceAppDir（先生成正式包）"
}
$runtimePackageAppDir = Join-Path $packageDir "app"
New-Item -ItemType Directory -Force -Path $runtimePackageAppDir | Out-Null
$appProject = Join-Path $repoRoot "src\ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$visionAssembly = Join-Path $sourceAppDir "ExpressPackingMonitoring.Vision.Obb.dll"
$visionModel = Join-Path $sourceAppDir "models\vision-obb.onnx"
$hasVisionAssembly = Test-Path -LiteralPath $visionAssembly -PathType Leaf
$hasVisionModel = Test-Path -LiteralPath $visionModel -PathType Leaf
if ($hasVisionAssembly -ne $hasVisionModel) {
    throw "标准包的视觉功能状态不一致：Vision.Obb 与模型必须同时存在或同时排除"
}
$includeVisionObb = $hasVisionAssembly
$visionPublishArguments = @(
    "-p:IncludeVisionObb=$($includeVisionObb.ToString().ToLowerInvariant())"
)
if ($includeVisionObb) {
    $visionPublishArguments += "-p:CameraVisionModelPath=$visionModel"
}
$publishArguments = @(
    "publish", $appProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--nologo",
    "--self-contained", "false",
    "-p:PublishSingleFile=false",
    "-p:InformationalVersion=$normalizedVersion",
    "-p:IncludeSourceRevisionInInformationalVersion=false",
    "-o", $runtimePackageAppDir
) + $visionPublishArguments
$publishOutput = & dotnet @publishArguments 2>&1
if (-not (Test-Path -LiteralPath (Join-Path $runtimePackageAppDir "ExpressPackingMonitoring.dll") -PathType Leaf)) {
    throw "框架依赖发布失败：$([Environment]::NewLine)$($publishOutput -join [Environment]::NewLine)"
}
$visionRuntimeFiles = @(
    "ExpressPackingMonitoring.Vision.Obb.dll",
    "Microsoft.ML.OnnxRuntime.dll",
    "System.Numerics.Tensors.dll",
    "DirectML.dll",
    "onnxruntime.dll",
    "onnxruntime_providers_shared.dll",
    "models\vision-obb.onnx"
)
if ($includeVisionObb) {
    foreach ($visionFile in $visionRuntimeFiles) {
        $sourceFile = Join-Path $sourceAppDir $visionFile
        $publishedFile = Join-Path $runtimePackageAppDir $visionFile
        if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
            throw "no-runtime 包未继承视觉文件：$visionFile"
        }
        $publishedDirectory = Split-Path -Parent $publishedFile
        [System.IO.Directory]::CreateDirectory($publishedDirectory) | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $publishedFile -Force
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
        $publishedHash = (Get-FileHash -LiteralPath $publishedFile -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $publishedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "no-runtime 视觉文件与标准包不一致：$visionFile"
        }
    }
    $visionModelBaseline = Read-VisionModelBaselineManifest `
        -ManifestPath (Join-Path $PSScriptRoot "vision-model-baseline.json")
    Assert-VisionModelRuntimeFiles `
        -AppDir $runtimePackageAppDir `
        -Baseline $visionModelBaseline
}
else {
    foreach ($visionOnlyFile in @(
        "ExpressPackingMonitoring.Vision.Obb.dll",
        "Microsoft.ML.OnnxRuntime.dll",
        "DirectML.dll",
        "onnxruntime_providers_shared.dll",
        "models\vision-obb.onnx")) {
        if (Test-Path -LiteralPath (Join-Path $runtimePackageAppDir $visionOnlyFile)) {
            throw "标准包未包含视觉功能，但 no-runtime 包出现：$visionOnlyFile"
        }
    }
}
foreach ($runtimeOnlyFile in @("coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll")) {
    if (Test-Path -LiteralPath (Join-Path $runtimePackageAppDir $runtimeOnlyFile)) {
        throw "框架依赖产物里不该有 $runtimeOnlyFile：$runtimePackageAppDir"
    }
}
$seedDirectory = Join-Path $runtimePackageAppDir "runtime-seed"
if (Test-Path -LiteralPath $seedDirectory) {
    [System.IO.Directory]::Delete($seedDirectory, $true)
}

# 正式包里由发布流程额外铺进去的资源，这里按同样的形态补上（没有的就跳过）。
foreach ($sharedAsset in @("tools\ffmpeg.exe", "ExpressPackingMonitoring.WinTts.dll", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll")) {
    $assetSource = Join-Path $sourceAppDir $sharedAsset
    if (-not (Test-Path -LiteralPath $assetSource -PathType Leaf)) { continue }
    $assetDestination = Join-Path $runtimePackageAppDir $sharedAsset
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($assetDestination)) | Out-Null
    Copy-Item -LiteralPath $assetSource -Destination $assetDestination -Force
}
$ttsCacheSource = Join-Path $sourceAppDir "tts_cache"
if (Test-Path -LiteralPath $ttsCacheSource -PathType Container) {
    Copy-Item -LiteralPath $ttsCacheSource -Destination (Join-Path $runtimePackageAppDir "tts_cache") -Recurse -Force
}

Write-Host "==> 组装清爽包（根启动器 + app\）"
Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $packageDir "ExpressPackingMonitoring.exe") -Force
Set-Content -LiteralPath (Join-Path $packageDir "使用前必读.txt") -Encoding UTF8 -Value @"
本包不包含 .NET 运行时，运行前请先安装：
  .NET 10 Desktop Runtime (x64)  https://dotnet.microsoft.com/download/dotnet/10.0

安装后双击根目录的 ExpressPackingMonitoring.exe 启动。
这份安装向导仅供 Gitee 下载（Gitee 单附件上限 100MB，自包含安装包放不下）。
更新方式和标准包一致：启动器会自动下载并安装增量包（增量包只含应用文件，不含运行时）；
也可以直接下载新版本的安装向导，覆盖安装到原目录。
%LOCALAPPDATA%\ExpressPackingMonitoring 下的配置、数据库与录像不受影响。
"@

$zipPath = ""
if ($IncludeZip) {
    $zipPath = Join-Path $packageRoot $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    $sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($sevenZip) {
        & $sevenZip.Source a -tzip -mx=5 $zipPath (Join-Path $packageDir "*") | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip 打包失败，退出码 $LASTEXITCODE"
        }
    }
    else {
        Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force
    }

    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    Write-Host ("==> ZIP（可选本地产物）：{0}（{1:N1} MB）" -f $zipPath, ($zipSize / 1MB))
    if ($zipSize -gt 100MB) {
        throw "ZIP 超过 Gitee 单附件上限 100MB，需要进一步精简"
    }
}

# 同样打一份安装向导：Gitee 用户和 GitHub 用户拿到的是同一种"双击安装"体验。
$setupPath = ""
if (-not $SkipSetup) {
    # 命名与其它产物一致：版本号放最后（PackingProof_Setup_vX.Y.Z.exe / PackingProof_AppPatch_vX.Y.Z.zip）。
    $setupName = "PackingProof_Setup_no-runtime_$releaseTag.exe"
    $installerBuilder = Join-Path $PSScriptRoot "Build-Installer.ps1"
    Write-Host "==> 生成安装向导（不含运行时）"
    & $installerBuilder `
        -SourceDir $packageDir `
        -Version $normalizedVersion `
        -OutputDir $packageRoot `
        -OutputFileName $setupName
    $setupPath = Join-Path $packageRoot $setupName
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "安装向导没有生成：$setupPath"
    }
    $setupSize = (Get-Item -LiteralPath $setupPath).Length
    Write-Host ("==> 安装向导：{0}（{1:N1} MB）" -f $setupPath, ($setupSize / 1MB))
    if ($setupSize -gt 100MB) {
        Write-Host "提示：安装向导超过 Gitee 单附件上限 100MB，本次不上传；可改用 -IncludeZip 生成较小的 ZIP" -ForegroundColor Yellow
        $setupPath = ""
    }
}

if ($UploadGitee) {
    $null = Import-GiteeTokenFromEnvFile -RepoRoot $repoRoot
    Write-Host "==> 上传到 Gitee Release $releaseTag"
    $uploadFiles = @()
    if (-not [string]::IsNullOrWhiteSpace($setupPath)) { $uploadFiles += $setupPath }
    if (-not [string]::IsNullOrWhiteSpace($zipPath)) { $uploadFiles += $zipPath }
    if ($uploadFiles.Count -eq 0) {
        throw "没有可上传的产物"
    }
    # 重发同一版本时 Gitee 会留两份同名附件（它不支持覆盖上传）：先删旧的再传新的。
    Remove-GiteeReleaseAttachmentsForTag `
        -Repository $GiteeRepository `
        -Tag $releaseTag `
        -FileNames @($uploadFiles | ForEach-Object { Split-Path -Leaf $_ })
    & gitee release upload --repo $GiteeRepository $releaseTag @uploadFiles
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee 附件上传失败，退出码 $LASTEXITCODE"
    }
    Write-Host ("Gitee 已上传：{0}" -f (($uploadFiles | ForEach-Object { Split-Path -Leaf $_ }) -join ", "))
}

Remove-Item -LiteralPath $workRoot -Recurse -Force
Write-Host "完成"
