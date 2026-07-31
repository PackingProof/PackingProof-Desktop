using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingTransferCardStateTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, false, false, "已完成", "暂无待上传录像")]
    [InlineData(0, 0, 0, 0, false, true, "已完成", "最近录像已上传")]
    [InlineData(2, 0, 0, 0, false, false, "待上传", "2 个录像等待上传")]
    [InlineData(2, 0, 0, 0, true, false, "待上传", "2 个录像已保存在本机，联网后自动上传")]
    [InlineData(1, 1, 0, 45, false, false, "上传中", "正在上传 45% · 共 2 个待上传")]
    [InlineData(0, 0, 2, 0, false, false, "上传失败", "2 个录像等待重试，录像仍保存在本机")]
    public void BuildRecordingTransferCardState_SeparatesShortStatusFromCountedDetail(
        int pendingCount,
        int uploadingCount,
        int failedCount,
        double progress,
        bool hostOffline,
        bool recentlyUploaded,
        string expectedShortStatus,
        string expectedDetail)
    {
        RecordingTransferCardState state = MainViewModel.BuildRecordingTransferCardState(
            pendingCount,
            uploadingCount,
            failedCount,
            progress,
            hostOffline,
            recentlyUploaded);

        Assert.Equal(expectedShortStatus, state.ShortStatusText);
        Assert.Equal(expectedDetail, state.DetailText);
    }
}
