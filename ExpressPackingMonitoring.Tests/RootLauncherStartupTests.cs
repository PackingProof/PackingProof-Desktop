using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RootLauncherStartupTests
{
    [Fact]
    public void NormalDirectLaunchIsRedirectedButLauncherOriginIsNot()
    {
        Assert.True(RootLauncherStartupService.ShouldRedirect(["--role", "CameraMonitor"]));
        Assert.False(RootLauncherStartupService.ShouldRedirect(
            [RootLauncherStartupService.RootLauncherMarkerOption, "--role", "CameraMonitor"]));
    }

    [Theory]
    [InlineData("--audio-check")]
    [InlineData("--audio-probe")]
    [InlineData("--uninstall-plan-recordings")]
    [InlineData("--uninstall-delete-recordings")]
    [InlineData("--uninstall-delete-local-data")]
    public void MaintenanceCommandsBypassRootLauncherRedirect(string option)
    {
        Assert.False(RootLauncherStartupService.ShouldRedirect([option]));
    }

    [Fact]
    public void HandoffPreservesRoleArgumentsAfterInternalWaitArguments()
    {
        string launcher = Path.Combine(Path.GetTempPath(), "install", "ExpressPackingMonitoring.exe");
        var startInfo = RootLauncherStartupService.BuildHandoffStartInfo(
            launcher,
            1234,
            ["--temporary-role", "PrintStation"]);

        Assert.Equal(launcher, startInfo.FileName);
        Assert.Equal(
            ["--wait-for-process-exit", "1234", "--temporary-role", "PrintStation"],
            startInfo.ArgumentList);
    }

    [Fact]
    public void RootLauncherWaitsBeforeInspectingOrInstallingAndMarksChildLaunch()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring.Launcher", "Program.cs"));

        int prepareIndex = source.IndexOf("TryPrepareStartupArguments(args", StringComparison.Ordinal);
        int runningIndex = source.IndexOf("IsAppRunning(appPath)", StringComparison.Ordinal);
        Assert.True(prepareIndex >= 0);
        Assert.True(runningIndex > prepareIndex);
        Assert.Contains(
            "startInfo.ArgumentList.Add(LaunchedByRootLauncherOption);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RootLauncherClearsReadOnlyFilesAndRestoresOriginalAttributesOnRollback()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring.Launcher", "Program.cs"));

        Assert.Contains("RemoveReadOnlyAttribute(targetPath);", source, StringComparison.Ordinal);
        Assert.Contains("File.SetAttributes(backup.TargetPath, backup.OriginalAttributes);", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
