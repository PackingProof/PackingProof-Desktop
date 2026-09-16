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

internal sealed class CameraFrameRateGate
{
    private long _lastAcceptedTimestamp;

    public void Reset() => Interlocked.Exchange(ref _lastAcceptedTimestamp, 0);

    public bool ShouldAccept(bool isRecording, int targetFps) =>
        ShouldAccept(isRecording, targetFps, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal bool ShouldAccept(bool isRecording, int targetFps, long nowTimestamp, long timestampFrequency)
    {
        if (isRecording)
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
