# 构建可提交 Microsoft Store 的 MSIX 包。
#
# 商店提交不需要 CA 代码签名证书，Microsoft 会在认证通过后重新签名；
# -Sign 仅用于本机安装验证（会生成自签名证书）。
# 正式发布由 Publish-CleanPackage.ps1 调用，并用 -AppPayloadDir 复用同一次 Setup 的 app 负载。
param(
    [string]$Version = "",
    [string]$PackageVersion = "",
    [string]$IdentityName = "",
    [string]$Publisher = "",
    [string]$PublisherDisplayName = "",
    [string]$DisplayName = "",
    [string]$Description = "",
    [string]$ConfigPath = "",
    [string]$OutputRoot = "",
    [string]$AppPayloadDir = "",
    [string]$SymbolsDir = "",
    [switch]$SkipBuild,
    [switch]$RequireStoreIdentity,
    [switch]$Sign,
    [switch]$TrustCertificate,
    [switch]$SuppressCertificateDetails,
    [string]$CertificatePath = "",
    [string]$CertificatePassword = "",
    [string]$CertificateSubject = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$storeRoot = Join-Path $repoRoot "docs\store"
$manifestTemplate = Join-Path $storeRoot "AppxManifest.xml"
$iconPath = Join-Path $repoRoot "src\ExpressPackingMonitoring\app.ico"
$appProject = Join-Path $repoRoot "src\ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$winttsProject = Join-Path $repoRoot "src\ExpressPackingMonitoring.WinTts\ExpressPackingMonitoring.WinTts.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    $props = [System.IO.File]::ReadAllText($propsPath, [System.Text.Encoding]::UTF8)
    $match = [regex]::Match($props, "<Version>\s*([^<]+?)\s*</Version>")
    if (-not $match.Success) { throw "读不到 Directory.Build.props 里的 <Version>" }
    $Version = $match.Groups[1].Value.Trim()
}

if ($Version -notmatch "^\d+\.\d+\.\d+(\.\d+)?$") {
    throw "版本号必须是 3 段或 4 段数字：$Version"
}
$versionParts = $Version.Split(".")
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    # 应用版本是 0.x.y，而商店包版本第 1 段不能为 0；默认映射成 1.0.y.0。
    $PackageVersion = if ($versionParts.Count -eq 3 -and $versionParts[0] -eq "0") {
        "1.$($versionParts[1]).$($versionParts[2]).0"
    }
    elseif ($versionParts.Count -eq 3) {
        "$Version.0"
    }
    else {
        $Version
    }
}

if ($PackageVersion -notmatch "^\d+\.\d+\.\d+\.\d+$") {
    throw "商店包版本必须是 4 段数字：$PackageVersion"
}
$packageVersion = $PackageVersion
$packageVersionParts = $packageVersion.Split(".")
if ($packageVersionParts[3] -ne "0") {
    throw "商店包版本的第 4 段必须为 0：$PackageVersion"
}
foreach ($part in $packageVersionParts) {
    $number = 0
    if (-not [int]::TryParse($part, [ref]$number) -or $number -lt 0 -or $number -gt 65535) {
        throw "商店包版本号段无效：$PackageVersion"
    }
}
if ($packageVersionParts[0] -eq "0") {
    throw "商店包版本第 1 段不能为 0：$PackageVersion"
}

# 本地配置只放 Partner Center 预留身份，不提交到仓库。
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $localConfig = Join-Path $PSScriptRoot "store-package.local.json"
    if (Test-Path -LiteralPath $localConfig -PathType Leaf) {
        $ConfigPath = $localConfig
    }
}
if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "找不到商店身份配置：$ConfigPath"
    }
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($IdentityName)) { $IdentityName = [string]$config.identityName }
    if ([string]::IsNullOrWhiteSpace($Publisher)) { $Publisher = [string]$config.publisher }
    if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) { $PublisherDisplayName = [string]$config.publisherDisplayName }
    if ([string]::IsNullOrWhiteSpace($DisplayName)) { $DisplayName = [string]$config.displayName }
    if ([string]::IsNullOrWhiteSpace($Description)) { $Description = [string]$config.description }
}

