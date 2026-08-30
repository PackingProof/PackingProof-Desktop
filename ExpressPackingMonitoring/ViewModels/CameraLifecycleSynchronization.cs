using System.Diagnostics;

namespace ExpressPackingMonitoring.ViewModels;

internal static class CameraFrameProcessingPolicy
{
    internal const int IdleCaptureFps = 15;
    internal const int IdleProcessingFps = 24;
    private const int FallbackCameraFps = 15;

    public static int GetCaptureFps(bool isRecording, int actualCameraFps)
    {
        int cameraFps = actualCameraFps > 0 ? actualCameraFps : FallbackCameraFps;
        return isRecording ? cameraFps : Math.Min(cameraFps, IdleCaptureFps);
    }

    public static int GetProcessingFps(bool isRecording, int actualCameraFps)
    {
        int cameraFps = actualCameraFps > 0 ? actualCameraFps : FallbackCameraFps;
        return isRecording ? cameraFps : Math.Min(cameraFps, IdleProcessingFps);
    }
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

    public bool ShouldAccept(bool isRecording, int actualCameraFps) =>
        ShouldAccept(isRecording, actualCameraFps, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal bool ShouldAccept(bool isRecording, int actualCameraFps, long nowTimestamp, long timestampFrequency)
    {
        if (isRecording)
        {
            Interlocked.Exchange(ref _lastAcceptedTimestamp, nowTimestamp);
            return true;
        }

        int targetFps = CameraFrameProcessingPolicy.GetCaptureFps(false, actualCameraFps);
        long minimumInterval = Math.Max(1, timestampFrequency / targetFps);

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
