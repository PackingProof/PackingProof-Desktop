using System.Diagnostics;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 录像时间轴对齐：现场实测 10.29 秒的录制只有 492 帧（8.2 秒文件），
/// 即 47.8fps 喂给 60fps 编码器，录出来的片段比真实时间快 20% 以上，音轨是真实时间，
/// 因此越到后面越不同步。这里固定"按墙钟补齐帧数"的算法。
/// </summary>
public sealed class RecordingTimelinePolicyTests
{
    private static readonly long Frequency = Stopwatch.Frequency;

    [Fact]
    public void ExpectedFramesFollowWallClock()
    {
        long start = 1_000_000;
        long oneSecondLater = start + Frequency;

        Assert.Equal(0, RecordingTimelinePolicy.CalculateExpectedFrameCount(start, start, 60));
        Assert.Equal(60, RecordingTimelinePolicy.CalculateExpectedFrameCount(start, oneSecondLater, 60));
        Assert.Equal(30, RecordingTimelinePolicy.CalculateExpectedFrameCount(start, start + Frequency / 2, 60));
        Assert.Equal(50, RecordingTimelinePolicy.CalculateExpectedFrameCount(start, oneSecondLater, 50));
    }

    [Fact]
    public void ExpectedFramesAreZeroUntilTimelineStartIsKnown()
    {
        Assert.Equal(0, RecordingTimelinePolicy.CalculateExpectedFrameCount(0, Frequency, 60));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateExpectedFrameCount(-5, Frequency, 60));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateExpectedFrameCount(1000, 500, 60));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateExpectedFrameCount(1000, 1000 + Frequency, 0));
    }

    [Fact]
    public void CatchUpCompensatesTheShortfall()
    {
        Assert.Equal(5, RecordingTimelinePolicy.CalculateCatchUpFrames(expectedFrames: 600, writtenFrames: 595, fps: 60));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateCatchUpFrames(expectedFrames: 600, writtenFrames: 600, fps: 60));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateCatchUpFrames(expectedFrames: 600, writtenFrames: 640, fps: 60));
    }

    /// <summary>落后太多时不用重复帧把卡顿摊平，只补半秒的量。</summary>
    [Fact]
    public void CatchUpIsBoundedPerFrame()
    {
        Assert.Equal(30, RecordingTimelinePolicy.MaxCatchUpFrames(60));
        Assert.Equal(30, RecordingTimelinePolicy.CalculateCatchUpFrames(expectedFrames: 6000, writtenFrames: 0, fps: 60));

        int tiny = RecordingTimelinePolicy.MaxCatchUpFrames(1);
        Assert.True(tiny >= 1);
    }

    /// <summary>现场那次失败录制的复现：10.29 秒真实时间、492 帧、声明 60fps。</summary>
    [Fact]
    public void MeasuredShortRecordingWouldHaveBeenCorrected()
    {
        const int fps = 60;
        long start = 10_000_000;
        // 真实录制 10.29 秒后停止，此刻文件里应当有的帧数
        int expected = RecordingTimelinePolicy.CalculateExpectedFrameCount(
            start,
            start + (long)(10.29 * Frequency),
            fps);

        Assert.InRange(expected, 616, 618);
        // 实际只写了 492 帧，差 125 帧左右（2 秒多），单帧上限 30 帧，需要多次补齐
        int firstCatchUp = RecordingTimelinePolicy.CalculateCatchUpFrames(expected, writtenFrames: 492, fps: fps);
        Assert.Equal(30, firstCatchUp);
    }

    /// <summary>预录帧属于时间轴最前面一段，不能算成实时段的"领先"。</summary>
    [Fact]
    public void PreRecordFramesDoNotCountAsLiveProgress()
    {
        // 5 秒预录 = 302 帧先写进去，实时段此刻才刚开始：实时进度必须是 0，否则补齐要等 5 秒才生效
        Assert.Equal(0, RecordingTimelinePolicy.CalculateLiveWrittenFrames(writtenFrames: 302, preRecordFrames: 302));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateLiveWrittenFrames(writtenFrames: 250, preRecordFrames: 302));
        Assert.Equal(60, RecordingTimelinePolicy.CalculateLiveWrittenFrames(writtenFrames: 362, preRecordFrames: 302));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateLiveWrittenFrames(writtenFrames: -5, preRecordFrames: 0));
    }

    /// <summary>自检日志两个口径都要含预录那一段，才能直接比较。</summary>
    [Fact]
    public void SelfCheckSecondsIncludePreRecord()
    {
        const int fps = 60;
        int preRecordFrames = 302;          // 5.03 秒预录
        long liveElapsed = (long)(29.0 * Frequency);

        double fileSeconds = RecordingTimelinePolicy.CalculateFileSeconds(2043, fps);
        double wallSeconds = RecordingTimelinePolicy.CalculateWallSeconds(preRecordFrames, fps, liveElapsed);

        // 现场那条录像：文件 34.05 秒 / 真实 5.03+29.0 = 34.03 秒
        Assert.InRange(fileSeconds, 34.0, 34.1);
        Assert.InRange(wallSeconds, 34.0, 34.1);
        Assert.True(Math.Abs(fileSeconds - wallSeconds) < 0.2);
    }

    [Fact]
    public void WallSecondsIsZeroSafeBeforeTimelineIsKnown()
    {
        Assert.Equal(0, RecordingTimelinePolicy.CalculateWallSeconds(0, 60, 0));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateFileSeconds(0, 60));
        Assert.Equal(0, RecordingTimelinePolicy.CalculateFileSeconds(120, 0));
    }
}
