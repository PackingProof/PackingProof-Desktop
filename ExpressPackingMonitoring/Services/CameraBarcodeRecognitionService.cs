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
    Confirmed,
    Visible
}

internal sealed record CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState State, string Code = "");

internal sealed record CameraBarcodeObservation(
    string CandidateCode = "",
    string ConfirmedCode = "",
    string VisibleCode = "",
    bool KeepDecoding = false);

internal enum BarcodeRecordingDecisionAction
{
    Ignore,
    Queue,
    Start,
    Stop,
    Switch,
    ClearInput,
    SwitchToShipping,
    SwitchToReturn,
    ToggleRecording
}

internal enum BarcodeRecordingDecisionReason
{
    Ready,
    CannotProcess,
    EmptyInput,
    CameraCurrentCodeIgnored,
    ProductBarcodeIgnored,
    CooldownOrderQueued,
    CooldownIgnored,
    ClearCommand,
    ShippingCommand,
    ReturnCommand,
    StartCommand,
    StopCommand,
    RecordingOrderMissing,
    RecordingOrderMismatch,
    SameCodeMatched,
    InvalidOrderNumber
}

internal sealed record BarcodeRecordingDecision(
    BarcodeRecordingDecisionAction Action,
    BarcodeRecordingDecisionReason Reason,
    string NormalizedValue);

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
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.CannotProcess, value);

        string normalized = (value ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.EmptyInput, normalized);

        if (fromCamera
            && CameraBarcodeCandidatePolicy.ShouldIgnoreCurrentRecordingCode(
                normalized,
                recordingOrderId,
                isRecording,
                sameBarcodeStopEnabled))
        {
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored, normalized);
        }

        if (CameraBarcodeCandidatePolicy.IsProductEan13(normalized))
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.ProductBarcodeIgnored, normalized);

        if (inputOnCooldown)
        {
            return IsOrderScan(normalized, orderIdRegex)
                ? Create(BarcodeRecordingDecisionAction.Queue, BarcodeRecordingDecisionReason.CooldownOrderQueued, normalized)
                : Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.CooldownIgnored, normalized);
        }

        if (TryGetCommandDecision(normalized, out BarcodeRecordingDecision commandDecision))
            return commandDecision;

        if (isRecording && sameBarcodeStopEnabled)
        {
            string current = (recordingOrderId ?? "").Trim().ToUpperInvariant();
            if (current.Length == 0)
                return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.RecordingOrderMissing, normalized);
            if (!string.Equals(normalized, current, StringComparison.Ordinal))
                return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.RecordingOrderMismatch, normalized);

            return Create(BarcodeRecordingDecisionAction.Stop, BarcodeRecordingDecisionReason.SameCodeMatched, normalized);
        }

        if (!IsOrderScan(normalized, orderIdRegex))
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.InvalidOrderNumber, normalized);

        return isRecording
            ? Create(BarcodeRecordingDecisionAction.Switch, BarcodeRecordingDecisionReason.Ready, normalized)
            : Create(BarcodeRecordingDecisionAction.Start, BarcodeRecordingDecisionReason.Ready, normalized);
    }

    /// <summary>
    /// 识别已知指令码（清除/切发货/切退货/开始录制/停止录制）。
    /// 摄像头候选校验与扫码枪决策共用同一份定义，避免两处规则漂移。
    /// </summary>
    internal static bool TryGetCommandDecision(
        string normalized,
        out BarcodeRecordingDecision decision)
    {
        if (normalized.Contains("CLEAR") || normalized.Contains("清除"))
        {
            decision = Create(BarcodeRecordingDecisionAction.ClearInput, BarcodeRecordingDecisionReason.ClearCommand, normalized);
            return true;
        }
        if (normalized.Contains("SHIP") || normalized.Contains("发货") || normalized.Contains("FAHUO"))
        {
            decision = Create(BarcodeRecordingDecisionAction.SwitchToShipping, BarcodeRecordingDecisionReason.ShippingCommand, normalized);
            return true;
        }
        if (normalized.Contains("BACK") || normalized.Contains("退货") || normalized.Contains("TUIHUO"))
        {
            decision = Create(BarcodeRecordingDecisionAction.SwitchToReturn, BarcodeRecordingDecisionReason.ReturnCommand, normalized);
            return true;
        }
        if (normalized.Contains("START") || normalized.Contains("开始录制"))
        {
            decision = Create(BarcodeRecordingDecisionAction.ToggleRecording, BarcodeRecordingDecisionReason.StartCommand, normalized);
            return true;
        }
        if (normalized.Contains("STOP") || normalized.Contains("停止录制"))
        {
            decision = Create(BarcodeRecordingDecisionAction.Stop, BarcodeRecordingDecisionReason.StopCommand, normalized);
            return true;
        }
        decision = default;
        return false;
    }

    internal static string GetReasonText(BarcodeRecordingDecisionReason reason) => reason switch
    {
        BarcodeRecordingDecisionReason.CannotProcess => "程序忙碌或正在关闭",
        BarcodeRecordingDecisionReason.EmptyInput => "空输入",
        BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored => "未开启同码停录，摄像头忽略当前录制单号",
        BarcodeRecordingDecisionReason.ProductBarcodeIgnored => "商品条码，已忽略",
        BarcodeRecordingDecisionReason.CooldownOrderQueued => "扫码冷却中，保留最后一个单号",
        BarcodeRecordingDecisionReason.CooldownIgnored => "扫码冷却中",
        BarcodeRecordingDecisionReason.ClearCommand => "清除输入指令",
        BarcodeRecordingDecisionReason.ShippingCommand => "切换发货模式指令",
        BarcodeRecordingDecisionReason.ReturnCommand => "切换退货模式指令",
        BarcodeRecordingDecisionReason.StartCommand => "开始录制切换指令",
        BarcodeRecordingDecisionReason.StopCommand => "停止录制指令",
        BarcodeRecordingDecisionReason.RecordingOrderMissing => "当前录像未绑定单号",
        BarcodeRecordingDecisionReason.RecordingOrderMismatch => "同码停录模式下单号不一致",
        BarcodeRecordingDecisionReason.SameCodeMatched => "同码停录匹配",
        BarcodeRecordingDecisionReason.InvalidOrderNumber => "非法单号",
        _ => "通过录制规则"
    };

    private static BarcodeRecordingDecision Create(
        BarcodeRecordingDecisionAction action,
        BarcodeRecordingDecisionReason reason,
        string? value) => new(action, reason, (value ?? "").Trim().ToUpperInvariant());

    private static bool IsOrderScan(string value, string? orderIdRegex)
    {
        if (!CameraBarcodeCandidatePolicy.IsValidPattern(orderIdRegex))
            return true;
        try { return Regex.IsMatch(value, orderIdRegex ?? ""); }
        catch { return true; }
    }
}

