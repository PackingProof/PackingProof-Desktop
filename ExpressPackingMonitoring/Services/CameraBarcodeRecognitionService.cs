using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

internal enum CameraBarcodeRecognitionState
{
    Idle,
    Candidate,
    Confirmed
}

internal sealed record CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState State, string Code = "");

internal sealed record CameraBarcodeObservation(string CandidateCode = "", string ConfirmedCode = "");

internal enum BarcodeRecordingDecisionAction
{
    Ignore,
    Queue,
    Start,
    Stop,
    Switch
}

internal sealed record BarcodeRecordingDecision(BarcodeRecordingDecisionAction Action, string Reason);

internal static class BarcodeRecordingDecisionPolicy
{
    public static BarcodeRecordingDecision Evaluate(
        string? value,
        bool fromCamera,
        bool canProcess,
        bool isRecording,
        string? recordingOrderId,
        bool sameBarcodeStopEnabled,
        bool inputOnCooldown,
        string? orderIdRegex)
    {
        if (!canProcess)
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "程序忙碌或正在关闭");

        string normalized = (value ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "空输入");

        if (fromCamera
            && CameraBarcodeCandidatePolicy.ShouldIgnoreCurrentRecordingCode(
                normalized,
                recordingOrderId,
                isRecording,
                sameBarcodeStopEnabled))
        {
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "未开启同码停录，摄像头忽略当前录制单号");
        }

        if (inputOnCooldown)
        {
            return IsOrderScan(normalized, orderIdRegex)
                ? new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Queue, "扫码冷却中，保留最后一个单号")
                : new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "扫码冷却中");
        }

        if (normalized.Contains("CLEAR") || normalized.Contains("清除"))
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "清除输入指令");
        if (normalized.Contains("SHIP") || normalized.Contains("发货") || normalized.Contains("FAHUO"))
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "切换发货模式指令");
        if (normalized.Contains("BACK") || normalized.Contains("退货") || normalized.Contains("TUIHUO"))
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "切换退货模式指令");
        if (normalized.Contains("START") || normalized.Contains("开始录制"))
        {
            return new BarcodeRecordingDecision(
                isRecording ? BarcodeRecordingDecisionAction.Stop : BarcodeRecordingDecisionAction.Start,
                "开始录制切换指令");
        }
        if (normalized.Contains("STOP") || normalized.Contains("停止录制"))
        {
            return new BarcodeRecordingDecision(
                isRecording ? BarcodeRecordingDecisionAction.Stop : BarcodeRecordingDecisionAction.Ignore,
                isRecording ? "停止录制指令" : "停止指令到达时未在录制");
        }

        if (isRecording && sameBarcodeStopEnabled)
        {
            string current = (recordingOrderId ?? "").Trim().ToUpperInvariant();
            if (current.Length == 0)
                return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "当前录像未绑定单号");
            if (!string.Equals(normalized, current, StringComparison.Ordinal))
                return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "同码停录模式下单号不一致");

            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Stop, "同码停录匹配");
        }

        if (!IsOrderScan(normalized, orderIdRegex))
            return new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Ignore, "非法单号");

        return isRecording
            ? new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Switch, "停止当前录像并开始新单号")
            : new BarcodeRecordingDecision(BarcodeRecordingDecisionAction.Start, "开始新单号录像");
    }

    private static bool IsOrderScan(string value, string? orderIdRegex)
    {
        try { return Regex.IsMatch(value, orderIdRegex ?? ""); }
        catch { return true; }
    }
}

