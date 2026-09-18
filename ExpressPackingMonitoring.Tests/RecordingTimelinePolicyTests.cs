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

    [Theory]
    [InlineData(21.33, 21)]
    [InlineData(59.94, 60)]
    [InlineData(30.2, 30)]
    public void FractionalCaptureRateDoesNotAccumulateClockDrift(double captureFps, int encoderFps)
    {
        const int preRecord = 43;
        const double seconds = 600;
        long start = Frequency;
        long written = preRecord;
        int skipped = 0;
        for (int frame = 0; frame < captureFps * seconds; frame++)
        {
            long captured = start + (long)(frame / captureFps * Frequency);
            int target = RecordingTimelinePolicy.CalculateLiveFrameTarget(start, captured, encoderFps);
            if (RecordingTimelinePolicy.CalculateLiveWrittenFrames(written, preRecord) >= target)
            {
                skipped++;
                continue;
            }
            written++;
            written += RecordingTimelinePolicy.CalculateCatchUpFrames(target, written, encoderFps, preRecord);
        }
        double liveSeconds = (written - preRecord) / (double)encoderFps;
        Assert.InRange(Math.Abs(liveSeconds - seconds), 0, 1d / encoderFps);
        if (captureFps > encoderFps) Assert.True(skipped > 0);
    }

    [Fact]
    public void QueuedFrameTargetUsesCaptureTimeAndRejectsPreSessionFrames()
    {
        long start = Frequency;
        Assert.Equal(0, RecordingTimelinePolicy.CalculateLiveFrameTarget(start, start - 1, 30));
        Assert.Equal(1, RecordingTimelinePolicy.CalculateLiveFrameTarget(start, start, 30));
        // 即使队列耗时数秒，采集后 100ms 的画面仍属于第四帧。
        Assert.Equal(4, RecordingTimelinePolicy.CalculateLiveFrameTarget(start, start + Frequency / 10, 30));
    }

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

    /// <summary>
    /// 现场回归：预录 296 帧还在写入时墙钟已经走了几秒，按实时帧数判断会误以为"落后"，
    /// 于是把重复帧盖到预录画面上——录出来预录段变成同一帧定格，文件还长了 5 秒。
    /// 预录段必须原样写入，一帧都不补。
    /// </summary>
    [Fact]
    public void PreRecordFramesAreNeverPadded()
    {
        const int fps = 60;
        const int preRecordFrames = 296;

        for (int written = 1; written <= preRecordFrames; written++)
        {
            int catchUp = RecordingTimelinePolicy.CalculateCatchUpFrames(
                expectedFrames: 600,           // 墙钟已经走了 10 秒
                writtenFrames: written,
                fps: fps,
                preRecordFrames: preRecordFrames);

            Assert.Equal(0, catchUp);
        }
    }

    /// <summary>预录段写完之后，实时段照常补齐（补的是实时画面，不是预录画面）。</summary>
    [Fact]
    public void LivePhaseStillCatchesUpAfterPreRecordIsWritten()
    {
        const int fps = 60;
        const int preRecordFrames = 296;

        // 预录写完、实时才写了 1 帧，墙钟已过 1 秒：应当补到上限
        int catchUp = RecordingTimelinePolicy.CalculateCatchUpFrames(
            expectedFrames: 60,
            writtenFrames: preRecordFrames + 1,
            fps: fps,
            preRecordFrames: preRecordFrames);

        Assert.Equal(30, catchUp);
    }

    /// <summary>没有预录时行为不变。</summary>
    [Fact]
    public void WithoutPreRecordPaddingBehavesAsBefore()
    {
        Assert.False(RecordingTimelinePolicy.IsWritingPreRecordFrames(0, 0));
        Assert.False(RecordingTimelinePolicy.IsWritingPreRecordFrames(5, 0));

        Assert.Equal(5, RecordingTimelinePolicy.CalculateCatchUpFrames(
            expectedFrames: 60,
            writtenFrames: 55,
            fps: 60,
            preRecordFrames: 0));
    }

    [Fact]
    public void PreRecordWindowIsRecognisedExactlyOnce()
    {
        Assert.True(RecordingTimelinePolicy.IsWritingPreRecordFrames(1, 296));
        Assert.True(RecordingTimelinePolicy.IsWritingPreRecordFrames(296, 296));
        Assert.False(RecordingTimelinePolicy.IsWritingPreRecordFrames(297, 296));
    }

    /// <summary>
    /// 逐帧模拟现场那条录像的时间线：296 帧预录先灌进管道（约 1 秒），
    /// 这段时间到达的实时帧被丢弃，之后实时只有 54fps。
    /// 断言：预录段一帧不补、盘尾补齐只发生在实时段、文件时长与真实时长对齐。
    /// </summary>
    [Fact]
    public void TimelineSimulationKeepsPreRecordIntactAndRealTimeLength()
    {
        const int fps = 60;
        const int preRecordFrames = 296;
        const double flushSeconds = 1.0;
        const int liveFps = 54;
        const double recordingSeconds = 17.0;

        long written = 0;
        long duplicated = 0;
        long paddedDuringPreRecord = 0;
        long lostDuringFlush = (long)(flushSeconds * fps);

        // 预录段：一次性灌入，墙钟同时前进
        for (int i = 0; i < preRecordFrames; i++)
        {
            written++;
            double burstProgressSeconds = flushSeconds * (i + 1) / preRecordFrames;
            int catchUp = RecordingTimelinePolicy.CalculateCatchUpFrames(
                expectedFrames: (int)(burstProgressSeconds * fps),
                writtenFrames: written,
                fps: fps,
                preRecordFrames: preRecordFrames);
            if (catchUp > 0 && RecordingTimelinePolicy.IsWritingPreRecordFrames(written, preRecordFrames))
                paddedDuringPreRecord += catchUp;
            written += catchUp;
            duplicated += catchUp;
        }

        // 实时段：54fps 到达，且开头 flushSeconds 内到达的帧已经丢了
        long liveArrived = 0;
        for (double t = flushSeconds; t <= recordingSeconds; t += 1.0 / liveFps)
        {
            liveArrived++;
            if (liveArrived <= lostDuringFlush)
                continue; // 灌入窗口内被队列丢掉的实时帧

            written++;
            int catchUp = RecordingTimelinePolicy.CalculateCatchUpFrames(
                expectedFrames: (int)((t - 0) * fps),
                writtenFrames: written,
                fps: fps,
                preRecordFrames: preRecordFrames);
            if (catchUp > 0 && RecordingTimelinePolicy.IsWritingPreRecordFrames(written, preRecordFrames))
                paddedDuringPreRecord += catchUp;
            written += catchUp;
            duplicated += catchUp;
        }

        double fileSeconds = RecordingTimelinePolicy.CalculateFileSeconds(written, fps);
        double wallSeconds = RecordingTimelinePolicy.CalculateWallSeconds(preRecordFrames, fps, (long)(recordingSeconds * Frequency));

        // 需要补的量 = 灌入窗口丢掉的实时帧 + 实时帧率相对声明帧率的缺口
        int expectedLiveFrames = (int)(recordingSeconds * fps);
        int arrivedLiveFrames = (int)((recordingSeconds - flushSeconds) * liveFps);
        long expectedPadding = lostDuringFlush + (expectedLiveFrames - arrivedLiveFrames);

        Assert.Equal(0, paddedDuringPreRecord);                                  // 预录段绝不能被补
        Assert.InRange(duplicated, expectedPadding - 35, expectedPadding + 35);  // 补的是真丢掉的量
        Assert.True(Math.Abs(fileSeconds - wallSeconds) < 1.0);                  // 文件时长贴着真实时长
    }
}
