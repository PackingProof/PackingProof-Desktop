using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ReleasePackagingPolicyTests
{
    [Fact]
    public void OfficialLinks_UseCurrentGithubRepository()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] relativePaths =
        [
            @"Installer\ExpressPackingMonitoring.iss",
            @"Scripts\快递助手订单推送.user.js",
            @"ExpressPackingMonitoring\Services\BackupCompatibilityPolicy.cs",
            @"ExpressPackingMonitoring\UI\SettingsWindow.xaml.cs"
        ];

        foreach (string relativePath in relativePaths)
        {
            string content = File.ReadAllText(
                Path.Combine(repositoryRoot, relativePath),
                Encoding.UTF8);
            Assert.Contains("PackingProof/PackingProof-Desktop", content, StringComparison.Ordinal);
            Assert.DoesNotContain("m-RNA/ExpressPackingMonitoring", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Packaging_WarnsButDoesNotBlockWhenManualChecksAreUnconfirmed()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string incrementalScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "打包脚本-增量.bat"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "打包脚本-基线.bat"),
            Encoding.UTF8);

        Assert.Contains("Packaging will continue", publishScript);
        Assert.DoesNotContain("throw \"Manual core business", publishScript);
        Assert.DoesNotContain("choice /C YN", incrementalScript);
        Assert.DoesNotContain("-ConfirmManualCoreChecks", incrementalScript);
        Assert.DoesNotContain("choice /C YN", baselineScript);
        Assert.DoesNotContain("-ConfirmManualCoreChecks", baselineScript);
    }

    [Fact]
    public void Packaging_RequiresDestructiveOutputsToBeStrictRepositoryDescendants()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("function Test-IsStrictDescendantPath", publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "[string]::Equals($fullPath, $fullRoot",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fullRoot + [System.IO.Path]::DirectorySeparatorChar",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IsStrictDescendantPath -Path $outputFullPath -Root $repoFullPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IsStrictDescendantPath -Path $zipFullPath -Root $repoFullPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$outputFullPath.StartsWith($repoFullPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$zipFullPath.StartsWith($repoFullPath",
            publishScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_EmbedsSafeManualInstallersInPatchPackages()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string appInstallerScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Apply-AppPatch.ps1"),
            Encoding.UTF8);
        string installerCmd = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Install-AppPatch.cmd"),
            Encoding.UTF8);
        string launcherBaselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-LauncherBaseline.ps1"),
            Encoding.UTF8);

        Assert.DoesNotContain("New-ManualUpdatePackage", publishScript);
        Assert.Contains("双击更新主程序.cmd", publishScript);
        Assert.Contains("apply_app_patch.ps1", publishScript);
        Assert.Contains("主程序更新说明.txt", publishScript);
        Assert.Contains("双击更新启动器.cmd", launcherBaselineScript);
        Assert.Contains("apply_launcher_patch.ps1", launcherBaselineScript);
        Assert.Contains("launcher_patch_manifest.json", launcherBaselineScript);
        Assert.Contains("启动器更新说明.txt", launcherBaselineScript);
        Assert.Contains("Get-FileSha256", appInstallerScript);
        Assert.Contains("System.Security.Cryptography.SHA256", appInstallerScript);
        Assert.Contains("AppRootDirectory", appInstallerScript);
        Assert.Contains("正在恢复原文件", appInstallerScript);
        Assert.Contains("apply_app_patch.ps1", installerCmd);
        Assert.Contains("powershell.exe", installerCmd);
        Assert.Contains("Copy-NormalizedCommandFile", publishScript);
        Assert.Contains("Copy-NormalizedCommandFile", launcherBaselineScript);
        Assert.DoesNotContain("taskkill", installerCmd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Packaging_ReusesLockedLauncherBaselineAndKeepsBridgeSafetyGate()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-LauncherBaseline.ps1"),
            Encoding.UTF8);
        string commonScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "LauncherBaseline.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("Read-LauncherBaselineManifest", publishScript);
        Assert.Contains("Resolve-LauncherBaselineExecutable", publishScript);
        Assert.Contains("Launcher logical inputs changed", publishScript);
        Assert.Contains("git -C $repoRoot diff --quiet", publishScript);
        Assert.DoesNotContain("$launcherProject", publishScript);
        Assert.Contains("\"--artifacts-path\", $appBuildArtifacts", publishScript);
        Assert.Contains("\"-o\", $appPublishDir", publishScript);
        Assert.DoesNotContain("BaseIntermediateOutputPath=$appBaseIntermediate", publishScript);
        Assert.Contains("dotnet publish $launcherProject", baselineScript);
        Assert.Contains("launcher-v$normalizedVersion", baselineScript);
        Assert.Contains("ExpressPackingMonitoring\\app.ico", commonScript);
        Assert.Contains("update_check_url=", commonScript);
        Assert.Contains("Replace(\"`r`n\", \"`n\")", commonScript);
        Assert.Contains("Assert-LauncherPackage", commonScript);
        Assert.Contains("$updateManifest[\"launcher_package\"]", publishScript);
        Assert.Contains("$launcherPackageInfo[\"github_url\"]", publishScript);
        Assert.Contains("$launcherPackageInfo[\"gitee_url\"]", publishScript);
        Assert.Contains("LAUNCHER_PACKAGE_GITHUB_URL_TEMPLATE", publishScript);
        Assert.Contains("LAUNCHER_PACKAGE_GITEE_URL_TEMPLATE", publishScript);
        Assert.Contains("$launcherPackageHash", publishScript);
        Assert.Contains("$launcherExecutableHash", publishScript);
        Assert.Contains("protocol_version", publishScript);
        Assert.Contains(
            "AppPatch bridge validation failed: launcher changed but updated app assembly is missing",
            publishScript);
        Assert.Contains("A new launcher baseline requires a compatible AppPatch bridge", publishScript);
        Assert.Contains("[switch]$ReuseExistingLauncherBaseline", publishScript);
        Assert.Contains("ReuseExistingLauncherBaseline requires an existing app release tag", publishScript);
        Assert.Contains("本版本不要重复上传 LauncherPatch", publishScript);
        Assert.DoesNotContain("Compress-PackageWithRetry -SourceDir $launcherPackageWorkDir", publishScript);
    }

    [Fact]
    public void Packaging_UsesPinnedVerifiedFfmpegEssentialsDependency()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string commonScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "FFmpegBaseline.Common.ps1"),
            Encoding.UTF8);
        string manifest = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "ffmpeg-baseline.json"),
            Encoding.UTF8);

        Assert.Contains("Read-FFmpegBaselineManifest", publishScript);
        Assert.Contains("Resolve-FFmpegBaselineExecutable", publishScript);
        Assert.Contains("tools\\ffmpeg.exe", publishScript);
        Assert.Contains("Assert-FFmpegPackage", commonScript);
        Assert.Contains("Assert-FFmpegExecutable", commonScript);
        Assert.Contains("unsafe path", commonScript);
        Assert.Contains("trying next source", commonScript);
        Assert.Contains("ffmpeg-8.0.1-essentials_build.7z", manifest);
        Assert.Contains("GyanD/codexffmpeg/releases/download/8.0.1", manifest);
        Assert.Contains("5af82a0d4fe2b9eae211b967332ea97edfc51c6b328ca35b827e73eac560dc0d", manifest);
    }

    [Fact]
    public void ReleaseValidation_RequiresHardwareAwareEncoderRoundTrips()
    {
        string repositoryRoot = FindRepositoryRoot();
        string releaseScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Test-Release.ps1"),
            Encoding.UTF8);
        string encoderScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Test-EncodingCodecs.ps1"),
            Encoding.UTF8);
        string solution = File.ReadAllText(
            Path.Combine(repositoryRoot, "ExpressPackingMonitoring.sln"),
            Encoding.UTF8);

        Assert.Contains("Test-EncodingCodecs.ps1", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance Win32_VideoController", encoderScript, StringComparison.Ordinal);
        Assert.Contains("libx264", encoderScript, StringComparison.Ordinal);
        Assert.Contains("libx265", encoderScript, StringComparison.Ordinal);
        Assert.Contains("h264_nvenc", encoderScript, StringComparison.Ordinal);
        Assert.Contains("hevc_nvenc", encoderScript, StringComparison.Ordinal);
        Assert.Contains("h264_amf", encoderScript, StringComparison.Ordinal);
        Assert.Contains("hevc_amf", encoderScript, StringComparison.Ordinal);
        Assert.Contains("h264_qsv", encoderScript, StringComparison.Ordinal);
        Assert.Contains("hevc_qsv", encoderScript, StringComparison.Ordinal);
        Assert.Contains("EPM_REQUIRED_ENCODERS", encoderScript, StringComparison.Ordinal);
        Assert.Contains("ExpressPackingMonitoring.EncodingIntegrationTests", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_OnlyBuildsSlimPatchForCompatibleRuntimeBaseline()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string compatibilityScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "AppPatchRuntimeCompatibility.Common.ps1"),
            Encoding.UTF8);
        string manifest = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "ffmpeg-baseline.json"),
            Encoding.UTF8);

        Assert.Contains("Test-AppPatchRuntimeCompatibility", publishScript);
        Assert.Contains("-ExcludeCompatibleRuntimes", publishScript);
        Assert.Contains("Test-IsAppPatchManagedRuntimePath", publishScript);
        Assert.Contains("Test-ZipContainsEntryPrefix", publishScript);
        Assert.Contains("patch_supported", publishScript);
        Assert.Contains("APP_PATCH_GITHUB_URL_TEMPLATE", publishScript);
        Assert.Contains("APP_PATCH_GITEE_URL_TEMPLATE", publishScript);
        Assert.Contains("$patchPackageInfo[\"github_url\"]", publishScript);
        Assert.Contains("$patchPackageInfo[\"gitee_url\"]", publishScript);
        Assert.Contains("full_download_fallback_page", publishScript);
        Assert.Contains("FULL_DOWNLOAD_PRIMARY_PAGE_URL_TEMPLATE", publishScript);
        Assert.Contains("FULL_DOWNLOAD_FALLBACK_PAGE_URL_TEMPLATE", publishScript);
        Assert.Contains("-Key \"FULL_DOWNLOAD_PAGE\" -DefaultValue \"\"", publishScript);
        Assert.Contains("Full download fallback page:", publishScript);
        Assert.Contains("$updateManifest[\"patch_package\"] = $null", publishScript);
        Assert.Contains("Remove-Item -LiteralPath $appPatchZipPath -Force", publishScript);
        Assert.Contains("$patchReason", publishScript);
        Assert.Contains("app_patch_compatible_executables", manifest);
        Assert.Contains("b1383f5d07470d503edecdaee4bddc5891e986e916a698299b357f79cfe445fd", manifest);
        Assert.Contains("AppPatch 基线中的 FFmpeg 版本或哈希不在兼容白名单中", compatibilityScript);
        Assert.Contains("AppPatch 基线缺少 LibVLC 必需文件", compatibilityScript);
    }

    [Fact]
    public void Packaging_IgnoresLauncherComponentTagsWhenResolvingAppVersion()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("^v\\d+\\.\\d+\\.\\d+", publishScript);
        Assert.Contains("git -C $repoRoot describe --tags --match \"v[0-9]*\"", publishScript);
        Assert.DoesNotContain("git -C $repoRoot describe --tags --always", publishScript);
    }

    [Fact]
    public void WindowsInstaller_UsesFixedPerUserIdentityAndSafeReleaseInputs()
    {
        string repositoryRoot = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Installer", "ExpressPackingMonitoring.iss"),
            Encoding.UTF8);
        string chineseMessages = File.ReadAllText(
            Path.Combine(repositoryRoot, "Installer", "Languages", "ChineseSimplified.isl"),
            Encoding.UTF8);
        string buildScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Build-Installer.ps1"),
            Encoding.UTF8);
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05", innoScript);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\ExpressPackingMonitoring", innoScript);
        Assert.Contains("DisableDirPage=yes", innoScript);
        Assert.Contains("PrivilegesRequired=lowest", innoScript);
        Assert.Contains("ArchitecturesAllowed=x64compatible", innoScript);
        Assert.Contains("CloseApplications=yes", innoScript);
        Assert.DoesNotContain("CloseApplications=force", innoScript);
        Assert.Contains(@"MessagesFile: ""Languages\ChineseSimplified.isl""", innoScript);
        Assert.Contains("LanguageName=简体中文", chineseMessages);
        Assert.Contains("ButtonNext=下一步", chineseMessages);
        Assert.Contains(@"Filename: ""{app}\{#MyAppExeName}""; WorkingDir: ""{app}""", innoScript);
        Assert.Equal(2, innoScript.Split(
            "AppUserModelID: \"{#MyAppUserModelId}\"",
            StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "#define MyAppUserModelId \"PackingProof.ExpressPackingMonitoring\"",
            innoScript);
        Assert.Contains("--uninstall-plan-recordings", innoScript);
        Assert.Contains("--uninstall-delete-recordings", innoScript);
        Assert.Contains("--uninstall-delete-local-data", innoScript);
        Assert.Contains("删除设置和临时文件", innoScript);
        Assert.Contains("不会删除录像、录像记录和恢复备份", innoScript);
        Assert.Contains("删除录像和录像记录", innoScript);
        Assert.Contains("SettingsCheckBox.Checked := False", innoScript);
        Assert.Contains("RecordingsCheckBox.Checked := False", innoScript);
        Assert.Contains("HeadingLabel.Height := ScaleY(26)", innoScript);
        Assert.Contains("/SILENT /EPMUNINSTALLOPTIONS", innoScript);
        Assert.DoesNotContain("MB_DEFBUTTON2", innoScript);
        Assert.DoesNotContain("是否删除本机应用数据", innoScript);
        Assert.DoesNotContain("是否同时删除数据库登记的录像原文件", innoScript);
        Assert.DoesNotContain("DelTree(UserDataPath", innoScript);
        Assert.DoesNotContain("WizardSilent", innoScript);

        Assert.Contains("INNO_SETUP_ISCC", buildScript);
        Assert.Contains("InstallerCompression = \"lzma2/ultra64\"", buildScript);
        Assert.Contains("InstallerCompression = \"lzma2/ultra64\"", publishScript);
        Assert.Contains("winget install --id JRSoftware.InnoSetup", buildScript);
        Assert.Contains("WINDOWS_SIGN_CERT_THUMBPRINT", buildScript);
        Assert.Contains("Get-AuthenticodeSignature", buildScript);
        Assert.Contains("PackingProof_Setup_v$normalizedVersion.exe", buildScript);
        Assert.Contains("config.json", buildScript);
        Assert.Contains("videos.db", buildScript);

        Assert.Contains("OutputBaseFilename=PackingProof_Setup_v{#MyAppVersion}", innoScript);
        Assert.Contains("PackingProof_Setup_$releaseTag.exe", publishScript);
        Assert.Contains("\"PackingProof+$packageVersion\"", publishScript);
        Assert.Contains("Build-Installer.ps1", publishScript);
        Assert.Contains("SmartScreen", publishScript);
        Assert.Contains("GitHub 默认上传", publishScript);
        Assert.Contains("Gitee 手工上传", publishScript);
        Assert.Contains("Setup、完整 7z 和完整 ZIP 使用 Full download page", publishScript);
        Assert.Contains("SEVEN_ZIP_EXE", publishScript);
        Assert.Contains("winget install --id 7zip.7zip", publishScript);
        Assert.Contains("-t7z", publishScript);
        Assert.Contains("SevenZipCompressionLevel = 5", publishScript);
        Assert.Contains("ZipCompressionLevel = \"Optimal\"", publishScript);
        Assert.Contains("if ($CompressionLevel -ge 9)", publishScript);
        Assert.Contains("\"-mx=$CompressionLevel\"", publishScript);
        Assert.Contains("\"-md=128m\"", publishScript);
        Assert.Contains("\"-mfb=273\"", publishScript);
        Assert.Contains("-CompressionLevel $ZipCompressionLevel", publishScript);
        Assert.Contains("-InstallerCompression $InstallerCompression", publishScript);
        Assert.Contains("-m0=lzma2", publishScript);
        Assert.Contains("-ms=on", publishScript);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
