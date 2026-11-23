using ExpressPackingMonitoring.UpdateCore;
using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BrandProfileTests
{
    [Fact]
    public void PublicPackingProofKeepsPhotoCapabilityButHidesTheEntryByDefault()
    {
        Assert.True(BrandMetadata.ProductFeatures.PhotoCapture);
        Assert.False(new ExpressPackingMonitoring.Config.AppConfig().ShowPhotoCaptureButton);
    }

    [Fact]
    public void DefaultProfileMetadataMatchesSelectedProfile()
    {
        IReadOnlyDictionary<string, string> values = ReadProfile(BrandMetadata.ProfileId);

        Assert.Equal(Value(values, "BrandProfileId"), BrandMetadata.ProfileId);
        Assert.Equal(Value(values, "BrandProductName"), BrandMetadata.ProductName);
        Assert.Equal(Value(values, "BrandWindowTitle"), BrandMetadata.WindowTitle);
        Assert.Equal(Value(values, "BrandDisplayName"), BrandMetadata.DisplayName);
        Assert.Equal(Value(values, "BrandDataDirectoryName"), BrandMetadata.DataDirectoryName);
        Assert.Equal(Value(values, "BrandInstanceNamePrefix"), BrandMetadata.InstanceNamePrefix);
        Assert.Equal(Value(values, "BrandAppUserModelId"), BrandMetadata.AppUserModelId);
        Assert.Equal(Value(values, "BrandExtensionMarketPublicKeyId"), BrandMetadata.ExtensionMarketPublicKeyId);
        Assert.Equal(Value(values, "BrandExtensionMarketPublicKeySpki"), BrandMetadata.ExtensionMarketPublicKeySpki);
        Assert.Equal(Value(values, "BrandMobileReleasesUrl"), BrandMetadata.MobileReleasesUrl);
        Assert.Equal(int.Parse(Value(values, "BrandWebServerPort")), BrandMetadata.WebServerPort);
        Assert.Equal(int.Parse(Value(values, "BrandDiscoveryPort")), BrandMetadata.DiscoveryPort);
        Assert.Equal(Value(values, "BrandUpdateGiteeCheckUrl"), BrandMetadata.UpdateGiteeCheckUrl);
        Assert.Equal(Value(values, "BrandUpdateGithubCheckUrl"), BrandMetadata.UpdateGithubCheckUrl);
        Assert.Equal(
            !values.TryGetValue("BrandMobileAppEnabled", out string? mobileEnabled)
                || string.IsNullOrWhiteSpace(mobileEnabled)
                || bool.Parse(mobileEnabled),
            BrandMetadata.ProductFeatures.MobileApp);
        Assert.Equal(BrandMetadata.UpdateGiteeCheckUrl, UpdateEndpointPolicy.DefaultGiteeCheckUrl);
        Assert.Equal(BrandMetadata.UpdateGithubCheckUrl, UpdateEndpointPolicy.DefaultGithubCheckUrl);
    }

    [Fact]
    public void SoftwareTemplateDefinesIndependentParallelInstallIdentityAndUpdateChannel()
    {
        IReadOnlyDictionary<string, string> packingProof = ReadProfile("packingproof");
        IReadOnlyDictionary<string, string> software = ReadProfile("software-template");

        Assert.True(bool.Parse(Value(software, "BrandExtensionMarketPublicKeyIsPlaceholder")));
        Assert.False(bool.Parse(Value(packingProof, "BrandExtensionMarketPublicKeyIsPlaceholder")));
        Assert.NotEqual(Value(packingProof, "BrandDataDirectoryName"), Value(software, "BrandDataDirectoryName"));
        Assert.NotEqual(Value(packingProof, "BrandInstanceNamePrefix"), Value(software, "BrandInstanceNamePrefix"));
        Assert.NotEqual(Value(packingProof, "BrandInstallDirectoryName"), Value(software, "BrandInstallDirectoryName"));
        Assert.NotEqual(Value(packingProof, "BrandSetupAppId"), Value(software, "BrandSetupAppId"));
        Assert.NotEqual(Value(packingProof, "BrandStoreIdentityName"), Value(software, "BrandStoreIdentityName"));
        Assert.NotEqual(Value(packingProof, "BrandUpdateGiteeCheckUrl"), Value(software, "BrandUpdateGiteeCheckUrl"));
        Assert.NotEqual(Value(packingProof, "BrandUpdateGithubCheckUrl"), Value(software, "BrandUpdateGithubCheckUrl"));
        Assert.NotEqual(Value(packingProof, "BrandPatchDownloadBaseUrl"), Value(software, "BrandPatchDownloadBaseUrl"));
        Assert.NotEqual(Value(packingProof, "BrandPrivacyPolicyUrl"), Value(software, "BrandPrivacyPolicyUrl"));
        Assert.NotEqual(Value(packingProof, "BrandLicenseUrl"), Value(software, "BrandLicenseUrl"));
        Assert.NotEqual(Value(packingProof, "BrandWebServerPort"), Value(software, "BrandWebServerPort"));
        Assert.NotEqual(Value(packingProof, "BrandDiscoveryPort"), Value(software, "BrandDiscoveryPort"));
        Assert.NotEqual(Value(packingProof, "BrandMacBundleId"), Value(software, "BrandMacBundleId"));
    }

    [Fact]
    public void BrandAssetsAndRuntimeEntryPointsUseTheSelectedProfile()
    {
        string root = FindRepositoryRoot();
        string packingProofIcon = Path.Combine(
            root, "branding", "profiles", "packingproof", "app.ico");
        string packingProofLogo = Path.Combine(
            root, "branding", "profiles", "packingproof", "logo.png");
        Assert.True(new FileInfo(packingProofIcon).Length > 0);
        Assert.True(new FileInfo(packingProofLogo).Length > 0);

        string mainProject = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"));
        string startupConfig = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Config", "EarlyStartupConfig.cs"));
        string instanceCoordinator = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Workstations", "WorkstationInstanceCoordinator.cs"));
        string taskbarIdentity = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Services", "TaskbarIdentityService.cs"));
        string appConfig = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Config", "AppConfig.cs"));
        string webPage = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Web", "index.html"));
        string extensionMarket = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Services", "Extensions", "ExtensionMarketClient.cs"));
        string mobileConnection = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "UI", "MobileConnectionWindow.xaml.cs"));
        string mobileUpdateProvider = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Services", "MobileAppUpdatePolicyProvider.cs"));
        string mainSettings = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "ViewModels", "MainViewModel.Settings.cs"));

        Assert.Contains("<ApplicationIcon>$(BrandAppIcon)</ApplicationIcon>", mainProject, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.DataDirectoryName", startupConfig, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.InstanceNamePrefix", instanceCoordinator, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.AppUserModelId", taskbarIdentity, StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(BrandMetadata.ProfileId, \"packingproof\"",
            appConfig,
            StringComparison.Ordinal);
        Assert.Contains("__PACKINGPROOF_BRANDING_JSON__", webPage, StringComparison.Ordinal);
        Assert.Contains("function brandValue(value)", webPage, StringComparison.Ordinal);
        Assert.Contains("mobileReleasesUrl", webPage, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.ExtensionMarketPublicKeyId", extensionMarket, StringComparison.Ordinal);
        Assert.Contains("ImportSubjectPublicKeyInfo", extensionMarket, StringComparison.Ordinal);
        Assert.Contains("ProductCapabilityRuntime.CurrentFeatures.MobileApp", mobileConnection, StringComparison.Ordinal);
        Assert.Contains("ProductCapabilityRuntime.CurrentFeatures.MobileApp", mobileUpdateProvider, StringComparison.Ordinal);
        Assert.Contains("ProductCapabilityRuntime.CurrentFeatures.MobileApp", mainSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void MsBuildPropertyFunctionsDoNotCallUnsupportedPathApis()
    {
        string mainProject = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"));

        Assert.DoesNotContain(
            "[System.IO.Path]::TrimEndingDirectorySeparator",
            mainProject,
            StringComparison.Ordinal);
        Assert.Contains(
            ".TrimEnd('\\')",
            mainProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrandReplacementChangesVisibleBrandTextOnly()
    {
        string Apply(string value) => BrandText.Apply(
            value,
            "PackingProof",
            "Contoso Packing",
            "Contoso Packing 快递打包监控",
            "Contoso Packing 包裹留证",
            "ContosoPacking");

        Assert.Equal("Contoso Packing 主机", Apply("PackingProof 主机"));
        Assert.Equal("Contoso Packing.", Apply("PackingProof."));
        Assert.Equal("ContosoPackingVideos目录", Apply("PackingProofVideos目录"));
        Assert.Equal(
            "Contoso Packing 快递打包监控",
            Apply("PackingProof 快递打包监控"));
        Assert.Equal(
            "Contoso Packing 包裹留证",
            Apply("PackingProof 包裹留证"));

        foreach (string unchanged in new[]
                 {
                     "https://example.com/PackingProofVideos/file",
                     "https://example.com/?brand=PackingProof&x=1",
                     "https://example.com/#PackingProof",
                     "PackingProof@outlook.com",
                     "PackingProof:8080",
                     "BRAND=PackingProof",
                     "X-PackingProof-Extension-Version",
                     "PackingProof.exe",
                     @"\\server\PackingProofVideos"
                 })
        {
            Assert.Equal(unchanged, Apply(unchanged));
        }
    }

    [Fact]
    public void InstallerRuntimeDetectionUsesTheSelectedBrandIdentity()
    {
        string installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools", "Installer", "ExpressPackingMonitoring.iss"));

        Assert.Contains("Local\\{#MyBrandInstanceNamePrefix}.Portable.Mutex", installer, StringComparison.Ordinal);
        Assert.Contains("AppMutex=Local\\{#MyBrandInstanceNamePrefix}.Portable.Mutex", installer, StringComparison.Ordinal);
        Assert.Contains("'{#MyBrandCompatibilityId}' = 'PackingProof'", installer, StringComparison.Ordinal);
        Assert.Contains("Uppercase('{#MyBrandStoreIdentityName}')", installer, StringComparison.Ordinal);
        Assert.Contains("Uninstall\\{#MyAppId}_is1", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentSoftwarePatchAndReleasePathsCarryTheSelectedContract()
    {
        string root = FindRepositoryRoot();
        string appPatch = File.ReadAllText(Path.Combine(root, "tools", "Apply-AppPatch.ps1"));
        string launcherPatch = File.ReadAllText(Path.Combine(root, "tools", "Apply-LauncherPatch.ps1"));
        string publisher = File.ReadAllText(Path.Combine(root, "tools", "Publish-CleanPackage.ps1"));
        string launcherPublisher = File.ReadAllText(Path.Combine(root, "tools", "Publish-LauncherBaseline.ps1"));
        string noRuntimePublisher = File.ReadAllText(Path.Combine(root, "tools", "Publish-NoRuntimePackage.ps1"));
        string macHost = File.ReadAllText(Path.Combine(root, "tools", "Publish-MacHost.sh"));
        string launcherCommon = File.ReadAllText(Path.Combine(root, "tools", "LauncherBaseline.Common.ps1"));
        string githubAuth = File.ReadAllText(Path.Combine(root, "tools", "ReleaseAuth.Common.ps1"));
        string giteeAuth = File.ReadAllText(Path.Combine(root, "tools", "GiteeAuth.Common.ps1"));
        string privateLeak = File.ReadAllText(Path.Combine(root, "tools", "Test-PrivateBrandLeak.ps1"));
        string scanner = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "ViewModels", "MainViewModel.Scanner.cs"));

        Assert.Contains("Get-PatchBrandContract", appPatch, StringComparison.Ordinal);
        Assert.Contains("IsLegacy = $true", appPatch, StringComparison.Ordinal);
        Assert.Contains("$patchBrand.DataDirectoryName", appPatch, StringComparison.Ordinal);
        Assert.Contains("Test-TargetBrandProduct", appPatch, StringComparison.Ordinal);
        Assert.Contains("Get-PatchBrandContract", launcherPatch, StringComparison.Ordinal);
        Assert.Contains("IsLegacy = $true", launcherPatch, StringComparison.Ordinal);
        Assert.Contains("Test-TargetBrandProduct", launcherPatch, StringComparison.Ordinal);
        Assert.Contains("$patchManifest[\"brand\"]", publisher, StringComparison.Ordinal);
        Assert.Contains("AllowPlaceholderBrandSecrets", publisher, StringComparison.Ordinal);
        Assert.Contains("ExtensionMarketPublicKeyIsPlaceholder", publisher, StringComparison.Ordinal);
        Assert.Contains("$cachedBaselineRoot", publisher, StringComparison.Ordinal);
        Assert.Contains("brand = [ordered]@{", launcherPublisher, StringComparison.Ordinal);
        Assert.Contains("$brand.ArtifactPrefix", noRuntimePublisher, StringComparison.Ordinal);
        Assert.Contains("BrandAppIconRelativePath", macHost, StringComparison.Ordinal);
        Assert.Contains("Get-LauncherBaselineTag", launcherCommon, StringComparison.Ordinal);
        Assert.Contains("launcher-$ArtifactPrefix-v$normalizedVersion", launcherCommon, StringComparison.Ordinal);
        Assert.Contains("\"Branding\\Brand.props\"", launcherCommon, StringComparison.Ordinal);
        Assert.Contains("brand_profile_sha256=", launcherCommon, StringComparison.Ordinal);
        Assert.DoesNotContain("Where-Object { $_.Name -notin", launcherCommon, StringComparison.Ordinal);
        Assert.Contains(
            "非默认软件配置必须配置 BrandGitHubReleaseTokenFile",
            githubAuth,
            StringComparison.Ordinal);
        Assert.Contains(
            "非默认软件配置必须配置 BrandGiteeReleaseTokenFile",
            giteeAuth,
            StringComparison.Ordinal);
        Assert.Contains("$Profile.SetupAppId", privateLeak, StringComparison.Ordinal);
        Assert.Contains("$Profile.UpdateGiteeCheckUrl", privateLeak, StringComparison.Ordinal);
        Assert.Contains("$Profile.LocalizedValues", privateLeak, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.PackageEditionMarker", scanner, StringComparison.Ordinal);
        Assert.DoesNotContain("PackingProof_Setup_no-runtime_", noRuntimePublisher, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalProfileRootAndResolvedAssetPathsAreWiredThroughReleaseScripts()
    {
        string root = FindRepositoryRoot();
        string selector = File.ReadAllText(Path.Combine(root, "branding", "Brand.props"));
        string branding = File.ReadAllText(Path.Combine(root, "tools", "Branding.Common.ps1"));
        string installer = File.ReadAllText(Path.Combine(root, "tools", "Build-Installer.ps1"));
        string store = File.ReadAllText(Path.Combine(root, "tools", "Publish-StorePackage.ps1"));
        string launcher = File.ReadAllText(Path.Combine(root, "tools", "Publish-LauncherBaseline.ps1"));
        string macHost = File.ReadAllText(Path.Combine(root, "tools", "Publish-MacHost.sh"));

        Assert.Contains("profiles.local.props", selector, StringComparison.Ordinal);
        Assert.Contains("BRAND_PROFILE_ROOT", selector, StringComparison.Ordinal);
        Assert.Contains("BrandExternalProfilesRoot", selector, StringComparison.Ordinal);
        Assert.Contains("profiles.local.props", branding, StringComparison.Ordinal);
        Assert.Contains("AppIconPath", branding, StringComparison.Ordinal);
        Assert.Contains("LauncherBaselinePath", branding, StringComparison.Ordinal);
        Assert.Contains("GitHubReleaseTokenFile", branding, StringComparison.Ordinal);
        Assert.Contains("GiteeReleaseTokenFile", branding, StringComparison.Ordinal);
        Assert.Contains("GiteeReleaseUsername", branding, StringComparison.Ordinal);
        Assert.Contains("MobileAppEnabled", branding, StringComparison.Ordinal);
        Assert.Contains("$brand.AppIconPath", installer, StringComparison.Ordinal);
        Assert.Contains("$brand.AppIconPath", store, StringComparison.Ordinal);
        Assert.Contains("$brand.LauncherBaselinePath", launcher, StringComparison.Ordinal);
        Assert.Contains("BRAND_PROFILE_DIR", macHost, StringComparison.Ordinal);
        Assert.Contains("profiles.local.props", macHost, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFacingBehaviorCopyUsesNeutralSubjectsInsteadOfThePublicProductName()
    {
        string root = FindRepositoryRoot();
        foreach (string resourceName in new[] { "Strings.resx", "Strings.zh-Hans.resx", "Strings.ja-JP.resx" })
        {
            XDocument document = XDocument.Load(Path.Combine(root, "src", "ExpressPackingMonitoring", "Resources", resourceName));
            foreach (XElement data in document.Descendants("data"))
            {
                string value = data.Element("value")?.Value ?? "";
                Assert.DoesNotContain("PackingProof 不会", value, StringComparison.Ordinal);
                Assert.DoesNotContain("PackingProof 无法", value, StringComparison.Ordinal);
                Assert.DoesNotContain("PackingProof is already running", value, StringComparison.Ordinal);
                Assert.DoesNotContain("PackingProof の改善", value, StringComparison.Ordinal);
            }
        }

        string hostOptions = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.Host", "HostOptions.cs"));
        string macDialog = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.Host", "Platform", "MacDialog.cs"));
        string viewerSession = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.Host", "ViewerSession.cs"));
        string macDeployment = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.Host", "MacDeploymentSetup.cs"));
        string launcher = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring", "Workstations", "WorkstationLauncher.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root, "src", "MacHostShell", "Sources", "PackingProofHostMenu", "main.swift"));
        string view = File.ReadAllText(Path.Combine(
            root, "src", "MacHostShell", "Sources", "PackingProofHostMenu", "MainWindowView.swift"));
        string macPublisher = File.ReadAllText(Path.Combine(root, "tools", "Publish-MacHost.sh"));

        Assert.Contains("BrandMetadata.ProductName", hostOptions, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.ProductName", macDialog, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.ProductName", viewerSession, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.ArtifactPrefix", macDeployment, StringComparison.Ordinal);
        Assert.Contains("AppLanguage.Get(\"PackingProof 主机离线或无法访问\")", launcher, StringComparison.Ordinal);
        Assert.Contains("AppBranding.displayName", shell, StringComparison.Ordinal);
        Assert.Contains("AppBranding.displayName", view, StringComparison.Ordinal);
        Assert.Contains("BrandVideosDirectoryName", macPublisher, StringComparison.Ordinal);
        Assert.Contains("BrandUpdatesUrl", macPublisher, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalProfileIdentityDoesNotLeakIntoTrackedPublicFiles()
    {
        string root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(root, "tools", "Test-PrivateBrandLeak.ps1"));
        startInfo.ArgumentList.Add("-RepoRoot");
        startInfo.ArgumentList.Add(root);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动私有身份泄漏扫描");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"私有身份泄漏扫描失败：{output}{Environment.NewLine}{error}");
    }

    [Fact]
    public void LocalizedBrandCatalogIsOptionalAndLauncherAotSafe()
    {
        string root = FindRepositoryRoot();
        string metadata = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.UpdateCore", "BrandMetadata.cs"));
        string project = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.UpdateCore", "ExpressPackingMonitoring.UpdateCore.csproj"));
        string selector = File.ReadAllText(Path.Combine(root, "branding", "Brand.props"));
        string branding = File.ReadAllText(Path.Combine(root, "tools", "Branding.Common.ps1"));
        string readme = File.ReadAllText(Path.Combine(root, "branding", "README.md"));

        Assert.Contains("BrandLocalizations.json", metadata, StringComparison.Ordinal);
        Assert.Contains("JsonDocument.Parse", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer", metadata, StringComparison.Ordinal);
        Assert.Contains("$(BrandLocalizationFile)", project, StringComparison.Ordinal);
        Assert.Contains("BrandLocalizationFile Condition=", selector, StringComparison.Ordinal);
        Assert.Contains("BrandLocalizationRelativePath", selector, StringComparison.Ordinal);
        Assert.Contains("BrandLocalizationRelativePath", branding, StringComparison.Ordinal);
        Assert.Contains("\"th-TH\"", readme, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> ReadProfile(string profile)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "branding",
            "profiles",
            profile,
            "Brand.props");
        XDocument document = XDocument.Load(path);
        return document.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.Ordinal);
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"软件配置缺少 {key}");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到解决方案根目录");
    }
}
