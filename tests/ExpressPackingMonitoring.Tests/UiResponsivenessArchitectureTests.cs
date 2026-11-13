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
        Assert.Contains("Task.Run(mfSource.StopAndWait)", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(networkSource.Stop)", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(CameraLifecycleWaitTimeout)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingStopRunsAudioAndFrameQueueCleanupOffTheUiThread()
    {
        string recording = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Recording.cs");
        int stopStart = recording.IndexOf(
            "private async Task InternalStopRecordingAsync",
            StringComparison.Ordinal);
        int stopEnd = recording.IndexOf(
            "private string ResolveBestStoragePath",
            stopStart,
            StringComparison.Ordinal);

        Assert.True(stopStart >= 0);
        Assert.True(stopEnd > stopStart);
        string stop = recording[stopStart..stopEnd];

        Assert.Contains("await StopAudioRecordingAsync();", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("audioFilePath = StopAudioRecording();", stop, StringComparison.Ordinal);
        Assert.Contains(
            "await Task.Run(() => DrainRecordingFrameQueue(oldQueue));",
            stop,
            StringComparison.Ordinal);

        string audio = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Audio.cs");
        Assert.Contains("Task.Run(StopAudioRecording)", audio, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCameraRestartRunsDeviceStartupOffTheUiThread()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Camera.cs");

        Assert.Contains("await Task.Run(StartCamera).ConfigureAwait(false);", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(CameraLifecycleWaitTimeout)", source, StringComparison.Ordinal);
        Assert.Contains("await StartCameraAsync();", source, StringComparison.Ordinal);
        Assert.Contains("_ = StartCameraAndOverlayChannelsAsync();",
            ReadProjectFile("ViewModels", "MainViewModel.Media.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await StopCameraAsync();\n                    StartCamera();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingStorageResolutionAndProbeRunOffTheUiThread()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Recording.cs");

        Assert.Contains("await Task.Run(() => ResolveBestStoragePlan());", source, StringComparison.Ordinal);
        Assert.Contains("await IsDirectoryWritableAsync(baseFolder)", source, StringComparison.Ordinal);
        Assert.Contains(
            "Task.Run(() => IsDirectoryWritable(dirPath))",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayCameraLifecycleRunsOffTheUiThread()
    {
        string channels = ReadProjectFile(
            "ViewModels",
            "MainViewModel.OverlayChannels.cs");
        string shell = ReadProjectFile(
            "ViewModels",
            "MainViewModel.cs");

        Assert.Contains("RunOverlayLifecycleOffUiThreadAsync(StartOverlayChannels);", channels,
            StringComparison.Ordinal);
        Assert.Contains("await Task.Run(operation).ConfigureAwait(false);", channels,
            StringComparison.Ordinal);
        Assert.Contains("_ = StartOverlayChannelsAsync();", shell, StringComparison.Ordinal);
        Assert.Contains("_ = StopOverlayChannelsAsync();", shell, StringComparison.Ordinal);
        Assert.Contains("mf.StopAndWait()", channels, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseWizardCameraLifecycleDoesNotSleepOnTheUiThread()
    {
        string source = ReadProjectFile(
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("private async Task ShowStepAsync(int stepIndex)", source,
            StringComparison.Ordinal);
        Assert.Contains("await StopCameraPreviewAsync()", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(CameraPreviewLifecycleTimeout)", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(TimeSpan.FromSeconds(3))", source, StringComparison.Ordinal);
        Assert.Contains("WaitForCameraCleanupAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(50);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_previewCameraForceStopTask.Wait(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorAddressCopyRetriesUseAsyncDelay()
    {
        string source = ReadProjectFile(
            "ViewModels",
            "MainViewModel.Integration.cs");

        Assert.Contains("public async Task CopyMonitorAddressAsync()", source,
            StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(80);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep(80)", source, StringComparison.Ordinal);
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
    public void ManualMkvMaintenanceRunsOnTheThreadPool()
    {
        string source = ReadProjectFile("UI", "SettingsWindow.xaml.cs");
        int conversion = source.IndexOf(
            "Context.BatchConvertMkvToMp4Async(progress, migrationCts.Token)",
            StringComparison.Ordinal);
        int background = source.LastIndexOf(
            "await Task.Run(() =>",
            conversion,
            StringComparison.Ordinal);

        Assert.True(conversion >= 0);
        Assert.True(background >= 0 && conversion - background < 120);
    }

    [Fact]
    public void SettingsStorageProbingRunsOffTheUiThread()
    {
        string source = ReadProjectFile("UI", "SettingsWindow.xaml.cs");

        Assert.Contains("TryPrepareStoragePathAsync(", source, StringComparison.Ordinal);
        Assert.Contains("TryConfirmLocalStoragePathAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_recordingCacheSummaryRefresh.Request(", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() =>", source, StringComparison.Ordinal);
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
