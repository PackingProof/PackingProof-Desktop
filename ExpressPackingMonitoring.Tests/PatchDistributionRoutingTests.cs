using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PatchDistributionRoutingTests
{
    [Fact]
    public void Launcher_DownloadsAndInstallsOnlyAutomaticAppPatchName()
    {
        string repositoryRoot = FindRepositoryRoot();
        string launcher = File.ReadAllText(
            Path.Combine(repositoryRoot, "ExpressPackingMonitoring.Launcher", "Program.cs"),
            Encoding.UTF8);

        Assert.Contains("PackingProof_AppPatch_v*.zip", launcher);
        Assert.Contains("ExpressPackingMonitoring_AppPatch_v*.zip", launcher);
        Assert.Contains("ExpressPackingMonitoring_AppPatch_v{descriptor.LatestVersion}.zip", launcher);
        Assert.DoesNotContain("PackingProof_ManualUpdate_", launcher);
    }

    [Fact]
    public void Launcher_LogsUpdateLifecycleAndFallsBackWithinCurrentRun()
    {
        string repositoryRoot = FindRepositoryRoot();
        string launcher = File.ReadAllText(
            Path.Combine(repositoryRoot, "ExpressPackingMonitoring.Launcher", "Program.cs"),
            Encoding.UTF8);

        Assert.Contains("启动器启动：appRunning=", launcher);
        Assert.Contains("自动检查更新开始：current=", launcher);
        Assert.Contains("Patch 已保存到 pending，等待下次启动安装", launcher);
        Assert.Contains("MetadataRequestAttempts = 2", launcher);
        Assert.Contains("GetJsonWithRetryAsync", launcher);
        Assert.Contains("本次立即改用更新描述中的下载地址", launcher);
        Assert.DoesNotContain(
            "if (failureState.ConsecutiveGithubDownloadFailures < GithubDownloadFailureFallbackThreshold)",
            launcher);
    }

    [Fact]
    public void Publisher_EmbedsManualInstallerDirectlyInOriginalPatch()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publisher = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        int patchHashIndex = publisher.IndexOf(
            "$appPatchHash = (Get-FileHash -LiteralPath $appPatchZipPath",
            StringComparison.Ordinal);
        int installerCopyIndex = publisher.IndexOf(
            "Copy-Item -LiteralPath $InstallerCmdPath",
            StringComparison.Ordinal);

        Assert.True(patchHashIndex >= 0);
        Assert.True(installerCopyIndex >= 0);
        Assert.True(patchHashIndex > installerCopyIndex);
        Assert.Contains(
            "$appPatchZipName = \"ExpressPackingMonitoring_AppPatch_$releaseTag.zip\"",
            publisher);
        Assert.Contains(
            "$launcherPackageName = \"PackingProof_LauncherPatch_$releaseTag.zip\"",
            publisher);
        Assert.Contains("$appPatchInstallerCmdName = \"双击更新主程序.cmd\"", publisher);
        Assert.Contains("$appPatchInstallerScriptName = \"apply_app_patch.ps1\"", publisher);
        Assert.DoesNotContain("New-ManualUpdatePackage", publisher);
    }

    [Fact]
    public void ManualInstaller_AppliesExtractedPatchWithValidationAndRollback()
    {
        string repositoryRoot = FindRepositoryRoot();
        string installer = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Apply-AppPatch.ps1"),
            Encoding.UTF8);

        Assert.Contains("patch_manifest.json", installer);
        Assert.Contains("Get-FileSha256", installer);
        Assert.Contains("Stop-TargetApplication", installer);
        Assert.Contains("File]::Replace", installer);
        Assert.Contains("正在恢复原文件", installer);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
