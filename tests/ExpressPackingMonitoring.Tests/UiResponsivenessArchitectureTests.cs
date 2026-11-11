using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UiResponsivenessArchitectureTests
{
    [Fact]
    public void HighFrequencyDatabaseRefreshesUseCoalescedBackgroundQueries()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.BackgroundUi.cs");

        Assert.Contains(
            "_todayStatsRefresh.Request(\"今日统计\", RefreshTodayStatsAsync)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_archiveBackupSummaryRefresh.Request(\"网络备份状态\", RefreshArchiveBackupSummaryAsync)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_mobileBackupStatusRefresh.Request(\"设备备份状态\", RefreshMobileBackupStatusesAsync)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_userscriptStatusRefresh.Request(\"油猴状态\", RefreshUserscriptStatusAsync)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_recentScanRecordsRefresh.Request(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "database.GetAggregatedStats(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "database.GetArchiveQueueSummary(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "database?.GetMobileBackupDailyCounts(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CameraStopDoesNotSleepOrWaitOnTheUiThread()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.CameraStop.cs");

        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_cameraForceStopTask.Wait(", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(100)", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(mfSource.Dispose)", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(networkSource.Stop)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundMkvConversionRunsBelowNormalPriority()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Conversion.cs");

        Assert.Contains(
            "process.PriorityClass = ProcessPriorityClass.BelowNormal",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackPageQueriesStayOffTheUiThread()
    {
        string source = ReadProjectFile("UI", "PlaybackWindow.xaml.cs");

        int query = source.IndexOf(
            "BuildVideoPage(request.Start, request.End, request.Keyword",
            StringComparison.Ordinal);
        int background = source.LastIndexOf(
            "Task.Run(() =>",
            query,
            StringComparison.Ordinal);

        Assert.True(query > 0);
        Assert.True(background >= 0 && query - background < 200);
        Assert.Contains(
            "await Task.Run(database.GetVideoSources)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsIntegrationPollingCollectsDataOffTheUiThread()
    {
        string source = ReadProjectFile("UI", "SettingsWindow.xaml.cs");

        Assert.Contains(
            "_integrationStatusRefresh.Request(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task.Run(() => Context.GetExtensionAuthorizations",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Task.Run(() => Context.GetOrderIntegrationDevices",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PrintWorkstationTimersUseBackgroundSnapshots()
    {
        string source = ReadProjectFile(
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");

        Assert.Contains(
            "_deviceSummaryRefresh.Request(\"设备备份概览\", RefreshDeviceSummaryAsync)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_archiveSummaryRefresh.Request(\"网络备份卡片\", RefreshArchiveBackupSummaryAsync)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UiHeartbeatLogsLongStallsEvenWhenPreviewIsDisabled()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Scanner.cs");

        Assert.Contains("UiHeartbeatStallThreshold", source, StringComparison.Ordinal);
        Assert.Contains("\"ui-heartbeat-stall\"", source, StringComparison.Ordinal);
        Assert.Contains("LogResourceHealthIfDue(", source, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(params string[] relativePathParts) =>
        File.ReadAllText(
            Path.Combine(
                [FindRepositoryRoot(), "src", "ExpressPackingMonitoring", .. relativePathParts]),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