internal static class CameraBarcodeCandidatePolicy
{
    public static bool IsValid(string? value, string? orderIdRegex)
    {
        string normalized = (value ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return false;
        if (normalized.Contains("CLEAR") || normalized.Contains("清除")) return false;
        if (normalized.Contains("SHIP") || normalized.Contains("发货") || normalized.Contains("FAHUO")) return false;
        if (normalized.Contains("BACK") || normalized.Contains("退货") || normalized.Contains("TUIHUO")) return false;
        if (normalized.Contains("START") || normalized.Contains("开始录制")) return false;
        if (normalized.Contains("STOP") || normalized.Contains("停止录制")) return false;

        try { return Regex.IsMatch(normalized, orderIdRegex ?? ""); }
        catch { return false; }
    }

    public static bool IsCurrentRecordingCode(string? value, string? recordingOrderId, bool isRecording)
    {
        if (!isRecording)
            return false;

        string normalized = (value ?? "").Trim();
        string current = (recordingOrderId ?? "").Trim();
        return normalized.Length > 0
            && current.Length > 0
            && string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldIgnoreCurrentRecordingCode(
        string? value,
        string? recordingOrderId,
        bool isRecording,
        bool sameBarcodeStopEnabled)
    {
        return !sameBarcodeStopEnabled
            && IsCurrentRecordingCode(value, recordingOrderId, isRecording);
    }
}

internal sealed class CameraBarcodeStabilityTracker
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RearmDelay = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, DateTimeOffset> _lockedCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _missingLockedCodesSince = new(StringComparer.Ordinal);
    private string _candidateCode = "";
    private DateTimeOffset _candidateFirstSeen;
    private DateTimeOffset _candidateLastSeen;
    private int _candidateHits;

    public CameraBarcodeObservation Observe(string? code, DateTimeOffset now)
    {
        RearmMissingCodes(now, code);

        string normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            ExpireCandidate(now);
            return new CameraBarcodeObservation(_candidateCode);
        }

        if (_lockedCodes.ContainsKey(normalized))
        {
            _lockedCodes[normalized] = now;
            _missingLockedCodesSince.Remove(normalized);
            if (string.Equals(_candidateCode, normalized, StringComparison.Ordinal))
                ClearCandidate();
            return new CameraBarcodeObservation();
        }

        if (!string.Equals(_candidateCode, normalized, StringComparison.Ordinal)
            || now - _candidateFirstSeen > ConfirmationWindow)
        {
            _candidateCode = normalized;
            _candidateFirstSeen = now;
            _candidateLastSeen = now;
            _candidateHits = 1;
            return new CameraBarcodeObservation(_candidateCode);
        }

        _candidateLastSeen = now;
        _candidateHits++;
        if (_candidateHits < 2)
            return new CameraBarcodeObservation(_candidateCode);

        _lockedCodes[normalized] = now;
        ClearCandidate();
        return new CameraBarcodeObservation(ConfirmedCode: normalized);
    }

    public void Reset(bool preserveLockedCodes = false)
    {
        if (!preserveLockedCodes)
        {
            _lockedCodes.Clear();
            _missingLockedCodesSince.Clear();
        }
        ClearCandidate();
    }

    private void RearmMissingCodes(DateTimeOffset now, string? observedCode)
    {
        string normalized = (observedCode ?? "").Trim().ToUpperInvariant();
        foreach (string code in _lockedCodes.Keys.ToArray())
        {
            if (string.Equals(code, normalized, StringComparison.Ordinal))
            {
                _missingLockedCodesSince.Remove(code);
                continue;
            }

            if (!_missingLockedCodesSince.TryGetValue(code, out DateTimeOffset missingSince))
            {
                _missingLockedCodesSince[code] = now;
                continue;
            }

            if (now - missingSince >= RearmDelay)
            {
                _lockedCodes.Remove(code);
                _missingLockedCodesSince.Remove(code);
            }
        }
    }

    private void ExpireCandidate(DateTimeOffset now)
    {
        if (_candidateCode.Length > 0 && now - _candidateLastSeen >= ConfirmationWindow)
            ClearCandidate();
    }

    private void ClearCandidate()
    {
        _candidateCode = "";
        _candidateFirstSeen = default;
        _candidateLastSeen = default;
        _candidateHits = 0;
    }
}

internal sealed class CameraBarcodeFrameDecoder
{
    internal const double GuideWidthRatio = 0.9;
    internal const double GuideHeightRatio = 0.9;

    private static readonly HashSet<BarcodeFormat> AllowedFormats =
    [
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.ITF
    ];

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = AllowedFormats.ToList()
        }
    };

    public string? DecodeGuideRegion(Mat frame)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        Rect guide = GetGuideRect(frame.Width, frame.Height);
        if (guide.Width <= 0 || guide.Height <= 0)
            return null;

        using Mat cropped = frame.Clone(guide);
        return Decode(cropped);
    }

    public string? DecodeFullFrame(Mat frame) => Decode(frame);

    internal static Rect GetGuideRect(int width, int height)
    {
        int guideWidth = Math.Clamp((int)Math.Round(width * GuideWidthRatio), 1, Math.Max(1, width));
        int guideHeight = Math.Clamp((int)Math.Round(height * GuideHeightRatio), 1, Math.Max(1, height));
        return new Rect((width - guideWidth) / 2, (height - guideHeight) / 2, guideWidth, guideHeight);
    }

    private string? Decode(Mat frame)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        using Mat gray = new();
        switch (frame.Channels())
        {
            case 1:
                frame.CopyTo(gray);
                break;
            case 3:
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return null;
        }

        using Mat? continuous = gray.IsContinuous() ? null : gray.Clone();
        Mat source = continuous ?? gray;
        byte[] pixels = new byte[checked(source.Width * source.Height)];
        Marshal.Copy(source.Data, pixels, 0, pixels.Length);

        Result? result = _reader.Decode(pixels, source.Width, source.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        if (result == null || !AllowedFormats.Contains(result.BarcodeFormat))
            return null;

        string normalized = (result.Text ?? "").Trim().ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}

