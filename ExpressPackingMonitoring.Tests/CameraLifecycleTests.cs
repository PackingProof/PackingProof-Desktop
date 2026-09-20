using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraLifecycleTests
{
    /// <summary>
    /// 空闲时的处理帧率跟预览档位走：以前写死 15fps 采集 / 24fps 处理，
    /// 60fps 摄像头下预览一直被压在 15fps，看起来就是"卡"。
    /// </summary>
    [Theory]
    [InlineData(false, 60, 60, 60)]
    [InlineData(false, 60, 12, 12)]
    [InlineData(false, 60, 4, 4)]
    [InlineData(false, 10, 60, 10)]
    [InlineData(true, 60, 4, 60)]
    [InlineData(true, 0, 4, 15)]
    public void CameraFrameProcessingPolicy_FollowsPreviewTierAndKeepsRecordingFps(
        bool isRecording,
        int actualCameraFps,
        int idleTargetFps,
        int expectedFps)
    {
        Assert.Equal(expectedFps, CameraFrameProcessingPolicy.GetCaptureFps(isRecording, actualCameraFps, idleTargetFps));
        Assert.Equal(expectedFps, CameraFrameProcessingPolicy.GetProcessingFps(isRecording, actualCameraFps, idleTargetFps));
    }

    [Fact]
    public void CameraFrameRateGate_ThrottlesIdleFramesButAcceptsEveryRecordingFrame()
    {
        var gate = new CameraFrameRateGate();
        const long frequency = 1_000;

        // 满帧档位（60fps）：间隔约 17 tick
        Assert.True(gate.ShouldAccept(false, 60, 1_000, frequency));
        Assert.False(gate.ShouldAccept(false, 60, 1_010, frequency));
        Assert.True(gate.ShouldAccept(false, 60, 1_017, frequency));

        Assert.True(gate.ShouldAccept(true, 60, 1_018, frequency));
        Assert.True(gate.ShouldAccept(true, 60, 1_019, frequency));
    }

    /// <summary>降帧档位下门限跟着档位走，否则"降到 12fps"不会真的生效。</summary>
    [Fact]
    public void CameraFrameRateGate_FollowsReducedTier()
    {
        var gate = new CameraFrameRateGate();
        const long frequency = 1_000;

        // 12fps 档位：间隔约 83 tick
        Assert.True(gate.ShouldAccept(false, 12, 1_000, frequency));
        Assert.False(gate.ShouldAccept(false, 12, 1_050, frequency));
        Assert.True(gate.ShouldAccept(false, 12, 1_084, frequency));
    }

    [Fact]
    public void CameraReconnectPolicy_DoesNotRestartHealthyCameraForPreviewOrEncoderBackpressure()
    {
        Assert.Equal(
            PreviewFreezeRecoveryAction.ResetPreviewPipeline,
            CameraReconnectPolicy.GetPreviewFreezeRecovery(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void CameraReconnectPolicy_AllowsRestartAfterCameraFramesActuallyStop()
    {
        Assert.Equal(
            PreviewFreezeRecoveryAction.RestartCamera,
            CameraReconnectPolicy.GetPreviewFreezeRecovery(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3)));
    }

    /// <summary>
    /// 设备能启动却立刻报错（虚拟摄像头 DLL 初始化失败）时必须按启动失败退避重试：
    /// 以前错误回调立刻重启、重启又立刻被判成功，10ms 一轮把界面、日志和句柄一起拖死。
    /// </summary>
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(2.9, true)]
    [InlineData(3.0, true)]
    [InlineData(3.1, false)]
    [InlineData(600.0, false)]
    public void CameraStartupFailurePolicy_OnlyEarlyErrorsCountAsStartupFailure(double elapsedSeconds, bool expected)
    {
        Assert.Equal(
            expected,
            CameraStartupFailurePolicy.IsStartupFailure(TimeSpan.FromSeconds(elapsedSeconds)));
    }

    [Fact]
    public void CameraStartupFailurePolicy_NegativeElapsedTimeIsNotStartupFailure()
    {
        Assert.False(CameraStartupFailurePolicy.IsStartupFailure(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(9, 10)]
    public void CameraStartupFailurePolicy_BackoffGrowsThenStaysBounded(int consecutiveFailures, double expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            CameraStartupFailurePolicy.GetRestartBackoff(consecutiveFailures));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void CameraStartupFailurePolicy_StopsAutoReconnectAtFailureBudget(int consecutiveFailures, bool expected)
    {
        Assert.Equal(
            expected,
            CameraStartupFailurePolicy.ShouldStopAutoReconnect(consecutiveFailures, 5));
    }

    /// <summary>
    /// 唤醒休眠摄像头时看门狗不能抢着判定掉线：启动窗口里设备本来就没在跑、帧时间也是旧的，
    /// 判掉线就会排队重连，把刚起来的摄像头又关掉（现场：反复"正在重连"、十几秒连不上）。
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false, false, false, 0, 600, false)]
    [InlineData(false, true, false, false, false, false, 0, 600, false)]
    [InlineData(false, false, true, false, false, false, 0, 600, false)]
    [InlineData(false, false, false, true, false, false, 0, 600, false)]
    [InlineData(false, false, false, false, true, false, 0, 600, false)]
    [InlineData(false, false, false, false, false, true, 0, 600, false)]
    [InlineData(false, false, false, false, false, false, 5, 600, false)]
    [InlineData(false, false, false, false, false, false, 2, 5, false)]
    [InlineData(false, false, false, false, false, false, 0, 600, true)]
    [InlineData(false, false, false, false, false, false, 2, 6, true)]
    public void CameraWatchdogPolicy_OnlyJudgesCameraLostWhenNothingElseExplainsIt(
        bool cameraSleeping,
        bool setupWizardActive,
        bool cameraStarting,
        bool cameraRestarting,
        bool autoReconnectSuspended,
        bool startupRetryPending,
        int consecutiveRestartFailures,
        double sinceLastRestartSeconds,
        bool expected)
    {
        CameraWatchdogState state = new(
            cameraSleeping,
            setupWizardActive,
            cameraStarting,
            cameraRestarting,
            autoReconnectSuspended,
            startupRetryPending,
            consecutiveRestartFailures,
            5,
            TimeSpan.FromSeconds(sinceLastRestartSeconds),
            3.0);

        Assert.Equal(expected, CameraWatchdogPolicy.CanJudgeCameraLost(state));
    }

    [Theory]
    [InlineData(4.9, 4.0, false)]
    [InlineData(5.0, 2.9, false)]
    [InlineData(5.0, 3.0, true)]
    [InlineData(60.0, 3.1, true)]
    public void RecordingFrameProgressPolicy_RecoversOnlyAfterStartupGraceAndSustainedStall(
        double recordingAgeSeconds,
        double frameProgressAgeSeconds,
        bool expected)
    {
        bool shouldRecover = RecordingFrameProgressPolicy.ShouldRecover(
            TimeSpan.FromSeconds(recordingAgeSeconds),
            TimeSpan.FromSeconds(frameProgressAgeSeconds));

        Assert.Equal(expected, shouldRecover);
    }

    [Fact]
    public void RecordingFramePipelineDiagnostics_CapturesStalledStageAndFrameIdentity()
    {
        var diagnostics = new RecordingFramePipelineDiagnostics();
        diagnostics.Enter(
            RecordingFramePipelineStage.Watermark,
            frameSequence: 321,
            timestamp: 1_000,
            managedThreadId: 17);

        RecordingFramePipelineSnapshot snapshot = diagnostics.Capture(
            timestamp: 4_500,
            timestampFrequency: 1_000);

        Assert.Equal(RecordingFramePipelineStage.Watermark, snapshot.Stage);
        Assert.Equal(TimeSpan.FromSeconds(3.5), snapshot.StageAge);
        Assert.Equal(321, snapshot.FrameSequence);
        Assert.Equal(17, snapshot.ManagedThreadId);
        Assert.Contains("stage=Watermark", snapshot.ToLogText());
        Assert.Contains("stageFrame=321", snapshot.ToLogText());
    }

    [Theory]
    [InlineData(900, 1_000)]
    [InlineData(2_000, 0)]
    public void RecordingFramePipelineDiagnostics_InvalidClockSampleDoesNotReportNegativeAge(
        long timestamp,
        long timestampFrequency)
    {
        var diagnostics = new RecordingFramePipelineDiagnostics();
        diagnostics.Enter(
            RecordingFramePipelineStage.RecorderEnqueue,
            frameSequence: 1,
            timestamp: 1_000,
            managedThreadId: 2);

        RecordingFramePipelineSnapshot snapshot = diagnostics.Capture(timestamp, timestampFrequency);

        Assert.Equal(TimeSpan.Zero, snapshot.StageAge);
    }

    [Fact]
    public void PreviewSessionGate_StaleCallbackCannotReleaseAwakenedSession()
    {
        var gate = new PreviewSessionGate();
        int sleepingSession = gate.BeginSession();
        Assert.True(gate.TryAcquire(out int oldCallbackSession));
        Assert.Equal(sleepingSession, oldCallbackSession);

        int awakenedSession = gate.BeginSession();
        Assert.NotEqual(sleepingSession, awakenedSession);
        Assert.True(gate.TryAcquire(out int awakenedCallbackSession));

        gate.Release(oldCallbackSession);

        Assert.True(gate.IsPending);
        Assert.False(gate.TryAcquire(out _));
        gate.Release(awakenedCallbackSession);
        Assert.False(gate.IsPending);
        Assert.True(gate.TryAcquire(out int nextCallbackSession));
        Assert.Equal(awakenedSession, nextCallbackSession);
    }

    [Fact]
    public async Task CameraFrameReadySignal_WakeRequiresNewSessionFrame()
    {
        var signal = new CameraFrameReadySignal();
        signal.Signal();
        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(20)));

        signal.BeginSession();
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(20)));

        Task<bool> awakenedFrame = signal.WaitAsync(TimeSpan.FromSeconds(1));
        signal.Signal();

        Assert.True(await awakenedFrame);
    }

    [Fact]
    public async Task CameraFrameReadySignal_RecordingStartTimesOutWithoutFrame()
    {
        var signal = new CameraFrameReadySignal();
        signal.BeginSession();

        bool ready = await signal.WaitAsync(TimeSpan.FromMilliseconds(30));

        Assert.False(ready);
    }

    /// <summary>
    /// 处理循环等的是"下一帧到达"，不能靠睡固定时长轮询：
    /// 轮询与摄像头节拍错开时会整整丢掉一拍，录制帧率掉到 47fps 而编码器按 60fps 打时间戳。
    /// </summary>
    [Fact]
    public async Task CameraFrameArrivalGate_WakesOnEveryNewFrame()
    {
        var gate = new CameraFrameArrivalGate();

        Task<bool> waiting = gate.WaitAsync(TimeSpan.FromSeconds(1));
        gate.Signal();

        Assert.True(await waiting);
    }

    [Fact]
    public async Task CameraFrameArrivalGate_TimesOutWhenCameraStopsDelivering()
    {
        var gate = new CameraFrameArrivalGate();

        Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(30)));
    }

    /// <summary>只保留一次通知：处理慢时宁可跳到最新帧，也不要把旧帧排成队。</summary>
    [Fact]
    public async Task CameraFrameArrivalGate_CoalescesPendingNotifications()
    {
        var gate = new CameraFrameArrivalGate();
        gate.Signal();
        gate.Signal();
        gate.Signal();

        Assert.True(await gate.WaitAsync(TimeSpan.FromMilliseconds(50)));
        Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task CameraFrameArrivalGate_DrainClearsPendingNotification()
    {
        var gate = new CameraFrameArrivalGate();
        gate.Signal();

        gate.Drain();

        Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(30)));
    }

    /// <summary>
    /// 处理循环必须取走整帧所有权，不能每轮整帧克隆：1080p 一帧 6MB，60fps 就是 360MB/s 的拷贝，
    /// 而且原来那次拷贝还在 _frameLock 里做，采集线程要等它拷完才能发布下一帧。
    /// </summary>
    [Fact]
    public void VideoProcessLoopTakesFrameOwnershipInsteadOfCloningEveryFrame()
    {
        string source = RepositorySource.ReadMainViewModel();
        int index = source.IndexOf("private async Task VideoProcessLoop", StringComparison.Ordinal);
        Assert.True(index >= 0, "未找到 VideoProcessLoop");
        string loop = source[index..];

        Assert.Contains("_latestCameraFrame.Take()", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("currentFrame = _latestFrame.Clone()", loop, StringComparison.Ordinal);
    }
}