internal static class CameraBarcodeRuntimeOptions
{
    internal const string ShadowModeArgument = "--camera-barcode-shadow";
    internal const string ShadowModeEnvironmentVariable = "EPM_CAMERA_BARCODE_SHADOW";

    public static bool ShadowMode { get; private set; }

    public static void Initialize(IEnumerable<string>? arguments)
    {
        ShadowMode = IsShadowModeEnabled(
            arguments,
            Environment.GetEnvironmentVariable(ShadowModeEnvironmentVariable));
    }

    internal static bool IsShadowModeEnabled(IEnumerable<string>? arguments, string? environmentValue)
    {
        if (arguments?.Any(argument => string.Equals(
                argument,
                ShadowModeArgument,
                StringComparison.OrdinalIgnoreCase)) == true)
        {
            return true;
        }

        return string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentValue, "yes", StringComparison.OrdinalIgnoreCase);
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

        if (!IsValidPattern(orderIdRegex))
            return true;
        try { return Regex.IsMatch(normalized, orderIdRegex ?? ""); }
        catch { return true; }
    }

    /// 正则表达式是否可编译；空表示不限制，视为可解析。
    public static bool IsValidPattern(string? orderIdRegex)
    {
        string pattern = (orderIdRegex ?? "").Trim();
        if (pattern.Length == 0)
            return true;
        try { _ = new Regex(pattern); return true; }
        catch { return false; }
    }

    /// 工作识别专用：先按现有规则校验，再拒绝 EAN-13 商品条码。
    /// 普通校验/历史查询继续使用 <see cref="IsValid"/>，不受影响。
    public static bool IsValidForWorkScan(string? value, string? orderIdRegex)
    {
        string normalized = (value ?? "").Trim().ToUpperInvariant();
        return IsValid(normalized, orderIdRegex) && !IsProductEan13(normalized);
    }

    /// <summary>
    /// 是否为已知指令码（与扫码枪决策共用同一份定义）。
    /// 用于摄像头候选校验：放宽为“订单号 或 指令码”，让摄像头也能触发
    /// 清除/切发货/切退货/开始录制/停止录制。
    /// </summary>
    public static bool IsKnownCommandCode(string? value)
    {
        string normalized = (value ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return false;
        return BarcodeRecordingDecisionPolicy.TryGetCommandDecision(normalized, out _);
    }

    /// 判断是否为 EAN-13 商品条码：13 位数字、690-699 前缀且校验位合法。
    /// 扫码枪链路没有码制信息，用该启发式避免把商品条码当成面单号。
    public static bool IsProductEan13(string? value)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.Length != 13 || normalized[0] != '6' || normalized[1] != '9')
            return false;

        for (int index = 0; index < normalized.Length; index++)
        {
            if (!char.IsAsciiDigit(normalized[index]))
                return false;
        }

        int sum = 0;
        for (int index = 0; index < 12; index++)
            sum += (index % 2 == 0 ? 1 : 3) * (normalized[index] - '0');
        int checkDigit = (10 - sum % 10) % 10;
        return checkDigit == normalized[12] - '0';
    }

    /// 从订单号正则提取期望长度提示（如 {16} → “需 16 位”、{12,25} → “需 12–25 位”）。
    public static string GetOrderIdLengthHint(string? orderIdRegex)
    {
        string pattern = (orderIdRegex ?? "").Trim();
        if (pattern.Length == 0)
            return "";

        Match match = Regex.Match(pattern, @"\{(\d+)(?:,(\d*))?\}");
        if (!match.Success)
            return "";

        string min = match.Groups[1].Value;
        string max = match.Groups[2].Value;
        if (!match.Groups[2].Success)
            return $"需 {min} 位";
        if (max.Length == 0)
            return $"需至少 {min} 位";
        if (min == max)
            return $"需 {min} 位";
        return $"需 {min}–{max} 位";
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
    private static readonly TimeSpan DefaultRearmDelay = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, DateTimeOffset> _lockedCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _missingLockedCodesSince = new(StringComparer.Ordinal);
    private string _candidateCode = "";
    private DateTimeOffset _candidateFirstSeen;
    private TimeSpan _candidateConfirmationWindow;
    private int _candidateRequiredHits;
    private int _candidateHits;

    public CameraBarcodeObservation Observe(
        string? code,
        DateTimeOffset now,
        TimeSpan confirmationWindow = default,
        TimeSpan rearmDelay = default,
        int requiredHits = 2)
    {
        RearmMissingCodes(
            now,
            code,
            rearmDelay > TimeSpan.Zero ? rearmDelay : DefaultRearmDelay);

        string normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            // 空帧（没识别到条码）不重置计数，只按确认时间窗口过期才清空候选；
            // 候选仍存活时保持续扫，保证条码短暂离开后重新出现能继续累计。
            ExpireCandidate(now);
            return new CameraBarcodeObservation(
                _candidateCode,
                KeepDecoding: _candidateCode.Length > 0);
        }

        if (_lockedCodes.ContainsKey(normalized))
        {
            _lockedCodes[normalized] = now;
            _missingLockedCodesSince.Remove(normalized);
            if (string.Equals(_candidateCode, normalized, StringComparison.Ordinal))
                ClearCandidate();
            return new CameraBarcodeObservation(VisibleCode: normalized);
        }

        int requiredHitsValue = Math.Clamp(requiredHits, 1, 4);
        TimeSpan window = confirmationWindow > TimeSpan.Zero
            ? confirmationWindow
            : ConfirmationWindow;
        if (requiredHitsValue == 1)
        {
            _lockedCodes[normalized] = now;
            _missingLockedCodesSince.Remove(normalized);
            ClearCandidate();
            return new CameraBarcodeObservation(ConfirmedCode: normalized);
        }

        if (!string.Equals(_candidateCode, normalized, StringComparison.Ordinal)
            || _candidateRequiredHits != requiredHitsValue
            || now - _candidateFirstSeen > _candidateConfirmationWindow)
        {
            _candidateCode = normalized;
            _candidateFirstSeen = now;
            _candidateConfirmationWindow = window;
            _candidateRequiredHits = requiredHitsValue;
            _candidateHits = 1;
            return new CameraBarcodeObservation(
                _candidateCode,
                KeepDecoding: true);
        }

        _candidateHits++;
        if (_candidateHits < _candidateRequiredHits)
        {
            return new CameraBarcodeObservation(
                _candidateCode,
                KeepDecoding: true);
        }

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

    /// 摄像头条码触发开始录像后刷新锁定：同码消失时间从触发那一刻起算，
    /// 避免启动流程耗时较长时防重复触发提前失效。
    public void LockFromStartTrigger(string code, DateTimeOffset now)
    {
        string normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return;

        _lockedCodes[normalized] = now;
        _missingLockedCodesSince.Remove(normalized);
        if (string.Equals(_candidateCode, normalized, StringComparison.Ordinal))
            ClearCandidate();
    }

    private void RearmMissingCodes(
        DateTimeOffset now,
        string? observedCode,
        TimeSpan rearmDelay)
    {
        string normalized = (observedCode ?? "").Trim().ToUpperInvariant();
        foreach (string code in _lockedCodes.Keys.ToArray())
        {
            if (string.Equals(code, normalized, StringComparison.Ordinal))
            {
                if (!_missingLockedCodesSince.TryGetValue(code, out DateTimeOffset missingSince)
                    || now - missingSince < rearmDelay)
                {
                    _missingLockedCodesSince.Remove(code);
                    continue;
                }

                // 运动门控会在空画面稳定后暂停解码，因此重新出现的这一帧可能是
                // 消失期间的下一次观察。先按实际经过时间解锁，再让它成为新候选。
                _lockedCodes.Remove(code);
                _missingLockedCodesSince.Remove(code);
                continue;
            }

            if (!_missingLockedCodesSince.TryGetValue(code, out DateTimeOffset firstMissingAt))
            {
                _missingLockedCodesSince[code] = now;
                continue;
            }

            if (now - firstMissingAt >= rearmDelay)
            {
                _lockedCodes.Remove(code);
                _missingLockedCodesSince.Remove(code);
            }
        }
    }

    private void ExpireCandidate(DateTimeOffset now)
    {
        if (_candidateCode.Length > 0 && now - _candidateFirstSeen > _candidateConfirmationWindow)
            ClearCandidate();
    }

    private void ClearCandidate()
    {
        _candidateCode = "";
        _candidateFirstSeen = default;
        _candidateConfirmationWindow = TimeSpan.Zero;
        _candidateRequiredHits = 0;
        _candidateHits = 0;
    }
}

