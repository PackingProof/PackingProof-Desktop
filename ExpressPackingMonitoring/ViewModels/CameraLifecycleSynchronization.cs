using System.Diagnostics;

namespace ExpressPackingMonitoring.ViewModels;

internal static class CameraFrameProcessingPolicy
{
    /// <summary>
    /// 采集门限：录制时跟摄像头帧率，空闲时按预览档位（满帧/12fps/4fps）。
    /// 原来空闲写死 15fps，摄像头 60fps 时预览被压在 15fps，看起来一直"卡"。
    /// </summary>
    public static int GetCaptureFps(bool isRecording, int actualCameraFps, int idleTargetFps)
    {
        int cameraFps = actualCameraFps > 0 ? actualCameraFps : PreviewFrameRatePolicy.FallbackCameraFps;
        return isRecording
            ? Math.Clamp(cameraFps, 1, 120)
            : Math.Clamp(Math.Min(cameraFps, idleTargetFps), 1, 120);
    }

    /// <summary>处理循环频率：与采集门限同一档位，否则处理循环自己会把预览压回去。</summary>
    public static int GetProcessingFps(bool isRecording, int actualCameraFps, int idleTargetFps) =>
        GetCaptureFps(isRecording, actualCameraFps, idleTargetFps);
}

internal static class RecordingFrameProgressPolicy
{
    internal static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);

    public static bool ShouldRecover(TimeSpan recordingAge, TimeSpan frameProgressAge) =>
        recordingAge >= StartupGracePeriod
        && frameProgressAge >= StallThreshold;
}

internal enum RecordingFramePipelineStage
{
    Idle,
    Startup,
    PreRecordWatermark,
    PreRecordEnqueue,
    AcquireLatestFrame,
    PairingQr,
    BarcodeRecognition,
    FrameMetadata,
    SmartZoom,
    Watermark,
    MotionDetection,
    PreviewPublish,
    RecorderEnqueue,
    FrameCleanup,
    HealthCheck,
    WaitingForNextFrame,
    NoFrame
}

internal readonly record struct RecordingFramePipelineSnapshot(
    RecordingFramePipelineStage Stage,
    TimeSpan StageAge,
    long FrameSequence,
    int ManagedThreadId,
    bool ThreadIsAlive,
    string ThreadState)
{
    public string ToLogText() =>
        $"stage={Stage}, stageAge={StageAge.TotalSeconds:F1}s, stageFrame={FrameSequence}, managedThread={ManagedThreadId}, threadAlive={ThreadIsAlive}, threadState={ThreadState}";
}

internal sealed class RecordingFramePipelineDiagnostics
{
    private int _stage;
    private long _stageStartedTimestamp;
    private long _frameSequence;
    private int _managedThreadId;
    private Thread? _thread;

    public void Enter(RecordingFramePipelineStage stage, long frameSequence) =>
        Enter(stage, frameSequence, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, Thread.CurrentThread);

    internal void Enter(
        RecordingFramePipelineStage stage,
        long frameSequence,
        long timestamp,
        int managedThreadId) =>
        Enter(stage, frameSequence, timestamp, managedThreadId, null);