$usingPlaceholderIdentity = $false
if ([string]::IsNullOrWhiteSpace($IdentityName)) {
    $IdentityName = "PackingProof.PackingProof"
    $usingPlaceholderIdentity = $true
}
if ([string]::IsNullOrWhiteSpace($Publisher)) {
    $Publisher = "CN=PackingProof"
    $usingPlaceholderIdentity = $true
}
if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) { $PublisherDisplayName = "PackingProof" }
if ([string]::IsNullOrWhiteSpace($DisplayName)) { $DisplayName = "PackingProof 快递打包监控" }
if ([string]::IsNullOrWhiteSpace($Description)) {
    $Description = "扫码自动录像，按快递单号保存，支持订单播报、退款拦截、局域网回放与多工位备份"
}

if ($RequireStoreIdentity -and $usingPlaceholderIdentity) {
    throw "提交商店前必须填写 Partner Center 预留的 Identity Name 和 Publisher；见 tools\store-package.example.json"
}
if ($IdentityName -notmatch "^[A-Za-z0-9.-]+$" -or $IdentityName.StartsWith(".") -or $IdentityName.EndsWith(".")) {
    throw "Identity Name 格式无效：$IdentityName"
}
if ($Publisher -notmatch "^CN=.+") {
    throw "Publisher 必须是 Partner Center 显示的 CN=... 形式：$Publisher"
}

function Resolve-SdkTool {
    param([string]$Name)
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"),
        (Join-Path $env:ProgramFiles "Windows Kits\10\bin")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }

    $candidates = foreach ($root in $roots) {
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            Join-Path $_.FullName "x64\$Name"
        }
    }
    $tool = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Sort-Object -Descending | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($tool)) {
        throw "找不到 $Name；请安装 Windows 11 SDK（含 MakeAppx/SignTool）"
    }
    return $tool
}

function Copy-SymbolFiles {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [switch]$RemoveSource
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "符号目录不存在：$SourceDirectory"
    }

    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\') + '\'
    $symbolFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Filter "*.pdb")
    foreach ($symbolFile in $symbolFiles) {
        $relativePath = $symbolFile.FullName.Substring($sourceRoot.Length)
        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path -Parent $destinationPath
        [System.IO.Directory]::CreateDirectory($destinationParent) | Out-Null
        Copy-Item -LiteralPath $symbolFile.FullName -Destination $destinationPath -Force
        if ($RemoveSource) {
            Remove-Item -LiteralPath $symbolFile.FullName -Force
        }
    }
    return $symbolFiles
}

function Assert-RequiredStoreSymbols {
    param(
        [Parameter(Mandatory = $true)][string]$SymbolsDirectory,
        [Parameter(Mandatory = $true)][string]$PayloadDirectory
    )

    $actualNames = @(Get-ChildItem -LiteralPath $SymbolsDirectory -Recurse -File -Filter "*.pdb" |
        ForEach-Object { $_.Name })
    $requiredAssemblies = @(
        Get-ChildItem -LiteralPath $PayloadDirectory -File -Filter "ExpressPackingMonitoring*.dll" |
            ForEach-Object { $_.BaseName }
    )
    $missing = @(
        $requiredAssemblies |
            Where-Object {
                Test-Path -LiteralPath (Join-Path $PayloadDirectory "$_.dll") -PathType Leaf
            } |
            Where-Object {
                $actualNames -notcontains "$_.pdb"
            } |
            ForEach-Object { "$_.pdb" }
    )
    if ($missing.Count -gt 0) {
        throw "Store 符号包缺少程序集符号：$($missing -join ', ')"
    }
}