/// 摄像头触发开始录制失败后，在去抖窗口内忽略同一单号，避免失败后立刻连环重扫。
internal sealed class CameraBarcodeFailedStartSuppression
{
    private string _code = "";
    private DateTimeOffset _failedAt;

    public void RecordFailure(string? code, DateTimeOffset now)
    {
        _code = (code ?? "").Trim().ToUpperInvariant();
        _failedAt = now;
    }

    public bool IsSuppressed(string? code, DateTimeOffset now, double rearmSeconds)
    {
        if (_code.Length == 0)
            return false;

        string normalized = (code ?? "").Trim().ToUpperInvariant();
        return string.Equals(normalized, _code, StringComparison.Ordinal)
            && (now - _failedAt).TotalSeconds < rearmSeconds;
    }
}

internal sealed class CameraBarcodeFrameDecoder : IDisposable
{
    internal const double GuideWidthRatio = 0.85;
    internal const double GuideHeightRatio = 0.85;
    internal const int MaxDecodeDimension = 1440;
    internal const int MaxDecodePixels = 1_200_000;

    private static readonly HashSet<BarcodeFormat> AllowedFormats =
    [
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODE_93,
        BarcodeFormat.CODABAR
    ];

    private readonly BarcodeReaderGeneric _fastReader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = false,
            PossibleFormats = AllowedFormats.ToList()
        }
    };

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = AllowedFormats.ToList()
        }
    };
    private readonly DecodeWorkspace _guideWorkspace = new();
    private readonly DecodeWorkspace _fullFrameWorkspace = new();
    private bool _disposed;

    internal int PixelBufferAllocationCount =>
        _guideWorkspace.Buffers.AllocationCount + _fullFrameWorkspace.Buffers.AllocationCount;

    public string? DecodeGuideRegion(Mat frame)
        => DecodeGuideRegion(frame, isValid: null, CameraBarcodeGuideGeometry.Default);

    public string? DecodeGuideRegion(Mat frame, Func<string, bool>? isValid)
        => DecodeGuideRegion(frame, isValid, CameraBarcodeGuideGeometry.Default);

    public string? DecodeGuideRegion(
        Mat frame,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        Rect guide = GetGuideRect(frame.Width, frame.Height, geometry);
        if (guide.Width <= 0 || guide.Height <= 0)
            return null;

        using Mat cropped = new(frame, guide);
        return isValid == null
            ? DecodeSingle(cropped, _guideWorkspace)
            : DecodeBest(cropped, _guideWorkspace, isValid, guide);
    }

    internal static Rect GetGuideRect(int width, int height)
        => GetGuideRect(width, height, CameraBarcodeGuideGeometry.Default);

    internal static Rect GetGuideRect(int width, int height, CameraBarcodeGuideGeometry geometry)
    {
        double widthRatio = Math.Clamp(geometry.WidthRatio, 0.1, 1.0);
        double heightRatio = Math.Clamp(geometry.HeightRatio, 0.1, 1.0);
        double offsetX = Math.Clamp(geometry.OffsetX, -1.0, 1.0);
        double offsetY = Math.Clamp(geometry.OffsetY, -1.0, 1.0);

        int guideWidth = Math.Clamp((int)Math.Round(width * widthRatio), 1, Math.Max(1, width));
        int guideHeight = Math.Clamp((int)Math.Round(height * heightRatio), 1, Math.Max(1, height));
        double marginX = (width - guideWidth) / 2.0;
        double marginY = (height - guideHeight) / 2.0;
        int left = Math.Clamp(
            (int)Math.Round(marginX * (1 + offsetX)),
            0,
            Math.Max(0, width - guideWidth));
        int top = Math.Clamp(
            (int)Math.Round(marginY * (1 + offsetY)),
            0,
            Math.Max(0, height - guideHeight));
        return new Rect(left, top, guideWidth, guideHeight);
    }

    private string? DecodeSingle(Mat frame, DecodeWorkspace workspace)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        if (_disposed)
            return null;

        switch (frame.Channels())
        {
            case 1:
                frame.CopyTo(workspace.Gray);
                break;
            case 3:
                Cv2.CvtColor(frame, workspace.Gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(frame, workspace.Gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return null;
        }

        Mat source = PrepareDecodeSize(workspace.Gray, workspace.Scaled);
        if (!source.IsContinuous())
        {
            source.CopyTo(workspace.Continuous);
            source = workspace.Continuous;
        }

        int length = checked(source.Width * source.Height);
        DecodeBuffers buffers = workspace.Buffers;
        buffers.EnsureSize(length);
        Marshal.Copy(source.Data, buffers.Pixels, 0, length);

        Result? result = null;
        for (int orientation = 0; orientation < 4 && result == null; orientation++)
        {
            var luminance = new ReusableGrayLuminanceSource(
                buffers.Pixels,
                buffers.OrientationScratch,
                source.Width,
                source.Height,
                orientation);
            result = _fastReader.Decode(luminance) ?? _reader.Decode(luminance);
        }
        if (result == null || !AllowedFormats.Contains(result.BarcodeFormat))
            return null;

        string normalized = NormalizeResult(result.Text);
        return normalized.Length == 0 ? null : normalized;
    }

    private string? DecodeBest(
        Mat frame,
        DecodeWorkspace workspace,
        Func<string, bool>? isValid,
        Rect referenceRect)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        if (_disposed)
            return null;

        switch (frame.Channels())
        {
            case 1:
                frame.CopyTo(workspace.Gray);
                break;
            case 3:
                Cv2.CvtColor(frame, workspace.Gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(frame, workspace.Gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return null;
        }

        Mat source = PrepareDecodeSize(workspace.Gray, workspace.Scaled);
        if (!source.IsContinuous())
        {
            source.CopyTo(workspace.Continuous);
            source = workspace.Continuous;
        }

        int length = checked(source.Width * source.Height);
        DecodeBuffers buffers = workspace.Buffers;
        buffers.EnsureSize(length);
        Marshal.Copy(source.Data, buffers.Pixels, 0, length);

        double centerX = referenceRect.X + referenceRect.Width / 2.0;
        double centerY = referenceRect.Y + referenceRect.Height / 2.0;
        var candidates = new List<DecodedCandidate>(4);
        DecodedCandidate? fallback = null;

        // 快速通道不启用 TryHarder；只有快速通道扫不到时才走慢通道，
        // 避免每个候选都触发完整的穷举解码。
        for (int phase = 0; phase < 2; phase++)
        {
            BarcodeReaderGeneric reader = phase == 0 ? _fastReader : _reader;
            candidates.Clear();
            CollectCandidates(
                reader,
                orientation: 0,
                buffers,
                source.Width,
                source.Height,
                centerX,
                centerY,
                candidates);
            CollectCandidates(
                reader,
                orientation: 1,
                buffers,
                source.Width,
                source.Height,
                centerX,
                centerY,
                candidates);

            if (candidates.Count == 0)
                continue;

            DecodedCandidate? bestValid = SelectBest(candidates, isValid, requireValid: true);
            if (bestValid != null)
                return bestValid.Value.Code;

            if (phase == 1)
                return SelectBest(candidates, null, requireValid: false)?.Code;

            fallback = SelectBest(candidates, null, requireValid: false);
        }

        return fallback?.Code;
    }

    private void CollectCandidates(
        BarcodeReaderGeneric reader,
        int orientation,
        DecodeBuffers buffers,
        int sourceWidth,
        int sourceHeight,
        double centerX,
        double centerY,
        List<DecodedCandidate> candidates)
    {
        var luminance = new ReusableGrayLuminanceSource(
            buffers.Pixels,
            buffers.OrientationScratch,
            sourceWidth,
            sourceHeight,
            orientation);
        Result[]? results = reader.DecodeMultiple(luminance);
        if (results == null)
            return;

        foreach (Result result in results)
        {
            if (result == null || !AllowedFormats.Contains(result.BarcodeFormat))
                continue;

            string normalized = NormalizeResult(result.Text);
            if (normalized.Length == 0)
                continue;

            double distanceSquared = double.MaxValue;
            double area = 0;
            if (TryGetGeometry(
                    result,
                    orientation,
                    sourceWidth,
                    sourceHeight,
                    out double barcodeCenterX,
                    out double barcodeCenterY,
                    out area))
            {
                double dx = barcodeCenterX - centerX;
                double dy = barcodeCenterY - centerY;
                distanceSquared = dx * dx + dy * dy;
            }

            candidates.Add(new DecodedCandidate(normalized, distanceSquared, area));
        }
    }

    private static DecodedCandidate? SelectBest(
        IReadOnlyList<DecodedCandidate> candidates,
        Func<string, bool>? isValid,
        bool requireValid)
    {
        DecodedCandidate? best = null;
        foreach (DecodedCandidate candidate in candidates)
        {
            if (requireValid && (isValid == null || !isValid(candidate.Code)))
                continue;

            if (best == null
                || candidate.DistanceSquared < best.Value.DistanceSquared
                || (candidate.DistanceSquared == best.Value.DistanceSquared
                    && candidate.Area > best.Value.Area))
            {
                best = candidate;
            }
        }
        return best;
    }

    private static bool TryGetGeometry(
        Result result,
        int orientation,
        int sourceWidth,
        int sourceHeight,
        out double centerX,
        out double centerY,
        out double area)
    {
        centerX = 0;
        centerY = 0;
        area = 0;

        ResultPoint[]? points = result.ResultPoints;
        if (points == null || points.Length == 0)
            return false;

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (ResultPoint point in points)
        {
            (double x, double y) = MapToOriginal(
                point.X,
                point.Y,
                orientation,
                sourceWidth,
                sourceHeight);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        centerX = (minX + maxX) / 2.0;
        centerY = (minY + maxY) / 2.0;
        area = Math.Max(0, (maxX - minX) * (maxY - minY));
        return true;
    }

    private static (double X, double Y) MapToOriginal(
        double x,
        double y,
        int orientation,
        int sourceWidth,
        int sourceHeight) => orientation switch
        {
            1 => (sourceWidth - 1 - y, x),
            2 => (sourceWidth - 1 - x, sourceHeight - 1 - y),
            3 => (y, sourceHeight - 1 - x),
            _ => (x, y)
        };

    private static string NormalizeResult(string? text) =>
        (text ?? "").Trim().ToUpperInvariant();

    private readonly record struct DecodedCandidate(
        string Code,
        double DistanceSquared,
        double Area);

    private Mat PrepareDecodeSize(Mat gray, Mat scaled)
    {
        double dimensionScale = (double)MaxDecodeDimension / Math.Max(gray.Width, gray.Height);
        double pixelScale = Math.Sqrt((double)MaxDecodePixels / (gray.Width * (double)gray.Height));
        double scale = Math.Min(1, Math.Min(dimensionScale, pixelScale));
        if (scale >= 0.999)
            return gray;

        int width = Math.Max(1, (int)Math.Round(gray.Width * scale));
        int height = Math.Max(1, (int)Math.Round(gray.Height * scale));
        Cv2.Resize(gray, scaled, new OpenCvSharp.Size(width, height), interpolation: InterpolationFlags.Area);
        return scaled;
    }

    private sealed class DecodeWorkspace : IDisposable
    {
        public Mat Gray { get; } = new();
        public Mat Scaled { get; } = new();
        public Mat Continuous { get; } = new();
        public DecodeBuffers Buffers { get; } = new();

        public void Dispose()
        {
            Gray.Dispose();
            Scaled.Dispose();
            Continuous.Dispose();
        }
    }

    private sealed class DecodeBuffers
    {
        public byte[] Pixels { get; private set; } = [];
        public byte[] OrientationScratch { get; private set; } = [];
        public int AllocationCount { get; private set; }

        public void EnsureSize(int length)
        {
            if (Pixels.Length == length && OrientationScratch.Length == length)
                return;

            Pixels = GC.AllocateUninitializedArray<byte>(length);
            OrientationScratch = GC.AllocateUninitializedArray<byte>(length);
            AllocationCount++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _guideWorkspace.Dispose();
        _fullFrameWorkspace.Dispose();
    }
}

public readonly record struct CameraBarcodeGuideGeometry(
    double WidthRatio,
    double HeightRatio,
    double OffsetX,
    double OffsetY)
{
    public static CameraBarcodeGuideGeometry Default { get; } =
        new(
            CameraBarcodeFrameDecoder.GuideWidthRatio,
            CameraBarcodeFrameDecoder.GuideHeightRatio,
            0,
            0);
}

internal sealed class CameraPairingQrFrameDecoder : IDisposable
{
    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = [BarcodeFormat.QR_CODE]
        }
    };
    private readonly Mat _gray = new();
    private readonly Mat _scaled = new();
    private readonly Mat _continuous = new();
    private byte[] _pixels = [];
    private byte[] _orientationScratch = [];
    private bool _disposed;

    public string? Decode(Mat frame)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return null;

        switch (frame.Channels())
        {
            case 1:
                frame.CopyTo(_gray);
                break;
            case 3:
                Cv2.CvtColor(frame, _gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(frame, _gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return null;
        }

        double dimensionScale = (double)CameraBarcodeFrameDecoder.MaxDecodeDimension /
            Math.Max(_gray.Width, _gray.Height);
        double pixelScale = Math.Sqrt(
            (double)CameraBarcodeFrameDecoder.MaxDecodePixels /
            (_gray.Width * (double)_gray.Height));
        double scale = Math.Min(1, Math.Min(dimensionScale, pixelScale));
        Mat source = _gray;
        if (scale < 0.999)
        {
            int width = Math.Max(1, (int)Math.Round(_gray.Width * scale));
            int height = Math.Max(1, (int)Math.Round(_gray.Height * scale));
            Cv2.Resize(_gray, _scaled, new OpenCvSharp.Size(width, height), interpolation: InterpolationFlags.Area);
            source = _scaled;
        }
        if (!source.IsContinuous())
        {
            source.CopyTo(_continuous);
            source = _continuous;
        }

        int length = checked(source.Width * source.Height);
        if (_pixels.Length != length)
        {
            _pixels = GC.AllocateUninitializedArray<byte>(length);
            _orientationScratch = GC.AllocateUninitializedArray<byte>(length);
        }
        Marshal.Copy(source.Data, _pixels, 0, length);

        for (int orientation = 0; orientation < 4; orientation++)
        {
            var luminance = new ReusableGrayLuminanceSource(
                _pixels,
                _orientationScratch,
                source.Width,
                source.Height,
                orientation);
            Result? result = _reader.Decode(luminance);
            if (result?.BarcodeFormat == BarcodeFormat.QR_CODE)
            {
                string value = (result.Text ?? "").Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gray.Dispose();
        _scaled.Dispose();
        _continuous.Dispose();
    }
}

internal sealed class ReusableGrayLuminanceSource : LuminanceSource
{
    private readonly byte[] _pixels;
    private readonly byte[] _orientationScratch;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _orientation;
    private readonly int _cropLeft;
    private readonly int _cropTop;

    public ReusableGrayLuminanceSource(
        byte[] pixels,
        byte[] orientationScratch,
        int sourceWidth,
        int sourceHeight,
        int orientation)
        : this(
            pixels,
            orientationScratch,
            sourceWidth,
            sourceHeight,
            orientation,
            cropLeft: 0,
            cropTop: 0,
            width: orientation % 2 == 0 ? sourceWidth : sourceHeight,
            height: orientation % 2 == 0 ? sourceHeight : sourceWidth)
    {
    }

    private ReusableGrayLuminanceSource(
        byte[] pixels,
        byte[] orientationScratch,
        int sourceWidth,
        int sourceHeight,
        int orientation,
        int cropLeft,
        int cropTop,
        int width,
        int height)
        : base(
            width,
            height)
    {
        _pixels = pixels;
        _orientationScratch = orientationScratch;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _orientation = orientation;
        _cropLeft = cropLeft;
        _cropTop = cropTop;
    }

    public override byte[] getRow(int y, byte[] row)
    {
        if ((uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        if (row == null || row.Length < Width)
            row = new byte[Width];

        for (int x = 0; x < Width; x++)
            row[x] = GetPixel(x + _cropLeft, y + _cropTop);
        return row;
    }

    public override byte[] Matrix
    {
        get
        {
            if (_orientation == 0 && _cropLeft == 0 && _cropTop == 0)
                return _pixels;

            int index = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    _orientationScratch[index++] = GetPixel(x + _cropLeft, y + _cropTop);
            }
            return _orientationScratch;
        }
    }

    public override LuminanceSource crop(int left, int top, int width, int height)
    {
        if (left < 0 || top < 0 || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(left));
        if (left + width > Width || top + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width));

        return new ReusableGrayLuminanceSource(
            _pixels,
            _orientationScratch,
            _sourceWidth,
            _sourceHeight,
            _orientation,
            _cropLeft + left,
            _cropTop + top,
            width,
            height);
    }

    private byte GetPixel(int x, int y)
    {
        (int sourceX, int sourceY) = _orientation switch
        {
            1 => (_sourceWidth - 1 - y, x),
            2 => (_sourceWidth - 1 - x, _sourceHeight - 1 - y),
            3 => (y, _sourceHeight - 1 - x),
            _ => (x, y)
        };
        return _pixels[sourceY * _sourceWidth + sourceX];
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

    public bool ShouldDecode(Mat frame, DateTimeOffset now, bool forceDecode = false)
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

        return forceDecode || now <= _decodeUntil;
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
    private static readonly TimeSpan SlowDecodeThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SlowDecodeLogInterval = TimeSpan.FromSeconds(30);

    private readonly Func<string, bool> _candidateValidator;
    private readonly Func<string, TimeSpan>? _confirmationWindowProvider;
    private readonly Func<int>? _confirmationHitsProvider;
    private readonly Func<TimeSpan>? _rearmDelayProvider;
    private readonly Func<TimeSpan> _guideIntervalProvider;
    private readonly Func<CameraBarcodeGuideGeometry> _guideGeometryProvider;
    private readonly bool _reportVisibleCodes;
    private readonly CameraBarcodeFrameDecoder _decoder = new();
    private readonly CameraBarcodeMotionGate _motionGate = new();
    private readonly CameraBarcodeStabilityTracker _stabilityTracker = new();
    private readonly object _pendingLock = new();
    private readonly object _trackerLock = new();
    private readonly SemaphoreSlim _pendingSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private Mat? _pendingFrame;
    private int _pendingGeneration;
    private DateTimeOffset _lastAcceptedAt;
    private DateTimeOffset _lastSlowDecodeLogAt;
    private DateTimeOffset _lastRecognitionErrorLogAt;
    private string _lastInvalidCandidate = "";
    private DateTimeOffset _lastInvalidCandidateAt;
    private readonly TimeSpan _invalidCandidateThrottle;
    private readonly Func<DateTimeOffset> _utcNow;
    private long _droppedFrames;
    private long _forceDecodeUntilUtcTicks;
    private string _lastRecognizedCode = "";
    private string _lastConfirmedCommandCode = "";
    private int _generation;
    private volatile bool _disposed;
    private int _workerResourcesDisposed;

    public event Action<CameraBarcodeRecognitionStatus>? StatusChanged;
    public event Action<string>? BarcodeConfirmed;
    public event Action<string>? BarcodeRecognized;
    public event Action<string>? InvalidCandidate;

    public CameraBarcodeRecognitionService(
        Func<string, bool> candidateValidator,
        Func<string, TimeSpan>? confirmationWindowProvider = null,
        Func<TimeSpan>? rearmDelayProvider = null,
        bool reportVisibleCodes = false,
        TimeSpan? invalidCandidateThrottle = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<TimeSpan>? guideIntervalProvider = null,
        Func<CameraBarcodeGuideGeometry>? guideGeometryProvider = null,
        Func<int>? confirmationHitsProvider = null)
    {
        _candidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
        _confirmationWindowProvider = confirmationWindowProvider;
        _confirmationHitsProvider = confirmationHitsProvider;
        _rearmDelayProvider = rearmDelayProvider;
        _guideIntervalProvider = guideIntervalProvider ?? (() => GuideInterval);
        _guideGeometryProvider = guideGeometryProvider ?? (() => CameraBarcodeGuideGeometry.Default);
        _reportVisibleCodes = reportVisibleCodes;
        _invalidCandidateThrottle = invalidCandidateThrottle ?? TimeSpan.FromSeconds(3);
        _utcNow = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _workerTask = Task.Run(ProcessLoopAsync);
    }

    public bool TrySubmitFrame(Mat frame)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        Mat? replacement = null;
        Mat? dropped = null;
        bool shouldSignal = false;
        lock (_pendingLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan guideInterval = _guideIntervalProvider();
            if (guideInterval <= TimeSpan.Zero)
                guideInterval = GuideInterval;
            if (_disposed || now - _lastAcceptedAt < guideInterval)
                return false;

            _lastAcceptedAt = now;
            bool forceDecode = now.UtcTicks <= Volatile.Read(ref _forceDecodeUntilUtcTicks);
            if (!_motionGate.ShouldDecode(frame, now, forceDecode))
                return false;

            replacement = frame.Clone();
            dropped = _pendingFrame;
            _pendingFrame = replacement;
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
            Volatile.Write(ref _forceDecodeUntilUtcTicks, 0);
            _motionGate.Reset();
        }
        pending?.Dispose();
        lock (_trackerLock)
            _stabilityTracker.Reset(preserveConfirmedCodes);
        StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle));
    }

    /// 摄像头条码触发开始录像后调用：让同码消失时间从该时刻起算。
    public void MarkStartTriggered(string code)
    {
        if (_disposed || string.IsNullOrEmpty(code))
            return;

        lock (_trackerLock)
            _stabilityTracker.LockFromStartTrigger(code, _utcNow());
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _pendingSignal.WaitAsync(_cts.Token).ConfigureAwait(false);

                Mat? frame;
                int generation;
                lock (_pendingLock)
                {
                    frame = _pendingFrame;
                    generation = _pendingGeneration;
                    _pendingFrame = null;
                }
                if (frame == null)
                    continue;

                using (frame)
                    ProcessFrame(frame, generation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("CameraBarcode", "Recognition worker stopped unexpectedly", ex);
        }
    }

    private void ProcessFrame(Mat frame, int generation)
    {
        var stopwatch = Stopwatch.StartNew();
        string? code = null;
        try
        {
            string? decoded = _decoder.DecodeGuideRegion(
                frame,
                IsValidCandidate,
                _guideGeometryProvider());
            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (decoded != null && !IsValidCandidate(decoded))
            {
                NotifyInvalidCandidate(decoded);
            }

            if (generation != Volatile.Read(ref _generation) || _disposed)
                return;

            // 解码到任意非空条码即触发独立滴声（含 CLEAR/SHIP 等指令码）；
            // 确认/状态/指令处理仍只使用通过校验的码。
            NotifyRecognizedIfNew(decoded);
            if (decoded != null &&
                CameraBarcodeCandidatePolicy.IsKnownCommandCode(decoded))
            {
                // 指令码一次识别即触发，不依赖稳定性确认；同码保持可见不重复。
                if (!string.Equals(
                    decoded,
                    _lastConfirmedCommandCode,
                    StringComparison.Ordinal))
                {
                    _lastConfirmedCommandCode = decoded;
                    RuntimeLog.Info(
                        "CameraBarcode",
                        $"Confirmed command {decoded} immediately");
                    BarcodeConfirmed?.Invoke(decoded);
                }
                code = null;
            }
            else
            {
                if (decoded == null)
                {
                    _lastConfirmedCommandCode = "";
                }
                code = decoded != null && IsValidCandidate(decoded)
                    ? decoded
                    : null;
            }

            CameraBarcodeObservation observation;
            TimeSpan confirmationWindow = code == null
                ? TimeSpan.Zero
                : _confirmationWindowProvider?.Invoke(code) ?? TimeSpan.Zero;
            int confirmationHits = _confirmationHitsProvider?.Invoke() ?? 2;
            if (confirmationHits < 1)
                confirmationHits = 1;
            if (confirmationHits > 4)
                confirmationHits = 4;
            TimeSpan rearmDelay = _rearmDelayProvider?.Invoke() ?? TimeSpan.Zero;
            lock (_trackerLock)
                observation = _stabilityTracker.Observe(
                    code,
                    now,
                    confirmationWindow,
                    rearmDelay,
                    confirmationHits);

            Volatile.Write(
                ref _forceDecodeUntilUtcTicks,
                observation.KeepDecoding ? now.AddSeconds(2.5).UtcTicks : 0);

            CameraBarcodeRecognitionStatus status =
                CreateStatus(observation, _reportVisibleCodes);
            if (observation.ConfirmedCode.Length > 0)
            {
                long dropped = Interlocked.Read(ref _droppedFrames);
                RuntimeLog.Info("CameraBarcode", $"Confirmed {observation.ConfirmedCode}, decode={stopwatch.ElapsedMilliseconds}ms, dropped={dropped}");
                StatusChanged?.Invoke(status);
                BarcodeConfirmed?.Invoke(observation.ConfirmedCode);
            }
            else
            {
                StatusChanged?.Invoke(status);
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

    /// 解码到合法条码即触发独立反馈事件：同码连续可见只触发一次，
    /// 码离开画面后清空记录，再次出现会重新触发。
    private void NotifyRecognizedIfNew(string? code)
    {
        string normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            _lastRecognizedCode = "";
            return;
        }

        if (string.Equals(normalized, _lastRecognizedCode, StringComparison.Ordinal))
            return;

        _lastRecognizedCode = normalized;
        BarcodeRecognized?.Invoke(normalized);
    }

    private void NotifyInvalidCandidate(string code)
    {
        if (_disposed || string.IsNullOrEmpty(code))
            return;

        DateTimeOffset now = _utcNow();
        if (string.Equals(code, _lastInvalidCandidate, StringComparison.Ordinal) &&
            now - _lastInvalidCandidateAt < _invalidCandidateThrottle)
        {
            return;
        }

        _lastInvalidCandidate = code;
        _lastInvalidCandidateAt = now;
        InvalidCandidate?.Invoke(code);
    }

    internal static CameraBarcodeRecognitionStatus CreateStatus(
        CameraBarcodeObservation observation,
        bool reportVisibleCodes)
    {
        if (observation.ConfirmedCode.Length > 0)
        {
            return new CameraBarcodeRecognitionStatus(
                CameraBarcodeRecognitionState.Confirmed,
                observation.ConfirmedCode);
        }

        if (reportVisibleCodes && observation.VisibleCode.Length > 0)
        {
            return new CameraBarcodeRecognitionStatus(
                CameraBarcodeRecognitionState.Visible,
                observation.VisibleCode);
        }

        if (observation.CandidateCode.Length > 0)
        {
            return new CameraBarcodeRecognitionStatus(
                CameraBarcodeRecognitionState.Candidate,
                observation.CandidateCode);
        }

        return new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle);
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
        bool completed = false;
        try { completed = _workerTask.Wait(1000); } catch { completed = _workerTask.IsCompleted; }
        if (completed)
        {
            DisposeWorkerResources();
        }
        else
        {
            RuntimeLog.Warn("CameraBarcode", "Recognition worker is still stopping; native decoder cleanup deferred");
            _ = _workerTask.ContinueWith(
                _ => DisposeWorkerResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void DisposeWorkerResources()
    {
        if (Interlocked.Exchange(ref _workerResourcesDisposed, 1) != 0)
            return;
        _decoder.Dispose();
        _motionGate.Dispose();
        _pendingSignal.Dispose();
        _cts.Dispose();
    }
}