internal sealed class CameraBarcodeMotionGate : IDisposable
{
    internal static readonly TimeSpan DecodeHoldDuration = TimeSpan.FromSeconds(1);
    internal const int SampleWidth = 160;
    internal const int SampleHeight = 90;
    internal const double MeanDifferenceThreshold = 6.0;
    internal const double ChangedPixelRatioThreshold = 0.01;
    internal const double PixelDifferenceThreshold = 18;

    private static readonly OpenCvSharp.Size SampleSize = new(SampleWidth, SampleHeight);
    private readonly Mat _sampled = new();
    private readonly Mat _currentGray = new();
    private readonly Mat _previousGray = new();
    private readonly Mat _difference = new();
    private readonly Mat _changedPixels = new();
    private bool _hasBaseline;
    private DateTimeOffset _decodeUntil;
    private bool _disposed;

    public bool ShouldDecode(Mat frame, DateTimeOffset now)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        switch (frame.Channels())
        {
            case 1:
                Cv2.Resize(frame, _currentGray, SampleSize, interpolation: InterpolationFlags.Area);
                break;
            case 3:
                Cv2.Resize(frame, _sampled, SampleSize, interpolation: InterpolationFlags.Area);
                Cv2.CvtColor(_sampled, _currentGray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.Resize(frame, _sampled, SampleSize, interpolation: InterpolationFlags.Area);
                Cv2.CvtColor(_sampled, _currentGray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return false;
        }

        if (!_hasBaseline)
        {
            _currentGray.CopyTo(_previousGray);
            _hasBaseline = true;
            _decodeUntil = now + DecodeHoldDuration;
            return true;
        }

        Cv2.Absdiff(_currentGray, _previousGray, _difference);
        double meanDifference = Cv2.Mean(_difference).Val0;
        Cv2.Threshold(
            _difference,
            _changedPixels,
            PixelDifferenceThreshold,
            255,
            ThresholdTypes.Binary);
        double changedPixelRatio = (double)Cv2.CountNonZero(_changedPixels) / (SampleWidth * SampleHeight);
        _currentGray.CopyTo(_previousGray);

        bool changed = meanDifference >= MeanDifferenceThreshold
            || changedPixelRatio >= ChangedPixelRatioThreshold;
        if (changed)
            _decodeUntil = now + DecodeHoldDuration;

        return now <= _decodeUntil;
    }

    public void Reset()
    {
        if (_disposed)
            return;

        _hasBaseline = false;
        _decodeUntil = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sampled.Dispose();
        _currentGray.Dispose();
        _previousGray.Dispose();
        _difference.Dispose();
        _changedPixels.Dispose();
    }
}

internal sealed class CameraBarcodeRecognitionService : IDisposable
{
    private static readonly TimeSpan GuideInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FullFrameInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan SlowDecodeThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SlowDecodeLogInterval = TimeSpan.FromSeconds(30);

    private readonly Func<string, bool> _candidateValidator;
    private readonly Func<bool>? _fullFrameAllowed;
    private readonly CameraBarcodeFrameDecoder _decoder = new();
    private readonly CameraBarcodeMotionGate _motionGate = new();
    private readonly CameraBarcodeStabilityTracker _stabilityTracker = new();
    private readonly object _pendingLock = new();
    private readonly object _trackerLock = new();
    private readonly SemaphoreSlim _pendingSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private Mat? _pendingFrame;
    private bool _pendingAllowFullFrame;
    private int _pendingGeneration;
    private DateTimeOffset _lastAcceptedAt;
    private DateTimeOffset _lastFullFrameAttemptAt;
    private DateTimeOffset _lastSlowDecodeLogAt;
    private DateTimeOffset _lastRecognitionErrorLogAt;
    private long _droppedFrames;
    private int _generation;
    private volatile bool _disposed;

    public event Action<CameraBarcodeRecognitionStatus>? StatusChanged;
    public event Action<string>? BarcodeConfirmed;

