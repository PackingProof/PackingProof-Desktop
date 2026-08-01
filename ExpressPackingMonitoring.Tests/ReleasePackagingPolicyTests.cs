using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ReleasePackagingPolicyTests
{
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
        Assert.Contains("dotnet publish $launcherProject", baselineScript);
        Assert.Contains("launcher-v$normalizedVersion", baselineScript);
        Assert.Contains("ExpressPackingMonitoring\\app.ico", commonScript);
        Assert.Contains("update_check_url=", commonScript);
        Assert.Contains("Replace(\"`r`n\", \"`n\")", commonScript);
        Assert.Contains("Assert-LauncherPackage", commonScript);
        Assert.Contains("$updateManifest[\"launcher_package\"]", publishScript);
        Assert.Contains("$launcherPackageHash", publishScript);
        Assert.Contains("$launcherExecutableHash", publishScript);
        Assert.Contains("protocol_version", publishScript);
        Assert.Contains(
            "AppPatch bridge validation failed: launcher changed but updated app assembly is missing",
            publishScript);
        Assert.Contains("A new launcher baseline requires a compatible AppPatch bridge", publishScript);
        Assert.Contains("本版本不要重复上传 LauncherPatch", publishScript);
        Assert.DoesNotContain("Compress-PackageWithRetry -SourceDir $launcherPackageWorkDir", publishScript);
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
        Assert.Contains("--uninstall-plan-recordings", innoScript);
        Assert.Contains("--uninstall-delete-recordings", innoScript);
        Assert.Contains("--uninstall-delete-local-data", innoScript);
        Assert.Contains("删除设置和临时文件", innoScript);
        Assert.Contains("不会删除录像、录像记录和恢复备份", innoScript);
        Assert.Contains("删除录像和录像记录", innoScript);
        Assert.Contains("SettingsCheckBox.Checked := False", innoScript);
        Assert.Contains("RecordingsCheckBox.Checked := False", innoScript);
        Assert.Contains("/SILENT /EPMUNINSTALLOPTIONS", innoScript);
        Assert.DoesNotContain("MB_DEFBUTTON2", innoScript);
        Assert.DoesNotContain("是否删除本机应用数据", innoScript);
        Assert.DoesNotContain("是否同时删除数据库登记的录像原文件", innoScript);
        Assert.DoesNotContain("DelTree(UserDataPath", innoScript);
        Assert.DoesNotContain("WizardSilent", innoScript);

        Assert.Contains("INNO_SETUP_ISCC", buildScript);
        Assert.Contains("InstallerCompression = \"lzma2/max\"", buildScript);
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
        Assert.Contains("\"-mx=$CompressionLevel\"", publishScript);
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
