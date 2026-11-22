using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingTransferCardStateTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, false, false, false, "已完成", "暂无待上传录像")]
    [InlineData(0, 0, 0, 0, 0, false, true, false, "已完成", "最近录像已上传")]
    [InlineData(2, 0, 0, 0, 0, false, false, false, "待上传", "2 个录像等待上传")]
    [InlineData(2, 0, 0, 0, 0, true, false, false, "待上传", "2 个录像已保存在本机，联网后自动上传")]
    [InlineData(1, 1, 0, 0, 45, false, false, false, "上传中", "正在上传 45% · 共 2 个待上传")]
    [InlineData(0, 0, 2, 0, 0, false, false, false, "待上传", "2 个录像稍后自动重试，录像仍保存在本机")]
    [InlineData(0, 0, 2, 0, 0, false, false, true, "需要重新连接", "需要重新连接保存主机，录像仍保存在本机")]
    // 停止重试的录像不占卡片状态，只并进现有明细；全都停止时沿用"上传失败"口径。
    [InlineData(0, 0, 0, 3, 0, false, false, false, "上传失败", "3 个录像已停止上传，请查看失败原因后重试")]
    [InlineData(1, 0, 0, 3, 0, false, false, false, "待上传", "1 个录像等待上传 · 3 个已停止上传")]
    [InlineData(0, 0, 2, 3, 0, false, false, false, "待上传", "2 个录像稍后自动重试 · 3 个已停止上传，录像仍保存在本机")]
    public void BuildRecordingTransferCardState_SeparatesShortStatusFromCountedDetail(
        int pendingCount,
        int uploadingCount,
        int failedCount,
        int abandonedCount,
        double progress,
        bool hostOffline,
        bool recentlyUploaded,
        bool requiresReconnect,
        string expectedShortStatus,
        string expectedDetail)
    {
        RecordingTransferCardState state = MainViewModel.BuildRecordingTransferCardState(
            pendingCount,
            uploadingCount,
            failedCount,
            abandonedCount,
            progress,
            hostOffline,
            recentlyUploaded,
            requiresReconnect);

        Assert.Equal(expectedShortStatus, state.ShortStatusText);
        Assert.Equal(expectedDetail, state.DetailText);
    }

    [Theory]
    [InlineData("设备令牌无效，请重新连接", true)]
    [InlineData("保存主机连接协议已升级，请重新连接保存主机", true)]
    [InlineData("保存主机未允许本机连接，可重新申请并在保存主机上点“允许连接”", true)]
    [InlineData("主机请求失败：HTTP 500", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReconnectRequiredError_DetectsPairingFailures(string? error, bool expected) =>
        Assert.Equal(expected, MainViewModel.IsReconnectRequiredError(error));

    [Fact]
    public void AwaitingHostApprovalIsNotReportedAsUploadFailure()
    {
        RecordingTransferCardState state = MainViewModel.BuildRecordingTransferCardState(
            pendingCount: 109,
            uploadingCount: 0,
            failedCount: 0,
            abandonedCount: 0,
            progress: 0,
            hostOffline: false,
            recentlyUploaded: false,
            hostAwaitingApproval: true);

        Assert.Equal("等待授权", state.ShortStatusText);
        Assert.Equal("等待主机允许连接，录像仍保存在本机", state.DetailText);
    }

    [Fact]
    public void UserPausedUploadKeepsQueueLocalAndIsNotReportedAsFailure()
    {
        RecordingTransferCardState state = MainViewModel.BuildRecordingTransferCardState(
            pendingCount: 109,
            uploadingCount: 0,
            failedCount: 0,
            abandonedCount: 0,
            progress: 0,
            hostOffline: false,
            recentlyUploaded: false,
            pausedByUser: true);

        Assert.Equal("已暂停", state.ShortStatusText);
        Assert.Equal("已暂停，109 个录像保存在本机", state.DetailText);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, null)]
    public void PausingUploadsDoesNotChangeTheBoundHostOnlineState(
        bool? currentOnline,
        bool? expected)
    {
        bool? resolved = MainViewModel.ResolveBoundHostOnlineAfterHealth(
            currentOnline,
            HostConnectionState.PausedByUser);

        Assert.Equal(
            expected,
            resolved);
        Assert.Equal(
            currentOnline switch
            {
                true => "在线",
                false => "离线",
                _ => "检查中"
            },
            MainViewModel.ResolveBoundHostOnlineStatusText(
                resolved,
                requiresReconnect: false,
                awaitingApproval: false));
    }

    [Fact]
    public void PauseActionStaysInsideTheTransferPopupAndHostBannerRemainsOnMainWindow()
    {
        string root = FindRepositoryRoot();
        string mainXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml"));
        string transferXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "TransferManagementWindow.xaml"));

        Assert.Contains("RecordingWorkstationHostCard", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ToggleRecordingUploadPauseCommand",
            mainXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "UploadPauseButtonText",
            transferXaml,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            transferXaml.Split(
                "Content=\"{Binding UploadPauseButtonText}\"",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(
            "UploadPauseOverlayButtonStyle",
            transferXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToggleUploadPauseCommand",
            transferXaml,
            StringComparison.Ordinal);
        int retryIndex = transferXaml.LastIndexOf(
            "Content=\"{loc:Localized 全部重试}\"",
            StringComparison.Ordinal);
        int pauseIndex = transferXaml.LastIndexOf(
            "Content=\"{Binding UploadPauseButtonText}\"",
            StringComparison.Ordinal);
        Assert.True(retryIndex >= 0);
        Assert.True(pauseIndex > retryIndex);

        int failureStart = transferXaml.IndexOf(
            "ItemsSource=\"{Binding FailureItems}\"",
            StringComparison.Ordinal);
        Assert.True(failureStart >= 0);
        int failureEnd = transferXaml.IndexOf(
            "</ItemsControl>",
            failureStart,
            StringComparison.Ordinal);
        Assert.True(failureEnd > failureStart);
        string failureTemplate = transferXaml[failureStart..failureEnd];
        Assert.Contains("Text=\"{Binding Reason}\"", failureTemplate, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SummaryText}\"", failureTemplate, StringComparison.Ordinal);
        Assert.Contains("Content=\"{loc:Localized 重试}\"", failureTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{loc:Localized 需要处理}\"",
            transferXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UiEventPathsWakeTheBackgroundTransferWorker()
    {
        string root = FindRepositoryRoot();
        string transferViewModel = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs"));
        string mediaViewModel = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Media.cs"));

        Assert.DoesNotContain(
            "EnqueueCompletedRecordings(",
            transferViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnqueueCompletedRecordings(",
            mediaViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            ".RequestCompletedRecordingsScan()",
            mediaViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedRecordingEnqueueUsesEventsInsteadOfPeriodicRescan()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "RecordingTransferService.cs"));

        Assert.DoesNotContain(
            "CompletedRecordingFullRescan",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShouldRestartCompletedRecordingScan",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequestEnqueueCompletedRecording(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcessCompletedRecordingEvents(",
            source,
            StringComparison.Ordinal);

        int retryStart = source.IndexOf(
            "public void RetryNow()",
            StringComparison.Ordinal);
        int retryEnd = source.IndexOf(
            "internal void RequestEnqueueCompletedRecording",
            retryStart,
            StringComparison.Ordinal);
        Assert.True(retryStart >= 0 && retryEnd > retryStart);
        string retryMethod = source[retryStart..retryEnd];
        Assert.DoesNotContain(
            "_store.",
            retryMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransferSummaryQueryRunsOffTheUiThread()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs"));
        int refresh = source.IndexOf(
            "private void RefreshRecordingTransferSummary",
            StringComparison.Ordinal);
        int backgroundRefresh = source.IndexOf(
            "Task.Run(RefreshRecordingTransferSummaryLoopAsync)",
            refresh,
            StringComparison.Ordinal);
        int query = source.IndexOf(
            "summary = store.GetSummary();",
            backgroundRefresh,
            StringComparison.Ordinal);
        int dispatcherApply = source.IndexOf(
            "await dispatcher.InvokeAsync(",
            query,
            StringComparison.Ordinal);

        Assert.True(refresh >= 0);
        Assert.True(backgroundRefresh > refresh);
        Assert.True(query > backgroundRefresh);
        Assert.True(dispatcherApply > query);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "ExpressPackingMonitoring.sln")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
