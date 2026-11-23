using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ModeTransitionTests
{
    [Fact]
    public void PolicyBlocksEveryUnsafeActivityCategory()
    {
        IReadOnlyList<string> blockers = ModeTransitionPolicy.FindBlockers(
            new ModeTransitionActivity(
                Recording: true,
                ActiveMobileBackups: true,
                PendingRecordingUploads: 2,
                ActiveClipTasks: true,
                ActiveMediaFinalization: true,
                PhotoCaptureInProgress: true,
                PhotoArchiveInProgress: true,
                RecordingStartInProgress: true,
                RecordingStopInProgress: true));

        Assert.Equal(
            [
                "recording",
                "mobile-backup",
                "recording-upload",
                "clip-task",
                "media-finalization",
                "photo-capture",
                "photo-archive",
                "recording-start",
                "recording-stop"
            ],
            blockers);
    }

    [Fact]
    public void IdleActivityHasNoBlockersAndUsesTransitionStatus()
    {
        IReadOnlyList<string> blockers = ModeTransitionPolicy.FindBlockers(
            new ModeTransitionActivity(
                Recording: false,
                ActiveMobileBackups: false,
                PendingRecordingUploads: 0,
                ActiveClipTasks: false,
                ActiveMediaFinalization: false,
                PhotoCaptureInProgress: false,
                PhotoArchiveInProgress: false,
                RecordingStartInProgress: false,
                RecordingStopInProgress: false));

        Assert.Empty(blockers);
        Assert.Equal("正在切换", ModeTransitionPolicy.StatusText(blockers));
    }

    [Theory]
    [InlineData("recording", "等待录像完成")]
    [InlineData("mobile-backup", "等待备份完成")]
    [InlineData("recording-upload", "等待上传完成")]
    [InlineData("clip-task", "等待剪辑完成")]
    [InlineData("media-finalization", "等待文件收尾")]
    [InlineData("photo-capture", "等待拍照保存完成")]
    [InlineData("photo-archive", "等待照片归档完成")]
    [InlineData("recording-start", "等待录像启动完成")]
    [InlineData("recording-stop", "等待录像收尾完成")]
    public void PolicyProvidesSpecificWaitStatus(string blocker, string expected)
    {
        Assert.Equal(
            expected,
            ModeTransitionPolicy.StatusText([blocker]));
    }

    [Fact]
    public void GateRejectsConcurrentTransitionsUntilReleased()
    {
        var gate = new ModeTransitionGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.True(gate.TryEnter());
        gate.Exit();
    }
}