    public RecordingFramePipelineSnapshot Capture() =>
        Capture(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal RecordingFramePipelineSnapshot Capture(long timestamp, long timestampFrequency)
    {
        var stage = (RecordingFramePipelineStage)Volatile.Read(ref _stage);
        long stageStartedTimestamp = Volatile.Read(ref _stageStartedTimestamp);
        long frameSequence = Volatile.Read(ref _frameSequence);
        int managedThreadId = Volatile.Read(ref _managedThreadId);
        Thread? thread = Volatile.Read(ref _thread);

        long elapsedTicks = timestampFrequency > 0
            && stageStartedTimestamp > 0
            && timestamp >= stageStartedTimestamp
            ? timestamp - stageStartedTimestamp
            : 0;
        TimeSpan stageAge = timestampFrequency > 0
            ? TimeSpan.FromSeconds(elapsedTicks / (double)timestampFrequency)
            : TimeSpan.Zero;

        bool threadIsAlive = false;
        string threadState = "Unavailable";
        if (thread != null)
        {
            try
            {
                threadIsAlive = thread.IsAlive;
                threadState = thread.ThreadState.ToString();
            }
            catch
            {
            }
        }

        return new RecordingFramePipelineSnapshot(
            stage,
            stageAge,
            frameSequence,
            managedThreadId,
            threadIsAlive,
            threadState);
    }

    private void Enter(
        RecordingFramePipelineStage stage,
        long frameSequence,
        long timestamp,
        int managedThreadId,
        Thread? thread)
    {
        Volatile.Write(ref _stageStartedTimestamp, timestamp);
        Volatile.Write(ref _frameSequence, frameSequence);
        Volatile.Write(ref _managedThreadId, managedThreadId);
        Volatile.Write(ref _thread, thread);
        Volatile.Write(ref _stage, (int)stage);
    }
}

internal enum PreviewFreezeRecoveryAction
{
    ResetPreviewPipeline,
    RestartCamera
}

internal static class CameraReconnectPolicy
{
    public static PreviewFreezeRecoveryAction GetPreviewFreezeRecovery(
        TimeSpan sinceLastFrame,
        TimeSpan staleFrameThreshold) =>
        sinceLastFrame <= staleFrameThreshold
            ? PreviewFreezeRecoveryAction.ResetPreviewPipeline
            : PreviewFreezeRecoveryAction.RestartCamera;
}

/// <summary>
/// 摄像头启动失败的判定与重连退避。
///
/// 现场问题：选中的虚拟摄像头能 StartCamera 成功（IsRunning=true），紧接着抛
/// VideoSourceError（例如 0x8007045A DLL 初始化失败）。原来的错误回调没有冷却，
/// 而重启流程只看 IsRunning 就把"重连成功"写回，清零失败计数，于是
/// 错误 → 重启 → 错误 不到 10ms 一轮：界面卡死、CPU 占满、日志暴涨、句柄泄漏。
///
/// 规则：启动后 <see cref="StartupErrorWindow"/> 内收到的错误算"启动失败"，
/// 按 1s / 2s / 5s / 10s 退避重试；连续失败到上限后停止自动重连并提示用户。
/// 只有真的收到画面帧才算恢复，所以"能启动、不给帧"的设备不会被当成连接成功。
/// </summary>
internal static class CameraStartupFailurePolicy
{
    /// <summary>启动后这段时间内收到的错误算启动失败，而不是运行中断线。</summary>
    internal static readonly TimeSpan StartupErrorWindow = TimeSpan.FromSeconds(3);

    public static bool IsStartupFailure(TimeSpan sinceStart) =>
        sinceStart >= TimeSpan.Zero && sinceStart <= StartupErrorWindow;

    /// <summary>连续启动失败后的重试间隔：1s、2s、5s，之后保持 10s。</summary>
    public static TimeSpan GetRestartBackoff(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(10)
    };

