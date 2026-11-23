param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

. (Join-Path $PSScriptRoot "Branding.Common.ps1")

function Get-ExternalProfilesRoot {
    $root = ($env:BRAND_PROFILE_ROOT ?? "").Trim()
    if (-not [string]::IsNullOrWhiteSpace($root)) {
        return [System.IO.Path]::GetFullPath($root)
    }

    $localPropsPath = Join-Path $RepoRoot "branding\profiles.local.props"
    if (-not (Test-Path -LiteralPath $localPropsPath -PathType Leaf)) {
        return ""
    }
    [xml]$localProps = [System.IO.File]::ReadAllText(
        $localPropsPath,
        [System.Text.Encoding]::UTF8)
    $node = $localProps.SelectSingleNode("/Project/PropertyGroup/BrandExternalProfilesRoot")
    $value = if ($null -eq $node) { "" } else { $node.InnerText.Trim() }
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ""
    }
    return [System.IO.Path]::GetFullPath($value)
}

function Get-ComparableIdentityValues {
    param($Profile)

    $values = @(
        $Profile.ProfileId,
        $Profile.ProductName,
        $Profile.DisplayName,
        $Profile.WindowTitle,
        $Profile.ArtifactPrefix,
        $Profile.WebsiteUrl,
        $Profile.SupportUrl,
        $Profile.UpdatesUrl,
        $Profile.RepositorySlug,
        $Profile.CompatibilityId,
        $Profile.DataDirectoryName,
        $Profile.InstanceNamePrefix,
        $Profile.InstallDirectoryName,
        $Profile.AppUserModelId,
        $Profile.SetupAppId,
        $Profile.StoreIdentityName,
        $Profile.StorePublisher,
        $Profile.StorePublisherDisplayName,
        $Profile.StoreShortName,
        $Profile.PackageEditionMarker,
        $Profile.MacBundleId,
        $Profile.SupportEmail,
        $Profile.PrivacyPolicyUrl,
        $Profile.LicenseUrl,
        $Profile.UpdateGiteeCheckUrl,
        $Profile.UpdateGithubCheckUrl,
        $Profile.ExtensionGiteeRegistryBase,
        $Profile.ExtensionGithubRegistryBase,
        $Profile.ExtensionMarketPublicKeyId,
        $Profile.ExtensionMarketPublicKeySpki
    )
    $values += @($Profile.LocalizedValues | ForEach-Object { $_ })
    $values |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { ([string]$_).Trim() }
}

$externalRoot = Get-ExternalProfilesRoot
if ([string]::IsNullOrWhiteSpace($externalRoot) -or
    -not (Test-Path -LiteralPath $externalRoot -PathType Container)) {
    Write-Host "未配置外部档案目录，跳过私有身份泄漏扫描"
    exit 0
}

$publicValues = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$publicProfileDirectories = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "branding\profiles") -Directory)
foreach ($publicProfileDirectory in $publicProfileDirectories) {
    if (-not (Test-Path -LiteralPath (Join-Path $publicProfileDirectory.FullName "Brand.props") -PathType Leaf)) {
        continue
    }
    try {
        $publicProfile = Get-BrandProfile `
            -RepoRoot $RepoRoot `
            -BrandProfile $publicProfileDirectory.Name
        foreach ($value in Get-ComparableIdentityValues -Profile $publicProfile) {
            [void]$publicValues.Add($value)
        }
    }
    catch {
        Write-Warning "无法读取公共档案 $($publicProfileDirectory.Name)：$($_.Exception.Message)"
    }
}
$profileDirectories = @(Get-ChildItem -LiteralPath $externalRoot -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "Brand.props") -PathType Leaf })
if ($profileDirectories.Count -eq 0) {
    Write-Host "外部档案目录中没有可扫描的档案"
    exit 0
}

$trackedPaths = @(& git -C $RepoRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw "无法读取已跟踪文件列表"
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($profileDirectory in $profileDirectories) {
    $profile = Get-BrandProfile `
        -RepoRoot $RepoRoot `
        -BrandProfile $profileDirectory.Name `
        -BrandProfileDirectory $profileDirectory.FullName
    if ($profile.SelectedProfileName -match '^(demo|example|sample|template|software-template)$') {
        continue
    }
    $terms = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($value in Get-ComparableIdentityValues -Profile $profile) {
        if ($value.Length -lt 3 -or $publicValues.Contains($value)) {
            continue
        }
        if ($value -match '(?i)example\.(com|invalid)') {
            continue
        }
        [void]$terms.Add($value)
    }

    foreach ($term in $terms) {
        foreach ($trackedPath in $trackedPaths) {
            if ($trackedPath.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $violations.Add("$($profile.ProfileId): 文件路径 $trackedPath")
            }
        }
        $matches = @(& git -C $RepoRoot grep -n -I -i -w -F -e $term -- . 2>$null)
        if ($LASTEXITCODE -eq 0 -and $matches.Count -gt 0) {
            foreach ($match in $matches) {
                $violations.Add("$($profile.ProfileId): $match")
            }
        }
        elseif ($LASTEXITCODE -notin @(0, 1)) {
            throw "扫描已跟踪文件失败：$term"
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "发现外部定制档案身份进入公共仓库：" -ForegroundColor Red
    $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "未发现外部定制档案身份泄漏"