function New-StoreUploadArchive {
    param(
        [Parameter(Mandatory = $true)][string]$MsixPath,
        [Parameter(Mandatory = $true)][string]$SymbolPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $DestinationPath -PathType Leaf) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open(
        $DestinationPath,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($sourcePath in @($MsixPath, $SymbolPath)) {
            $entry = $archive.CreateEntry(
                [System.IO.Path]::GetFileName($sourcePath),
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $sourceStream = [System.IO.File]::OpenRead($sourcePath)
            try {
                $sourceStream.CopyTo($entryStream)
            }
            finally {
                $sourceStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-StoreUploadArchive {
    param(
        [Parameter(Mandatory = $true)][string]$UploadPath,
        [Parameter(Mandatory = $true)][string]$MsixPath,
        [Parameter(Mandatory = $true)][string]$SymbolPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($UploadPath)
    try {
        $expected = @{
            ([System.IO.Path]::GetFileName($MsixPath)) = (Get-Item -LiteralPath $MsixPath).Length
            ([System.IO.Path]::GetFileName($SymbolPath)) = (Get-Item -LiteralPath $SymbolPath).Length
        }
        $entries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith("/") })
        if ($entries.Count -ne $expected.Count) {
            throw "MSIX 上传包条目数不正确：$UploadPath"
        }
        foreach ($entry in $entries) {
            if (-not $expected.ContainsKey($entry.FullName) -or
                [long]$entry.Length -ne [long]$expected[$entry.FullName]) {
                throw "MSIX 上传包条目不正确：$($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$makeappx = Resolve-SdkTool -Name "makeappx.exe"
$makepri = Resolve-SdkTool -Name "makepri.exe"
$signtool = Resolve-SdkTool -Name "signtool.exe"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "package\store"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd("\")
$productDir = [System.IO.Path]::GetFullPath((Join-Path $OutputRoot ("PackingProof_Store_v" + $Version)))
$expectedPrefix = $OutputRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $productDir.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "产物目录不在允许的输出根目录内：$productDir"
}

$payloadDir = Join-Path $productDir "payload"
$msixPath = Join-Path $productDir ("PackingProof_Store_v" + $Version + ".msix")
$appxsymPath = Join-Path $productDir ("PackingProof_Store_v" + $Version + ".appxsym")
$msixUploadPath = Join-Path $productDir ("PackingProof_Store_v" + $Version + ".msixupload")
$symbolStagingDir = Join-Path $productDir ".appxsym-staging"

$useExistingPayload = -not [string]::IsNullOrWhiteSpace($AppPayloadDir)
if ($useExistingPayload) {
    if (-not (Test-Path -LiteralPath $AppPayloadDir -PathType Container)) {
        throw "找不到已构建的应用负载：$AppPayloadDir"
    }
    if ([string]::IsNullOrWhiteSpace($SymbolsDir) -or
        -not (Test-Path -LiteralPath $SymbolsDir -PathType Container)) {
        throw "复用已构建负载时必须提供 -SymbolsDir，确保 .appxsym 与应用二进制来自同一次编译"
    }
    $resolvedAppPayloadDir = (Resolve-Path -LiteralPath $AppPayloadDir).Path
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedAppPayloadDir "ExpressPackingMonitoring.exe") -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $resolvedAppPayloadDir "coreclr.dll") -PathType Leaf)) {
        throw "已构建的应用负载不是自包含发布：$resolvedAppPayloadDir"
    }
    $productPrefix = $productDir.TrimEnd('\') + '\'
    $payloadPrefix = $resolvedAppPayloadDir.TrimEnd('\') + '\'
    if ([string]::Equals($resolvedAppPayloadDir, $productDir, [System.StringComparison]::OrdinalIgnoreCase) -or
        $resolvedAppPayloadDir.StartsWith($productPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "AppPayloadDir 不能位于 Store 产物目录内，否则重建产物时会先删除源负载：$resolvedAppPayloadDir"
    }
    if ($productDir.StartsWith($payloadPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Store 产物目录不能位于 AppPayloadDir 内，否则会把产物递归复制进源负载：$productDir"
    }
    if (Test-Path -LiteralPath $productDir) {
        Remove-Item -LiteralPath $productDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Get-ChildItem -LiteralPath $resolvedAppPayloadDir -Force |
        Copy-Item -Destination $payloadDir -Recurse -Force
}
elseif ($SkipBuild) {
    if (-not (Test-Path -LiteralPath $payloadDir -PathType Container)) {
        throw "指定了 -SkipBuild，但找不到已有 payload：$payloadDir"
    }
}
else {
    if (Test-Path -LiteralPath $productDir) {
        Remove-Item -LiteralPath $productDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

    Write-Host "发布自包含应用：$Version"
    & dotnet publish $appProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --nologo `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        -p:PackingProofCollectStoreSymbols=true `
        -p:DebugType=portable `
        -p:DebugSymbols=true `
        -o $payloadDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

    [System.IO.Directory]::CreateDirectory($symbolStagingDir) | Out-Null
    [void](Copy-SymbolFiles -SourceDirectory $payloadDir -DestinationDirectory $symbolStagingDir -RemoveSource)

    # 主程序工程不会把 WinTts 的运行时复制进 publish 输出，按发布脚本的方式单独补三份。
    $winttsTemp = Join-Path $productDir ".wintts"
    & dotnet publish $winttsProject -c Release --nologo -o $winttsTemp
    if ($LASTEXITCODE -ne 0) { throw "WinTts publish 失败，退出码 $LASTEXITCODE" }
    try {
        foreach ($file in @("ExpressPackingMonitoring.WinTts.dll", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll")) {
            $source = Join-Path $winttsTemp $file
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "WinTts 发布产物缺少 $file"
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $payloadDir $file) -Force
        }
        $winttsPdb = Join-Path $winttsTemp "ExpressPackingMonitoring.WinTts.pdb"
        if (Test-Path -LiteralPath $winttsPdb -PathType Leaf) {
            Copy-Item -LiteralPath $winttsPdb -Destination (Join-Path $symbolStagingDir "ExpressPackingMonitoring.WinTts.pdb") -Force
        }
    }
    finally {
        Remove-Item -LiteralPath $winttsTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

[System.IO.Directory]::CreateDirectory($symbolStagingDir) | Out-Null
if (-not [string]::IsNullOrWhiteSpace($SymbolsDir)) {
    [void](Copy-SymbolFiles -SourceDirectory $SymbolsDir -DestinationDirectory $symbolStagingDir)
}
[void](Copy-SymbolFiles -SourceDirectory $payloadDir -DestinationDirectory $symbolStagingDir -RemoveSource)
Assert-RequiredStoreSymbols -SymbolsDirectory $symbolStagingDir -PayloadDirectory $payloadDir

if (-not (Test-Path -LiteralPath (Join-Path $payloadDir "ExpressPackingMonitoring.exe") -PathType Leaf)) {
    throw "payload 里没有 ExpressPackingMonitoring.exe"
}
if (-not (Test-Path -LiteralPath (Join-Path $payloadDir "coreclr.dll") -PathType Leaf)) {
    throw "payload 不是自包含发布：缺少 coreclr.dll"
}

# Store 版以 MSIX 包身份运行，主程序启动时会跳过共享运行时落成；
# runtime-seed 只服务 Setup 版的扩展市场，不能进入商店包。
$runtimeSeedDir = [System.IO.Path]::GetFullPath((Join-Path $payloadDir "runtime-seed"))
$payloadPrefix = [System.IO.Path]::GetFullPath($payloadDir).TrimEnd('\') + '\'
if (-not $runtimeSeedDir.StartsWith($payloadPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "runtime-seed 路径不在 Store payload 内：$runtimeSeedDir"
}
if (Test-Path -LiteralPath $runtimeSeedDir -PathType Container) {
    Remove-Item -LiteralPath $runtimeSeedDir -Recurse -Force
}

# 商店包必须是完整应用，不能夹带 Setup 版的手工更新入口。
# 商店版更新统一由 Microsoft Store 负责；这些脚本一旦混入包内，
# 用户会误以为可以绕过商店更新，甚至把补丁往 WindowsApps 里写。
$forbiddenEntryNames = @(
    "apply_app_patch.ps1",
    "双击更新主程序.cmd",
    "apply_launcher_patch.ps1",
    "双击更新启动器.cmd"
)
$forbiddenEntry = Get-ChildItem -LiteralPath $payloadDir -Recurse -File |
    Where-Object { $forbiddenEntryNames -contains $_.Name } |
    Select-Object -First 1
if ($null -ne $forbiddenEntry) {
    throw "商店包不能包含 Setup 版更新脚本：$($forbiddenEntry.FullName)"
}

Get-ChildItem -LiteralPath $payloadDir -Recurse -File -Include *.pdb, *.xml |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$assetsDir = Join-Path $payloadDir "Assets"
& (Join-Path $PSScriptRoot "New-StoreAssets.ps1") -IconPath $iconPath -OutputDir $assetsDir

$editionMarkerPath = Join-Path $payloadDir "PackingProof.PackageEdition.json"
$editionMarker = [ordered]@{
    edition = "store"
    app_version = $Version
    package_version = $packageVersion
    package_identity = $IdentityName
} | ConvertTo-Json
[System.IO.File]::WriteAllText(
    $editionMarkerPath,
    $editionMarker,
    [System.Text.UTF8Encoding]::new($false))

$manifest = [System.IO.File]::ReadAllText($manifestTemplate, [System.Text.Encoding]::UTF8)
$manifest = $manifest.Replace("{{IDENTITY_NAME}}", [System.Security.SecurityElement]::Escape($IdentityName))
$manifest = $manifest.Replace("{{PUBLISHER}}", [System.Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace("{{PACKAGE_VERSION}}", [System.Security.SecurityElement]::Escape($packageVersion))
$manifest = $manifest.Replace("{{DISPLAY_NAME}}", [System.Security.SecurityElement]::Escape($DisplayName))
$manifest = $manifest.Replace("{{PUBLISHER_DISPLAY_NAME}}", [System.Security.SecurityElement]::Escape($PublisherDisplayName))
$manifest = $manifest.Replace("{{DESCRIPTION}}", [System.Security.SecurityElement]::Escape($Description))
if ($manifest.Contains("{{")) { throw "AppxManifest.xml 仍有未替换的占位符" }

$manifestPath = Join-Path $payloadDir "AppxManifest.xml"
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

# 生成资源索引，让 Windows 能找到 AppList/Square44x44Logo 的 targetsize 和
# altform-unplated 变体；没有 resources.pri 时任务栏会缩小图标并加主题色底板。
$priconfigPath = Join-Path $productDir "priconfig.xml"
$resourcesPriPath = Join-Path $payloadDir "resources.pri"
$languagePriPath = Join-Path $payloadDir "resources.language-ja.pri"
Remove-Item -LiteralPath $resourcesPriPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $languagePriPath -Force -ErrorAction SilentlyContinue
& $makepri createconfig /cf $priconfigPath /dq zh-CN /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig 失败，退出码 $LASTEXITCODE" }
& $makepri new /pr $payloadDir /cf $priconfigPath /of $resourcesPriPath /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new 失败，退出码 $LASTEXITCODE" }
Remove-Item -LiteralPath $priconfigPath -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $msixPath) {
    Remove-Item -LiteralPath $msixPath -Force
}

Write-Host "打包 MSIX：$msixPath"
& $makeappx pack /d $payloadDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败，退出码 $LASTEXITCODE" }

$signed = $false
$certificateThumbprint = ""
$certificateCerPath = ""
$certificatePfxPath = ""
$testCertificatePassword = ""
if ($Sign) {
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $signArguments = @("sign", "/fd", "SHA256", "/f", $CertificatePath)
        if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
            $signArguments += @("/p", $CertificatePassword)
        }
        $signArguments += $msixPath
        & $signtool @signArguments
        if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败，退出码 $LASTEXITCODE" }
    }
    else {
        # MSIX 侧载签名要求证书主题与包 Publisher 完全一致，否则 SignerSign 会报 0x8007000b。
        if ([string]::IsNullOrWhiteSpace($CertificateSubject)) {
            $CertificateSubject = $Publisher
        }
        if (-not [string]::Equals($CertificateSubject.Trim(), $Publisher.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "自签名证书主题必须与包 Publisher 完全一致：$CertificateSubject / $Publisher"
        }

        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $CertificateSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName "PackingProof Store Test" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        $certificateThumbprint = $certificate.Thumbprint

        # 通过 PFX 交给 SignTool，避免 CurrentUser\My 里的 CNG 私钥在部分机器上加载失败。
        $certificatePfxPath = Join-Path $productDir "PackingProof-test.pfx"
        $testCertificatePassword = "PackingProofTest!" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
        $securePassword = ConvertTo-SecureString -String $testCertificatePassword -AsPlainText -Force
        Export-PfxCertificate -Cert $certificate -FilePath $certificatePfxPath -Password $securePassword | Out-Null
        & $signtool sign /fd SHA256 /f $certificatePfxPath /p $testCertificatePassword $msixPath
        if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败，退出码 $LASTEXITCODE" }

        $certificateCerPath = Join-Path $productDir "PackingProof-test.cer"
        Export-Certificate -Cert $certificate -FilePath $certificateCerPath | Out-Null
        if ($TrustCertificate) {
            $trusted = Get-ChildItem "Cert:\CurrentUser\TrustedPeople" |
                Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }
            if (-not $trusted) {
                Import-Certificate -FilePath $certificateCerPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
            }
        }
    }
    $signed = $true
}

if (Test-Path -LiteralPath $appxsymPath -PathType Leaf) {
    Remove-Item -LiteralPath $appxsymPath -Force
}
$temporarySymbolArchive = Join-Path $productDir ("PackingProof_Store_v" + $Version + ".appxsym.tmp.zip")
if (Test-Path -LiteralPath $temporarySymbolArchive -PathType Leaf) {
    Remove-Item -LiteralPath $temporarySymbolArchive -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $symbolStagingDir,
    $temporarySymbolArchive,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Move-Item -LiteralPath $temporarySymbolArchive -Destination $appxsymPath -Force
Remove-Item -LiteralPath $symbolStagingDir -Recurse -Force

$symbolArchive = [System.IO.Compression.ZipFile]::OpenRead($appxsymPath)
try {
    $symbolEntries = @($symbolArchive.Entries | Where-Object { $_.Name -like "*.pdb" })
    if ($symbolEntries.Count -eq 0) {
        throw "Store 符号包中没有 PDB 文件：$appxsymPath"
    }
}
finally {
    $symbolArchive.Dispose()
}

New-StoreUploadArchive `
    -MsixPath $msixPath `
    -SymbolPath $appxsymPath `
    -DestinationPath $msixUploadPath
Assert-StoreUploadArchive `
    -UploadPath $msixUploadPath `
    -MsixPath $msixPath `
    -SymbolPath $appxsymPath

$hash = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash
$sizeMb = [Math]::Round((Get-Item -LiteralPath $msixPath).Length / 1MB, 1)
$symbolSizeMb = [Math]::Round((Get-Item -LiteralPath $appxsymPath).Length / 1MB, 1)
$symbolHash = (Get-FileHash -LiteralPath $appxsymPath -Algorithm SHA256).Hash
$uploadSizeMb = [Math]::Round((Get-Item -LiteralPath $msixUploadPath).Length / 1MB, 1)
$uploadHash = (Get-FileHash -LiteralPath $msixUploadPath -Algorithm SHA256).Hash

Write-Host ""
Write-Host "MSIX 已生成"
Write-Host "  路径：$msixPath"
Write-Host "  大小：$sizeMb MB"
Write-Host "  SHA256：$hash"
Write-Host "  符号包：$appxsymPath"
Write-Host "  符号包大小：$symbolSizeMb MB"
Write-Host "  符号包 SHA256：$symbolHash"
Write-Host "  商店上传包：$msixUploadPath"
Write-Host "  商店上传包大小：$uploadSizeMb MB"
Write-Host "  商店上传包 SHA256：$uploadHash"
Write-Host "  Identity：$IdentityName"
Write-Host "  Publisher：$Publisher"
Write-Host "  Version：$packageVersion"
Write-Host "  应用版本：$Version"
Write-Host "  已签名：$signed"
if ($usingPlaceholderIdentity) {
    Write-Host ""
    Write-Host "警告：当前使用占位身份，只能本机验证；提交商店前必须换成 Partner Center 预留值"
}
if ($certificateThumbprint) {
    Write-Host "  测试证书指纹：$certificateThumbprint"
    Write-Host "  测试证书：$certificateCerPath"
    Write-Host "  测试 PFX：$certificatePfxPath"
    if (-not $SuppressCertificateDetails) {
        Write-Host "  测试 PFX 密码：$testCertificatePassword"
    }
    if (-not $TrustCertificate) {
        Write-Host "  本机安装前需要把该证书导入 当前用户\受信任人，或重新运行并加 -TrustCertificate"
    }
}
if (-not $Sign) {
    Write-Host "  商店提交可直接上传；Microsoft 会重新签名。若上传校验要求签名，重新运行并加 -Sign"
}