    public static bool ShouldStopAutoReconnect(int consecutiveFailures, int maxFailures) =>
        consecutiveFailures >= maxFailures;
}

/// <summary>
/// 看门狗的一次判定输入。字段全部是"当前状态 + 上限/间隔常量"，便于单独测试。
/// </summary>
internal readonly record struct CameraWatchdogState(
    bool CameraSleeping,
    bool SetupWizardActive,
    bool CameraStarting,
    bool CameraRestarting,
    bool AutoReconnectSuspended,
    bool StartupRetryPending,
    int ConsecutiveRestartFailures,
    int MaxRestartFailures,
    TimeSpan SinceLastRestartAttempt,
    double MinRestartIntervalSeconds);

/// <summary>
/// 看门狗（VideoProcessLoop 的"没有新帧"分支）能不能判定摄像头掉线并重连。
///
/// 现场问题：唤醒休眠摄像头时先放开休眠标记再 StartCamera，看门狗在这中间看到
/// "设备不在跑 + 帧时间过旧"，于是每 200ms 往 UI 线程排一次重连；而启动一次要 1 秒多，
/// 排进去的请求会在启动完成后依次执行，把关掉的摄像头又一个个重开（Media Foundation
/// 停一次约 1.8 秒），表现就是反复播"正在重连"、十几秒都拿不到画面。
///
/// 所以"正在启动"必须和休眠、设置向导、正在重启一样算作不可判定状态；
/// 顺手把原先散在 if/else 链里的所有跳过条件收在一起，避免以后再漏。
/// </summary>
internal static class CameraWatchdogPolicy
{
    public static bool CanJudgeCameraLost(in CameraWatchdogState state) =>
        !state.CameraSleeping
        && !state.SetupWizardActive
        && !state.CameraStarting
        && !state.CameraRestarting
        && !state.AutoReconnectSuspended
        && !state.StartupRetryPending
        && state.ConsecutiveRestartFailures < state.MaxRestartFailures
        && state.SinceLastRestartAttempt.TotalSeconds
            >= state.MinRestartIntervalSeconds * Math.Max(1, state.ConsecutiveRestartFailures);
}

internal sealed class CameraFrameRateGate
{
    private long _lastAcceptedTimestamp;

    public void Reset() => Interlocked.Exchange(ref _lastAcceptedTimestamp, 0);

    public bool ShouldAccept(bool acceptEveryFrame, int targetFps) =>
        ShouldAccept(acceptEveryFrame, targetFps, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal bool ShouldAccept(bool acceptEveryFrame, int targetFps, long nowTimestamp, long timestampFrequency)
    {
        if (acceptEveryFrame)
        {
            Interlocked.Exchange(ref _lastAcceptedTimestamp, nowTimestamp);
            return true;
        }

        int fps = Math.Clamp(targetFps, 1, 120);
        long minimumInterval = Math.Max(1, timestampFrequency / fps);

        while (true)
        {
            long previous = Volatile.Read(ref _lastAcceptedTimestamp);
            if (previous != 0 && nowTimestamp - previous < minimumInterval)
                return false;

            if (Interlocked.CompareExchange(ref _lastAcceptedTimestamp, nowTimestamp, previous) == previous)
                return true;
        }
    }
}

internal sealed class PreviewSessionGate
{
    private int _sessionId;
    private int _pending;

    public int CurrentSessionId => Volatile.Read(ref _sessionId);
    public bool IsPending => Volatile.Read(ref _pending) != 0;

    public int BeginSession()
    {
        int sessionId = Interlocked.Increment(ref _sessionId);
        Interlocked.Exchange(ref _pending, 0);
        return sessionId;
    }

    public bool TryAcquire(out int sessionId)
    {
        sessionId = CurrentSessionId;
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return false;

        if (sessionId == CurrentSessionId)
            return true;

        Interlocked.Exchange(ref _pending, 0);
        return false;
    }

    public bool IsCurrent(int sessionId) => sessionId == CurrentSessionId;

    public void Release(int sessionId)
    {
        if (IsCurrent(sessionId))
            Interlocked.Exchange(ref _pending, 0);
    }

    public void ClearCurrentPending() => Interlocked.Exchange(ref _pending, 0);
}

/// <summary>预览仅保留最新一帧；转移所有权后由接收方释放，跨采集会话的帧直接丢弃。</summary>
internal sealed class LatestPreviewFrameSlot<T> where T : class, IDisposable
{
    private readonly object _sync = new();
    private int _sessionId;
    private T? _frame;
    private long _capturedTicks;

    public void Reset(int sessionId)
    {
        T? previous;
        lock (_sync)
        {
            if (sessionId < _sessionId) return;
            _sessionId = sessionId;
            previous = _frame;
            _frame = null;
        }
        previous?.Dispose();
    }

