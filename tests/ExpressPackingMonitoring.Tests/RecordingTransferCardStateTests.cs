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
    [InlineData(0, 0, 2, 0, 0, false, false, false, "上传失败", "2 个录像等待重试，录像仍保存在本机")]
    [InlineData(0, 0, 2, 0, 0, false, false, true, "需要重新连接", "需要重新连接保存主机，录像仍保存在本机")]
    // 停止重试的录像不占卡片状态，只并进现有明细；全都停止时沿用"上传失败"口径。
    [InlineData(0, 0, 0, 3, 0, false, false, false, "上传失败", "3 个录像已停止上传，请查看失败原因后重试")]
    [InlineData(1, 0, 0, 3, 0, false, false, false, "待上传", "1 个录像等待上传 · 3 个已停止上传")]
    [InlineData(0, 0, 2, 3, 0, false, false, false, "上传失败", "2 个录像等待重试 · 3 个已停止上传，录像仍保存在本机")]
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
