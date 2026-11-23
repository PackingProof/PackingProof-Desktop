function Get-BrandProfileContentSha256 {
    param([Parameter(Mandatory = $true)][string]$ProfileDirectory)

    $root = [System.IO.Path]::GetFullPath($ProfileDirectory)
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.Name -ne "launcher-baseline.json" } |
        Sort-Object -Property FullName)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($file in $files) {
            $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            foreach ($value in @($relative, [Convert]::ToHexString(
                        [System.Security.Cryptography.SHA256]::HashData(
                            [System.IO.File]::ReadAllBytes($file.FullName))))) {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($value)
                $sha.TransformBlock($bytes, 0, $bytes.Length, $null, 0) | Out-Null
                $separator = [byte[]](0)
                $sha.TransformBlock($separator, 0, 1, $null, 0) | Out-Null
            }
        }
        $sha.TransformFinalBlock([byte[]]::new(0), 0, 0) | Out-Null
        return [Convert]::ToHexString($sha.Hash).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Invoke-BrandFeaturesValidator {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ProfileDirectory,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string]$MobileReleaseUrl = ""
    )

    $projectDirectory = Join-Path $RepoRoot "tools\ExpressPackingMonitoring.BrandFeatures.Validator"
    $projectPath = Join-Path $projectDirectory "ExpressPackingMonitoring.BrandFeatures.Validator.csproj"
    $validatorArguments = @(
        "--profile-directory", $ProfileDirectory,
        "--relative-path", $RelativePath,
        "--mobile-release-url", $MobileReleaseUrl
    )
    $validatorOutput = & dotnet run --project $projectPath --configuration Debug --no-launch-profile `
        -p:SkipBrandFeatureValidation=true -- @validatorArguments 2>&1
    $validatorOutput | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "软件能力配置校验失败：$ProfileDirectory\$RelativePath"
    }
}

function Get-BrandProfile {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$BrandProfile = "",
        [string]$BrandProfileDirectory = ""
    )

    if ([string]::IsNullOrWhiteSpace($BrandProfile)) {
        $BrandProfile = ($env:BRAND_PROFILE ?? "").Trim()
    }
    if ([string]::IsNullOrWhiteSpace($BrandProfile)) {
        $selectorPath = Join-Path $RepoRoot "branding\Brand.props"
        [xml]$selector = [System.IO.File]::ReadAllText($selectorPath, [System.Text.Encoding]::UTF8)
        $defaultProfile = $selector.SelectSingleNode('/Project/PropertyGroup/BrandProfile')
        $BrandProfile = if ($null -ne $defaultProfile) { $defaultProfile.InnerText.Trim() } else { "" }
        if ($BrandProfile.StartsWith('$(', [System.StringComparison]::Ordinal)) {
            $BrandProfile = ""
        }
    }
    if ([string]::IsNullOrWhiteSpace($BrandProfile)) {
        $BrandProfile = "packingproof"
    }

    $profileDirectory = $BrandProfileDirectory.Trim()
    if ([string]::IsNullOrWhiteSpace($profileDirectory)) {
        $profileDirectory = ($env:BRAND_PROFILE_DIR ?? "").Trim()
    }
    if ([string]::IsNullOrWhiteSpace($profileDirectory)) {
        $externalRoot = ($env:BRAND_PROFILE_ROOT ?? "").Trim()
        if ([string]::IsNullOrWhiteSpace($externalRoot)) {
            $localPropsPath = Join-Path $RepoRoot "branding\profiles.local.props"
            if (Test-Path -LiteralPath $localPropsPath -PathType Leaf) {
                [xml]$localProps = [System.IO.File]::ReadAllText(
                    $localPropsPath,
                    [System.Text.Encoding]::UTF8)
                $externalRootNode = $localProps.SelectSingleNode(
                    '/Project/PropertyGroup/BrandExternalProfilesRoot')
                if ($null -ne $externalRootNode) {
                    $externalRoot = $externalRootNode.InnerText.Trim()
                }
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($externalRoot)) {
            if (-not [System.IO.Path]::IsPathRooted($externalRoot)) {
                throw "BrandExternalProfilesRoot 必须是绝对路径：$externalRoot"
            }
            $externalProfileDirectory = Join-Path $externalRoot $BrandProfile
            $repositoryProfileDirectory = Join-Path $RepoRoot "branding\profiles\$BrandProfile"
            $profileDirectory = if ((Test-Path -LiteralPath (Join-Path $externalProfileDirectory "Brand.props") -PathType Leaf) -or
                -not (Test-Path -LiteralPath (Join-Path $repositoryProfileDirectory "Brand.props") -PathType Leaf)) {
                $externalProfileDirectory
            }
            else {
                $repositoryProfileDirectory
            }
        }
        else {
            $profileDirectory = Join-Path $RepoRoot "branding\profiles\$BrandProfile"
        }
    }

    if (-not [System.IO.Path]::IsPathRooted($profileDirectory)) {
        $profileDirectory = Join-Path $RepoRoot $profileDirectory
    }
    $profileDirectory = [System.IO.Path]::GetFullPath($profileDirectory)
    $profilePath = Join-Path $profileDirectory "Brand.props"
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
        throw "找不到软件配置 '$BrandProfile'：$profilePath。可设置 BRAND_PROFILE_ROOT、BRAND_PROFILE_DIR，或创建 branding/profiles.local.props。"
    }

    [xml]$document = [System.IO.File]::ReadAllText($profilePath, [System.Text.Encoding]::UTF8)
    $properties = @{}
    foreach ($group in @($document.Project.PropertyGroup)) {
        foreach ($property in $group.ChildNodes) {
            if ($property.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                $properties[$property.Name] = [string]$property.InnerText
            }
        }
    }
    $selectedProfileName = Split-Path -Leaf $profileDirectory
    $declaredProfileId = ([string]$properties["BrandProfileId"]).Trim()
    if (-not [string]::Equals(
            $declaredProfileId,
            $selectedProfileName,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "软件配置 '$selectedProfileName' 的 BrandProfileId 必须与配置目录名一致；当前为 '$declaredProfileId'"
    }

    $mobileAppEnabledText = [string]$properties["BrandMobileAppEnabled"]
    if ([string]::IsNullOrWhiteSpace($mobileAppEnabledText)) {
        $mobileAppEnabledText = "true"
    }
    if ($mobileAppEnabledText -notin @("true", "false")) {
        throw "软件配置 '$BrandProfile' 的 BrandMobileAppEnabled 只能是 true 或 false"
    }

    $required = @(
        "BrandProfileId",
        "BrandProductName",
        "BrandArtifactPrefix",
        "BrandWindowTitle",
        "BrandDisplayName",
        "BrandDescription",
        "BrandWebsiteUrl",
        "BrandSupportUrl",
        "BrandUpdatesUrl",
        "BrandPrivacyPolicyUrl",
        "BrandLicenseUrl",
        "BrandSupportEmail",
        "BrandAppIconRelativePath",
        "BrandUpdateGiteeCheckUrl",
        "BrandUpdateGithubCheckUrl",
        "BrandPatchDownloadBaseUrl",
        "BrandExtensionGiteeRegistryBase",
        "BrandExtensionGithubRegistryBase",
        "BrandExtensionMarketPublicKeyId",
        "BrandExtensionMarketPublicKeySpki",
        "BrandExtensionMarketPublicKeyIsPlaceholder",
        "BrandRepositorySlug",
        "BrandDataDirectoryName",
        "BrandInstanceNamePrefix",
        "BrandInstallDirectoryName",
        "BrandAppUserModelId",
        "BrandSetupAppId",
        "BrandStoreIdentityName",
        "BrandStorePublisher",
        "BrandStorePublisherDisplayName",
        "BrandStoreShortName",
        "BrandStoreStartupTaskId",
        "BrandPackageEditionMarker",
        "BrandLauncherBaselineRelativePath",
        "BrandWebServerPort",
        "BrandDiscoveryPort",
        "BrandMacBundleId"
        "BrandFeaturesRelativePath"
    )
    if ($mobileAppEnabledText -eq "true") {
        $required += @(
            "BrandMobileRepositoryUrl",
            "BrandMobileReleasesUrl",
            "BrandMobileLatestReleaseApiUrl",
            "BrandMobileReleasesApiUrl"
        )
    }
    foreach ($name in $required) {
        if ([string]::IsNullOrWhiteSpace([string]$properties[$name])) {
            throw "软件配置 '$BrandProfile' 缺少 $name"
        }
    }

    function Resolve-ProfileScopedFile {
        param(
            [Parameter(Mandatory = $true)][string]$Value,
            [Parameter(Mandatory = $true)][string]$FieldName
        )

        if ([System.IO.Path]::IsPathRooted($Value)) {
            throw "软件配置 '$BrandProfile' 的 $FieldName 必须是配置目录内的相对路径"
        }
        $root = [System.IO.Path]::TrimEndingDirectorySeparator(
            [System.IO.Path]::GetFullPath($profileDirectory))
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $root $Value))
        if (-not $resolved.StartsWith(
                $root + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "软件配置 '$BrandProfile' 的 $FieldName 不能越出配置目录"
        }
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "软件配置 '$BrandProfile' 的 $FieldName 指向的文件不存在：$Value"
        }
        return $resolved
    }

    function Resolve-ProfileFile {
        param(
            [Parameter(Mandatory = $true)][string]$Value,
            [Parameter(Mandatory = $true)][string]$FieldName
        )

        if ([string]::IsNullOrWhiteSpace($Value)) {
            throw "软件配置 '$BrandProfile' 缺少 $FieldName"
        }
        if ([System.IO.Path]::IsPathRooted($Value)) {
            $resolved = [System.IO.Path]::GetFullPath($Value)
        }
        else {
            $profileRelative = [System.IO.Path]::GetFullPath((Join-Path $profileDirectory $Value))
            $repositoryRelative = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Value))
            $resolved = if (Test-Path -LiteralPath $profileRelative -PathType Leaf) {
                $profileRelative
            }
            elseif (Test-Path -LiteralPath $repositoryRelative -PathType Leaf) {
                $repositoryRelative
            }
            else {
                throw "软件配置 '$BrandProfile' 的 $FieldName 指向的文件不存在：$Value"
            }
        }
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "软件配置 '$BrandProfile' 的 $FieldName 指向的文件不存在：$resolved"
        }
        return $resolved
    }

    $featuresPath = Resolve-ProfileScopedFile `
        -Value $properties["BrandFeaturesRelativePath"] `
        -FieldName "BrandFeaturesRelativePath"

    $profile = [pscustomobject]@{
        ProfilePath = $profilePath
        ProfileDirectory = $profileDirectory
        ContentSha256 = Get-BrandProfileContentSha256 -ProfileDirectory $profileDirectory
        ProfileId = $properties["BrandProfileId"]
        SelectedProfileName = $selectedProfileName
        ProductName = $properties["BrandProductName"]
        ArtifactPrefix = $properties["BrandArtifactPrefix"]
        WindowTitle = $properties["BrandWindowTitle"]
        DisplayName = $properties["BrandDisplayName"]
        Description = $properties["BrandDescription"]
        FeaturesRelativePath = $properties["BrandFeaturesRelativePath"]
        FeaturesPath = $featuresPath
        WebsiteUrl = $properties["BrandWebsiteUrl"]
        SupportUrl = $properties["BrandSupportUrl"]
        UpdatesUrl = $properties["BrandUpdatesUrl"]
        PrivacyPolicyUrl = $properties["BrandPrivacyPolicyUrl"]
        AppIconRelativePath = $properties["BrandAppIconRelativePath"]
        AppIconPath = Resolve-ProfileFile -Value $properties["BrandAppIconRelativePath"] -FieldName "BrandAppIconRelativePath"
        LicenseUrl = $properties["BrandLicenseUrl"]
        SupportEmail = $properties["BrandSupportEmail"]
        UpdateGiteeCheckUrl = $properties["BrandUpdateGiteeCheckUrl"]
        UpdateGithubCheckUrl = $properties["BrandUpdateGithubCheckUrl"]
        GitHubReleaseTokenFile = $properties["BrandGitHubReleaseTokenFile"]
        GiteeReleaseTokenFile = $properties["BrandGiteeReleaseTokenFile"]
        GiteeReleaseUsername = if ([string]::IsNullOrWhiteSpace($properties["BrandGiteeReleaseUsername"])) {
            "PackingProof-Release-Bot"
        }
        else {
            $properties["BrandGiteeReleaseUsername"]
        }
        PatchDownloadBaseUrl = $properties["BrandPatchDownloadBaseUrl"]
        MobileRepositoryUrl = $properties["BrandMobileRepositoryUrl"]
        MobileReleasesUrl = $properties["BrandMobileReleasesUrl"]
        MobileLatestReleaseApiUrl = $properties["BrandMobileLatestReleaseApiUrl"]
        MobileReleasesApiUrl = $properties["BrandMobileReleasesApiUrl"]
        MobileAppEnabled = [string]::Equals(
            $mobileAppEnabledText,
            "true",
            [System.StringComparison]::OrdinalIgnoreCase)
        ExtensionGiteeRegistryBase = $properties["BrandExtensionGiteeRegistryBase"]
        ExtensionGithubRegistryBase = $properties["BrandExtensionGithubRegistryBase"]
        ExtensionMarketPublicKeyId = $properties["BrandExtensionMarketPublicKeyId"]
        ExtensionMarketPublicKeySpki = $properties["BrandExtensionMarketPublicKeySpki"]
        ExtensionMarketPublicKeyIsPlaceholder = [string]::Equals(
            $properties["BrandExtensionMarketPublicKeyIsPlaceholder"],
            "true",
            [System.StringComparison]::OrdinalIgnoreCase)
        RepositorySlug = $properties["BrandRepositorySlug"]
        CompatibilityId = $properties["BrandCompatibilityId"]
        DataDirectoryName = $properties["BrandDataDirectoryName"]
        InstanceNamePrefix = $properties["BrandInstanceNamePrefix"]
        InstallDirectoryName = $properties["BrandInstallDirectoryName"]
        AppUserModelId = $properties["BrandAppUserModelId"]
        SetupAppId = $properties["BrandSetupAppId"]
        StoreIdentityName = $properties["BrandStoreIdentityName"]
        StorePublisher = $properties["BrandStorePublisher"]
        StorePublisherDisplayName = $properties["BrandStorePublisherDisplayName"]
        StoreShortName = $properties["BrandStoreShortName"]
        StoreStartupTaskId = $properties["BrandStoreStartupTaskId"]
        PackageEditionMarker = $properties["BrandPackageEditionMarker"]
        LauncherBaselineRelativePath = $properties["BrandLauncherBaselineRelativePath"]
        LauncherBaselinePath = Resolve-ProfileFile -Value $properties["BrandLauncherBaselineRelativePath"] -FieldName "BrandLauncherBaselineRelativePath"
        WebServerPort = [int]$properties["BrandWebServerPort"]
        DiscoveryPort = [int]$properties["BrandDiscoveryPort"]
        MacBundleId = $properties["BrandMacBundleId"]
        LocalizedValues = @()
        LocalizationRelativePath = $properties["BrandLocalizationRelativePath"]
        LocalizationPath = $null
    }

    $localizationFile = $properties["BrandLocalizationRelativePath"]
    if (-not [string]::IsNullOrWhiteSpace($localizationFile)) {
        $localizationPath = Resolve-ProfileFile -Value $localizationFile -FieldName "BrandLocalizationFile"
        $profile.LocalizationPath = $localizationPath
        $catalog = Get-Content -LiteralPath $localizationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $profile.LocalizedValues = @($catalog.Locales.PSObject.Properties |
            ForEach-Object {
                @(
                    $_.Value.ProductName,
                    $_.Value.DisplayName,
                    $_.Value.WindowTitle,
                    $_.Value.Description
                ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
            })
    }

    if ($profile.ArtifactPrefix -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw "软件配置 '$BrandProfile' 的 BrandArtifactPrefix 只能包含字母、数字、点、下划线和连字符"
    }
    foreach ($pathField in @(
            $profile.DataDirectoryName,
            $profile.InstanceNamePrefix,
            $profile.InstallDirectoryName,
            $profile.PackageEditionMarker)) {
        if ([System.IO.Path]::GetFileName($pathField) -ne $pathField -or
            $pathField.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "软件配置 '$BrandProfile' 的数据目录、实例名和安装目录必须是安全的单段名称"
        }
    }
    foreach ($port in @($profile.WebServerPort, $profile.DiscoveryPort)) {
        if ($port -lt 1 -or $port -gt 65535) {
            throw "软件配置 '$BrandProfile' 的 Web/发现端口必须有效"
        }
    }
    if ($profile.WebServerPort -eq $profile.DiscoveryPort) {
        throw "软件配置 '$BrandProfile' 的 Web/发现端口必须互不相同"
    }
    if ($profile.ProfileId -ne "packingproof" -and (
            $profile.DataDirectoryName -eq "ExpressPackingMonitoring" -or
            $profile.InstanceNamePrefix -eq "ExpressPackingMonitoring" -or
            $profile.InstallDirectoryName -eq "ExpressPackingMonitoring" -or
            $profile.SetupAppId -eq "99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05" -or
            $profile.StoreIdentityName -eq "PackingProof.PackingProof" -or
            $profile.ProductName -eq "PackingProof" -or
            $profile.ArtifactPrefix -eq "PackingProof" -or
            $profile.CompatibilityId -eq "PackingProof" -or
            $profile.AppUserModelId -eq "PackingProof.ExpressPackingMonitoring" -or
            $profile.MacBundleId -eq "com.packingproof.host" -or
            $profile.WebServerPort -eq 5280 -or
            $profile.DiscoveryPort -eq 5281)) {
        throw "非默认软件配置必须使用独立的安装身份、数据目录、实例名和 Web/发现端口"
    }
    $validatorProject = Join-Path $RepoRoot "tools\ExpressPackingMonitoring.BrandFeatures.Validator\ExpressPackingMonitoring.BrandFeatures.Validator.csproj"
    if (Test-Path -LiteralPath $validatorProject -PathType Leaf) {
        Invoke-BrandFeaturesValidator `
            -RepoRoot $RepoRoot `
            -ProfileDirectory $profileDirectory `
            -RelativePath $profile.FeaturesRelativePath `
            -MobileReleaseUrl $profile.MobileReleasesUrl
    }
    $profile | Add-Member `
        -NotePropertyName Features `
        -NotePropertyValue (Get-Content -LiteralPath $featuresPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    return $profile
}