    public void Publish(int sessionId, T frame, long capturedTicks)
    {
        T? discarded;
        lock (_sync)
        {
            if (sessionId != _sessionId)
                discarded = frame;
            else
            {
                discarded = _frame;
                _frame = frame;
                _capturedTicks = capturedTicks;
            }
        }
        discarded?.Dispose();
    }

    public T? Take(int sessionId, out long capturedTicks)
    {
        lock (_sync)
        {
            capturedTicks = _capturedTicks;
            if (sessionId != _sessionId) return null;
            T? frame = _frame;
            _frame = null;
            return frame;
        }
    }

    public bool HasFrame(int sessionId)
    {
        lock (_sync) return sessionId == _sessionId && _frame != null;
    }
}

/// <summary>
/// 采集帧交接槽：摄像头回调把整帧所有权直接交给处理循环，循环取走后自行释放，
/// 还没被取走就被下一帧替换的帧由这里释放。
///
/// 处理循环原来每轮都 <c>_latestFrame.Clone()</c>：1080p 一帧 6MB，60fps 就是
/// 360MB/s 的整帧拷贝，而且拷贝还在 <c>_frameLock</c> 里做，采集线程要等它拷完
/// 才能发布下一帧。改成所有权交接后，同一条链路上不再有整帧拷贝。
///
/// 只保留最新一帧：处理慢的时候宁可跳到新帧，也不积压旧帧。
/// </summary>
internal sealed class LatestFrameHandoffSlot<T> where T : class, IDisposable
{
    private readonly object _sync = new();
    private T? _frame;

    /// <summary>发布一帧并接管所有权；调用方之后不得再引用或释放这一帧。</summary>
    public void Publish(T frame)
    {
        T? replaced;
        lock (_sync)
        {
            replaced = _frame;
            _frame = frame;
        }
        replaced?.Dispose();
    }

    /// <summary>取走最新一帧，调用方接管所有权；槽内没有帧时返回 null。</summary>
    public T? Take()
    {
        lock (_sync)
        {
            T? frame = _frame;
            _frame = null;
            return frame;
        }
    }

    /// <summary>丢弃槽内还没被取走的帧（停摄像头、断流清理时用）。</summary>
    public void Clear()
    {
        T? frame;
        lock (_sync)
        {
            frame = _frame;
            _frame = null;
        }
        frame?.Dispose();
    }
}

internal sealed class CameraFrameReadySignal
{
    private readonly object _sync = new();
    private TaskCompletionSource _source = CreateSource();

    public void BeginSession()
    {
        lock (_sync)
            _source = CreateSource();
    }

    public void Signal()
    {
        TaskCompletionSource source;
        lock (_sync)
            source = _source;
        source.TrySetResult();
    }

    public async Task<bool> WaitAsync(TimeSpan timeout)
    {
        Task task;
        lock (_sync)
            task = _source.Task;

        if (task.IsCompleted)
            return true;

        return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;
    }

    private static TaskCompletionSource CreateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 逐帧到达通知。摄像头线程每来一帧 Signal 一次，处理循环用带超时的 Wait 等到下一帧。
///
/// 处理循环原来在"这一帧已经处理过"时睡满一个帧间隔，只要轮询节拍与摄像头错开，
/// 就会整整丢掉一拍：60fps 的源实测只能喂到 47fps，而编码器按固定帧率生成时间戳，
/// 录出来的文件因此比真实时间快 20% 以上（音画不同步）。
///
/// 只保留一次通知、不排队：处理慢的时候宁可跳到最新帧，也不要积压旧帧。
/// </summary>
internal sealed class CameraFrameArrivalGate
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout) => _semaphore.WaitAsync(timeout);

    /// <summary>丢弃尚未消费的通知，用于重新开始一轮等待。</summary>
    public void Drain()
    {
        while (_semaphore.Wait(0))
        {
        }
    }
}