    public CameraBarcodeRecognitionService(Func<string, bool> candidateValidator, Func<bool>? fullFrameAllowed = null)
    {
        _candidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
        _fullFrameAllowed = fullFrameAllowed;
        _workerTask = Task.Run(ProcessLoopAsync);
    }

    public bool TrySubmitFrame(Mat frame, bool allowFullFrame)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        Mat? replacement = null;
        Mat? dropped = null;
        bool shouldSignal = false;
        lock (_pendingLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_disposed || now - _lastAcceptedAt < GuideInterval)
                return false;

            _lastAcceptedAt = now;
            if (!_motionGate.ShouldDecode(frame, now))
                return false;

            replacement = frame.Clone();
            dropped = _pendingFrame;
            _pendingFrame = replacement;
            _pendingAllowFullFrame = allowFullFrame;
            _pendingGeneration = _generation;
            if (dropped != null)
                Interlocked.Increment(ref _droppedFrames);
            shouldSignal = _pendingSignal.CurrentCount == 0;
        }

        dropped?.Dispose();
        if (shouldSignal)
        {
            try { _pendingSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
        return true;
    }

    public void Reset(bool preserveConfirmedCodes = false)
    {
        if (_disposed)
            return;

        Mat? pending;
        lock (_pendingLock)
        {
            Interlocked.Increment(ref _generation);
            pending = _pendingFrame;
            _pendingFrame = null;
            _lastAcceptedAt = default;
            _motionGate.Reset();
        }
        pending?.Dispose();
        lock (_trackerLock)
            _stabilityTracker.Reset(preserveConfirmedCodes);
        StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle));
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _pendingSignal.WaitAsync(_cts.Token).ConfigureAwait(false);

                Mat? frame;
                bool allowFullFrame;
                int generation;
                lock (_pendingLock)
                {
                    frame = _pendingFrame;
                    allowFullFrame = _pendingAllowFullFrame;
                    generation = _pendingGeneration;
                    _pendingFrame = null;
                }
                if (frame == null)
                    continue;

                using (frame)
                    ProcessFrame(frame, allowFullFrame, generation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("CameraBarcode", "Recognition worker stopped unexpectedly", ex);
        }
    }

    private void ProcessFrame(Mat frame, bool allowFullFrame, int generation)
    {
        var stopwatch = Stopwatch.StartNew();
        string? code = null;
        try
        {
            code = _decoder.DecodeGuideRegion(frame);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (code == null
                && allowFullFrame
                && (_fullFrameAllowed?.Invoke() ?? true)
                && now - _lastFullFrameAttemptAt >= FullFrameInterval)
            {
                _lastFullFrameAttemptAt = now;
                code = _decoder.DecodeFullFrame(frame);
            }

            if (code != null && !IsValidCandidate(code))
                code = null;

            if (generation != Volatile.Read(ref _generation) || _disposed)
                return;

            CameraBarcodeObservation observation;
            lock (_trackerLock)
                observation = _stabilityTracker.Observe(code, now);

            if (observation.ConfirmedCode.Length > 0)
            {
                long dropped = Interlocked.Read(ref _droppedFrames);
                RuntimeLog.Info("CameraBarcode", $"Confirmed {observation.ConfirmedCode}, decode={stopwatch.ElapsedMilliseconds}ms, dropped={dropped}");
                StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Confirmed, observation.ConfirmedCode));
                BarcodeConfirmed?.Invoke(observation.ConfirmedCode);
            }
            else if (observation.CandidateCode.Length > 0)
            {
                StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Candidate, observation.CandidateCode));
            }
            else
            {
                StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle));
            }
        }
        catch (Exception ex)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _lastRecognitionErrorLogAt >= SlowDecodeLogInterval)
            {
                _lastRecognitionErrorLogAt = now;
                RuntimeLog.Warn("CameraBarcode", $"Recognition frame skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            stopwatch.Stop();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (stopwatch.Elapsed >= SlowDecodeThreshold && now - _lastSlowDecodeLogAt >= SlowDecodeLogInterval)
            {
                _lastSlowDecodeLogAt = now;
                RuntimeLog.Warn("CameraBarcode", $"Recognition is slower than the target rate: decode={stopwatch.ElapsedMilliseconds}ms, dropped={Interlocked.Read(ref _droppedFrames)}");
            }
        }
    }

    private bool IsValidCandidate(string code)
    {
        try { return _candidateValidator(code); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();

        Mat? pending;
        lock (_pendingLock)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }
        pending?.Dispose();
        try { _workerTask.Wait(1000); } catch { }
        _motionGate.Dispose();
        _pendingSignal.Dispose();
        _cts.Dispose();
    }
}
