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
    public void Publisher_WrapsOriginalPatchAndMatchingManifestWithoutRecompression()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publisher = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        int patchHashIndex = publisher.IndexOf(
            "$appPatchHash = (Get-FileHash -LiteralPath $appPatchZipPath",
            StringComparison.Ordinal);
        int manifestWriteIndex = publisher.IndexOf(
            "Set-Content -LiteralPath $updateJsonPath",
            StringComparison.Ordinal);
        int manualPackageIndex = publisher.IndexOf(
            "New-ManualUpdatePackage `",
            StringComparison.Ordinal);

        Assert.True(patchHashIndex >= 0);
        Assert.True(manifestWriteIndex > patchHashIndex);
        Assert.True(manualPackageIndex > manifestWriteIndex);
        Assert.Contains(
            "$appPatchZipName = \"ExpressPackingMonitoring_AppPatch_$releaseTag.zip\"",
            publisher);
        Assert.Contains(
            "$launcherPackageName = \"PackingProof_LauncherPatch_$releaseTag.zip\"",
            publisher);
        Assert.Contains(
            "Copy-Item -LiteralPath $PatchZipPath -Destination",
            publisher);
        Assert.Contains(
            "Copy-Item -LiteralPath $UpdateManifestPath -Destination (Join-Path $manualWorkDir \"update_manifest.json\")",
            publisher);
        Assert.DoesNotContain("Compress-Archive -Path $PatchZipPath", publisher);
    }

    [Fact]
    public void ManualStager_CopiesProvidedManifestAndPatchWithoutGeneratingOrCompressingThem()
    {
        string repositoryRoot = FindRepositoryRoot();
        string stager = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Stage-AppPatch.ps1"),
            Encoding.UTF8);

        Assert.Contains("$manifestText = [System.IO.File]::ReadAllText", stager);
        Assert.Contains("Copy-Item -LiteralPath $patchZipPath", stager);
        Assert.Contains("$manifestText,", stager);
        Assert.DoesNotContain("ConvertTo-Json", stager);
        Assert.DoesNotContain("Compress-Archive", stager);
        Assert.DoesNotContain("ZipFile]::CreateFromDirectory", stager);
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
