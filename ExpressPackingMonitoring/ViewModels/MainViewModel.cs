#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExpressPackingMonitoring.ViewModels
{

    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private AppConfig _config;
        private readonly string _configFilePath = AppPaths.ConfigPath;
        private readonly string _dbFilePath = AppPaths.VideoDatabasePath;
        private VideoDatabase _db;
        private ArchiveService _archiveService;

        /// <summary>启动时缓存的可用 GPU 编码器列表</summary>
        public static List<GpuEncoderOption> CachedEncoderOptions { get; private set; }

        /// <summary>启动时通过试编码验证的所有编码器名称（包括 H.264 和 H.265）</summary>
        public static HashSet<string> ValidatedEncoders { get; private set; } = new();
        public static Dictionary<string, double> EncoderPerformanceScores { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        private VideoCaptureDevice _videoSource;
        private NetworkCameraSource _networkCameraSource;
        private DateTime _networkCameraStartedAt = DateTime.MinValue;
        private Task _cameraForceStopTask;
        private Mat _latestFrame;
        private long _latestFrameSequence;
        private readonly object _frameLock = new object();
        private readonly object _eventBufferLock = new();
        private readonly object _recordingFrameOrderLock = new();
        private readonly LinkedList<PreRecordFrame> _preRecordFrames = new();
        private long _preRecordBytes;
        private long _preRecordDroppedFrames;
        private bool _preRecordBufferHasWrapped;
        private long _preRecordSequence;
        private int _preRecordWidth;
        private int _preRecordHeight;
        private int _preRecordDisplayCapacityFrames;
        private long _lastPreRecordUiPublishTicks;
        private int _preRecordUiPublishQueued;
        private long _preRecordUiPublishVersion;
        private List<Mat> _pendingPreRecordFrames;
        private List<DateTime> _pendingPreRecordTimestamps;
        private DateTime? _pendingPreRecordStartTime;
        private const long PreRecordBufferHardMaxBytes = 8L * 1024 * 1024 * 1024;

        private BlockingCollection<Mat> _videoWriteQueue;
        private Task _writeTask;
        private Task _lastFinalizeTask;
        private Task _mkvRecoveryTask;
        private Task _postStopMuxTask;
        private readonly ConcurrentDictionary<string, ExpectedRecordingSpecification> _pendingRecordingSpecificationChecks =
            new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _writeCts;
        private int _actualCameraWidth;
        private int _actualCameraHeight;
        private int _actualCameraFps = 15; // 摄像头硬件实际帧率
        private double _cameraSourceFpsEstimate;
        private long _cameraSourceLastTimestamp;
        private int _cameraSourceSampleCount;
        private readonly object _audioLock = new object();
        private NAudio.CoreAudioApi.WasapiCapture _audioCapture;
        private NAudio.Wave.WaveFileWriter _audioWriter;
        private NAudio.Wave.WaveFormat _audioTargetFormat;
        private System.IO.Pipes.NamedPipeServerStream _audioPipeServer;
        private Task _audioPipeConnectionTask;
        private string _currentAudioPipeName;
        private bool _currentAudioUsesDirectAac;
        private int _audioInitialOffsetBytesRemaining;
        private BlockingCollection<byte[]> _audioWriteQueue;
        private Task _audioFileWriteTask;
        private bool _audioWriteFailed;
        private bool _audioWriteQueueFullLogged;
        private bool _audioWriteQueueFullReported;
        private bool _audioFailedForCurrentRecording;
        private string _currentAudioFilePath;
        private string _currentAudioLogPath;
        private CancellationTokenSource _audioMonitorCts;
        private Task _audioMonitorTask;
        private volatile bool _audioStopRequested;
        private bool _audioRestarting;
        private DateTime _lastAudioDataAt = DateTime.MinValue;
        private DateTime _lastAudioPacketAt = DateTime.MinValue;
        private DateTime _audioSuppressUntil = DateTime.MinValue;
        private long _audioBytesWritten;
        private short _audioPeakSinceLastCheck;
        private long _audioBytesSinceLastCheck;
        private int _silentAudioCheckCount;
        private int _audioMonitorLogTick;
        private int _audioConvertFailureCount;
        private int _audioSelectedSourceChannel = -1;
        private double _audioResamplePosition;
        private short _audioPreviousSourceSample;
        private bool _audioHasPreviousSourceSample;
        private bool _audioCaptureUnstable;
        private int _audioGapCount;
        private double _audioMaxGapMs;
        private long _audioGapPaddingBytes;

        private Mat _previousCheckFrame = new Mat();
        private readonly Mat _motionCurrentSmall = new Mat();
        private readonly Mat _motionPreviousSmall = new Mat();
        private readonly Mat _motionCurrentGray = new Mat();
        private readonly Mat _motionPreviousGray = new Mat();
        private readonly Mat _motionDiff = new Mat();
        private readonly Mat _motionThreshold = new Mat();
        private BitmapSource _videoFrame;
        private WriteableBitmap _previewWriteableBitmap;
        private static readonly TimeSpan PreviewFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 12.0);
        private static readonly TimeSpan PreviewFreezeWarnThreshold = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PreviewFreezeRestartThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PreviewFreezeRestartCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ResourceHealthLogInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan UiHeartbeatStaleThreshold = TimeSpan.FromSeconds(2);
        private DateTime _lastPreviewFrameAt = DateTime.MinValue;
        private DateTime _lastPreviewPublishedAt = DateTime.MinValue;
        private DateTime _lastPreviewFreezeLogAt = DateTime.MinValue;
        private DateTime _lastPreviewWatchdogRestartAt = DateTime.MinValue;
        private DateTime _lastRecordingQueueWarnAt = DateTime.MinValue;
        private DateTime _lastResourceHealthLogAt = DateTime.MinValue;
        private DateTime _lastPreviewConvertErrorLogAt = DateTime.MinValue;
        private DateTime _lastVideoFrameErrorLogAt = DateTime.MinValue;
        private DateTime _lastCameraStateErrorLogAt = DateTime.MinValue;
        private DateTime _lastUiHeartbeatAt = DateTime.Now;
        private long _archiveUiHeartbeatUtcTicks = DateTime.UtcNow.Ticks;
        private long _archiveFrameUtcTicks;
        private long _archivePreviewUtcTicks;
        private int _archiveCameraActive;
        private System.Windows.Threading.DispatcherTimer _uiHeartbeatTimer;
        private readonly PreviewSessionGate _previewSessionGate = new();
        private readonly CameraFrameReadySignal _cameraFrameReady = new();
        private readonly CameraFrameRateGate _cameraFrameRateGate = new();
        private CancellationTokenSource _cts;

        // 摄像头空闲休眠
        private bool _isCameraSleeping = false;
        private DateTime _lastActivityTime = DateTime.Now;
        public bool IsCameraSleeping { get => _isCameraSleeping; private set => SetProperty(ref _isCameraSleeping, value); }
        private Task _videoTask;
        private object _videoLock = new object();

        // 摄像头重连控制
        private volatile bool _isRestartingCamera = false;
        private volatile bool _isSetupWizardActive = false;
        private volatile bool _cameraEverConnected = false; // 摄像头是否曾经成功连接过（区分启动vs断连）
        private DateTime _lastRestartAttempt = DateTime.MinValue;
        private int _consecutiveRestartFailures = 0;
        private const int MaxConsecutiveRestartFailures = 5;
        private const double MinRestartIntervalSeconds = 3.0;
        // RTSP 网络源首帧可能因等待关键帧延迟数秒，宽限期内不判信号丢失。
        private const double NetworkCameraConnectGraceSeconds = 20.0;

        private readonly SemaphoreSlim _recorderLock = new SemaphoreSlim(1, 1);
        private sealed class PrintedRefundScanCheck
        {
            public Guid AlertId { get; } = Guid.NewGuid();
            public string TrackingNumber { get; init; } = "";
            public string Mode { get; init; } = "";
            private int _alerted;

            public bool TryMarkAlerted() => Interlocked.Exchange(ref _alerted, 1) == 0;
        }

        private static readonly TimeSpan PrintedRefundLookupInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PrintedRefundLookupTimeout = TimeSpan.FromSeconds(15);
        private readonly object _printedRefundLookupLock = new();
        private readonly List<PrintedRefundScanCheck> _pendingPrintedRefundChecks = new();
        private Task _printedRefundLookupTask;
        private DateTime _lastPrintedRefundLookupUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _mkvConvertLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _mkvBatchLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _shutdownLock = new SemaphoreSlim(1, 1);
        private volatile bool _shutdownRequested;
        private bool _isShutdownInProgress;
        private volatile bool _shutdownPrepared;
        private bool _isInputOnCooldown = false;
        private string _pendingScanDuringCooldown = "";
        private CancellationTokenSource _sameCodePostRollCts;

        private sealed class PreRecordFrame
        {
            public required Mat Frame { get; init; }
            public required DateTime Timestamp { get; init; }
            public required long Bytes { get; init; }
            public required long Sequence { get; init; }
        }
        private readonly CameraBarcodeFailedStartSuppression _cameraStartFailedSuppression = new();
        private Process _currentFfmpegProcess;
        private TaskCompletionSource<long> _firstRecordingFrameWritten;
        private long _recordingStartTimestamp;
        private bool _isDisposed = false; // 新增：防止销毁后操作 UI
        private WebServer _webServer;
        private ExtensionAuthorizationStore _extensionAuthorizationStore;
        private ExtensionRuntime _extensionRuntime;
        private Task<bool> _webServerStartupTask;
        private readonly SemaphoreSlim _webServerLifecycleLock = new(1, 1);
        private StatisticsWindow _statisticsWindow;
        private PlaybackWindow _playbackWindow;
        private GlobalKeyboardHook _globalKeyHook;
        private CameraBarcodeRecognitionService _cameraBarcodeRecognition;
        private CancellationTokenSource _cameraBarcodeFeedbackCts;
        private readonly CameraPairingQrFrameDecoder _cameraPairingQrDecoder = new();
        private readonly object _cameraPairingQrLock = new();
        private TaskCompletionSource<string> _cameraPairingQrScan;
        private int _cameraPairingQrDecodeBusy;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value) && value)
                    ResetCameraBarcodeRecognition(preserveConfirmedCodes: true);
            }
        }

        private string _busyText = "";
        public string BusyText { get => _busyText; set => SetProperty(ref _busyText, value); }
        // ====================================

        private SpeechService _speechService;
        private AlertService _alertService;

        private string _currentMode = "发货";
        private string _currentOrderId = "";
        private bool _isRecording;
        private string _scanInputText = "";
        public string ScanInputText { get => _scanInputText; set { if (SetProperty(ref _scanInputText, value)) ScheduleRefreshBarcodes(); } }

        private string _cameraBarcodeStatusText = "将面单条形码放入框内";
        private bool _isCameraBarcodeCandidate;
        private bool _isCameraBarcodeConfirmed;
        public string CameraBarcodeStatusText { get => _cameraBarcodeStatusText; private set => SetProperty(ref _cameraBarcodeStatusText, value); }
        public bool IsCameraBarcodeCandidate { get => _isCameraBarcodeCandidate; private set => SetProperty(ref _isCameraBarcodeCandidate, value); }
        public bool IsCameraBarcodeConfirmed { get => _isCameraBarcodeConfirmed; private set => SetProperty(ref _isCameraBarcodeConfirmed, value); }
        public bool IsCameraBarcodeRecognitionEnabled => Config?.EnableCameraBarcodeRecognition == true;

        private double _diskUsagePercent;
        private string _diskUsageText = "0.0 / 0.0 GB";
        private bool _isScanning = false;
        private DateTime _lastScanTime;
        private DateTime _lastMotionTime;
        private DateTime _recordStartTime;
        private double _activePreRecordSeconds;
        private DateTime _recordingGracePeriodStartTime;
        private enum ZoomPhase { None, ZoomingIn, Holding, ZoomingOut }
        private ZoomPhase _zoomPhase = ZoomPhase.None;
        private DateTime _zoomPhaseStartTime;
        private bool _delayBeforeZooming = false;

        private ScanRecord _currentScanRecord;
        private long _currentRecordId; 
        private string _currentVideoFilePath;  // 当前录制文件路径
        private string _currentArchivePath = ""; // 当前录像对应的网络归档目标根（为空表示无需归档）
        private string _currentVideoCodec;
        private string _currentVideoEncoder;
        private string _stopReason = "手动";     // 停止录制的原因
        private string _recordingOrderId;       // 录制开始时的单号
        private string _recordingSessionId;     // 当前录像会话 ID，供第三方扩展数据绑定
        private volatile WatermarkSnapshot _recordingWatermarkSnapshot = WatermarkSnapshot.Empty;
        private string _recordingMode;          // 录制开始时的模式
        private bool _autoStopWarned = false;
        private bool _maxDurationWarned = false;

        private sealed class WatermarkSnapshot
        {
            public static WatermarkSnapshot Empty { get; } = new WatermarkSnapshot("", Array.Empty<string>());
            public string RecordingSessionId { get; }
            public IReadOnlyList<string> Lines { get; }

            public WatermarkSnapshot(string recordingSessionId, IReadOnlyList<string> lines)
            {
                RecordingSessionId = recordingSessionId ?? "";
                Lines = lines ?? Array.Empty<string>();
            }
        }
        private bool _pendingCameraRestart = false; // 录制中修改了摄像头配置，录制结束后重启
        private volatile bool _isEncoderDetectRunning = true; // 是否正在进行 GPU 编码器检测
        private string _workstationPrintStatusText = "未连接";
        private string _workstationStatusToolTip = "";
        private string _orderIntegrationStatusText = "暂未收到订单";
        private string _userscriptSetupStatusText = "未配置订单联动";
        private string _userscriptSetupShortStatusText = "未配置";
        private string _userscriptButtonText = "安装订单联动";
        private IReadOnlyList<ConnectedClientInfo> _connectedClientSnapshot = [];
        private DateTime _mobileBackupStatusDate = DateTime.Today;
        private DateTime _lastUserscriptStatusRefreshAt = DateTime.MinValue;
        private string _connectedDeviceText = "连接服务未开启";
        private string _connectedDeviceToolTip = "开启局域网查看后可显示在线设备";
        private bool _hasConnectedDevices;
        private string _monitorAccessAddress = "";
        private int _workstationAddressRefreshVersion;
        private bool _purposeSwitchPending;
        private string _switchWorkstationButtonText = "切换用途";
        private readonly CancellationTokenSource _purposeSwitchCts = new();

        private int _totalPieces;
        private TimeSpan _totalPackTime;
        public int TotalPieces { get => _totalPieces; set { SetProperty(ref _totalPieces, value); OnPropertyChanged(nameof(AveragePackTimeDisplay)); } }
        public DurationDisplayText TotalPackTimeDisplay => FormatDurationDisplay(_totalPackTime);
        public DurationDisplayText AveragePackTimeDisplay => TotalPieces == 0 ? DurationDisplayText.Zero : FormatDurationDisplay(TimeSpan.FromSeconds(_totalPackTime.TotalSeconds / TotalPieces));

        public sealed class DurationDisplayText
        {
            public static DurationDisplayText Zero { get; } = new("", "", "", "", "0", "秒");

            public DurationDisplayText(string hourValue, string hourUnit, string minuteValue, string minuteUnit, string secondValue, string secondUnit)
            {
                HourValue = hourValue;
                HourUnit = hourUnit;
                MinuteValue = minuteValue;
                MinuteUnit = minuteUnit;
                SecondValue = secondValue;
                SecondUnit = secondUnit;
            }

            public string HourValue { get; }
            public string HourUnit { get; }
            public string MinuteValue { get; }
            public string MinuteUnit { get; }
            public string SecondValue { get; }
            public string SecondUnit { get; }
        }

        public sealed class MobileBackupDeviceStatus
        {
            public string DeviceId { get; init; } = "";
            public string DisplayText { get; init; } = "";
            public bool IsOnline { get; init; }
        }

        private static DurationDisplayText FormatDurationDisplay(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            int totalSeconds = (int)Math.Round(duration.TotalSeconds);
            int hours = totalSeconds / 3600;
            int minutes = totalSeconds % 3600 / 60;
            int seconds = totalSeconds % 60;

            return new DurationDisplayText(
                hours > 0 ? hours.ToString() : "",
                hours > 0 ? "时" : "",
                minutes > 0 || hours > 0 ? minutes.ToString() : "",
                minutes > 0 || hours > 0 ? "分" : "",
                seconds.ToString(),
                "秒");
        }

        internal static string FormatWatermarkTimestamp(DateTimeOffset timestamp)
        {
            TimeSpan offset = timestamp.Offset;
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            offset = offset.Duration();
            string offsetText = offset.Minutes == 0
                ? $"{sign}{offset.Hours:00}"
                : $"{sign}{offset.Hours:00}:{offset.Minutes:00}";
            return $"UTC{offsetText}: {timestamp:yyyy/MM/dd HH:mm:ss}";
        }

        internal static void ApplyWatermarkToFrame(Mat frame, DateTimeOffset timestamp, string orderId)
            => ApplyWatermarkToFrame(frame, timestamp, orderId, Array.Empty<string>());

        internal static void ApplyWatermarkToFrame(Mat frame, DateTimeOffset timestamp, string orderId, IReadOnlyList<string> extensionLines)
        {
            if (frame == null || frame.IsDisposed || frame.Empty()) return;

            string line1 = FormatWatermarkTimestamp(timestamp);
            double fontScale = Math.Max(0.5, frame.Height / 720.0) * 0.6;
            int thickness = fontScale >= 0.8 ? 2 : 1;
            int lineHeight = (int)(30 * fontScale / 0.6);
            var size1 = Cv2.GetTextSize(line1, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
            int x1 = Math.Max(8, frame.Width - size1.Width - 15);
            int y1 = lineHeight;

            Cv2.PutText(frame, line1, new OpenCvSharp.Point(x1, y1),
                HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
            Cv2.PutText(frame, line1, new OpenCvSharp.Point(x1, y1),
                HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);

            int nextLine = 1;
            if (!string.IsNullOrWhiteSpace(orderId))
            {
                string line2 = $"Order:{orderId}";
                DrawWatermarkLine(frame, line2, fontScale, thickness, lineHeight, ref nextLine);
            }

            if (extensionLines == null) return;
            foreach (string extensionLine in extensionLines.Take(4))
            {
                if (!string.IsNullOrWhiteSpace(extensionLine))
                    DrawWatermarkLine(frame, extensionLine, fontScale, thickness, lineHeight, ref nextLine);
            }
        }

        private static void DrawWatermarkLine(Mat frame, string text, double fontScale, int thickness, int lineHeight, ref int lineIndex)
        {
            string line2 = text;
            var size2 = Cv2.GetTextSize(line2, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
            int x2 = Math.Max(8, frame.Width - size2.Width - 15);
            int y2 = (int)(lineHeight * 1.1 * (lineIndex + 1));
            Cv2.PutText(frame, line2, new OpenCvSharp.Point(x2, y2),
                HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
            Cv2.PutText(frame, line2, new OpenCvSharp.Point(x2, y2),
                HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
            lineIndex++;
        }

        private string _toastMessage;
        private bool _isToastVisible;
        private ToastSeverity _toastSeverity = ToastSeverity.Success;
        private CancellationTokenSource _toastCts;
        public string ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }
        public bool IsToastVisible { get => _isToastVisible; set => SetProperty(ref _isToastVisible, value); }
        public ToastSeverity ToastSeverity { get => _toastSeverity; set => SetProperty(ref _toastSeverity, value); }

        private string _previewOrderRemarkText = "";
        private string _previewOrderDetailText = "";
        private string _previewOrderItemCountText = "";
        private string _previewAlertText = "";
        private bool _isPreviewOrderNoticeVisible;
        private bool _isPreviewAlertVisible;
        private bool _isPreviewAlertCritical;
        private CancellationTokenSource _previewAlertCts;
        public string PreviewOrderRemarkText { get => _previewOrderRemarkText; private set => SetProperty(ref _previewOrderRemarkText, value); }
        public string PreviewOrderDetailText { get => _previewOrderDetailText; private set => SetProperty(ref _previewOrderDetailText, value); }
        public string PreviewOrderItemCountText { get => _previewOrderItemCountText; private set => SetProperty(ref _previewOrderItemCountText, value); }
        public string PreviewAlertText { get => _previewAlertText; private set => SetProperty(ref _previewAlertText, value); }
        public bool IsPreviewOrderNoticeVisible { get => _isPreviewOrderNoticeVisible; private set => SetProperty(ref _isPreviewOrderNoticeVisible, value); }
        public bool IsPreviewAlertVisible { get => _isPreviewAlertVisible; private set => SetProperty(ref _isPreviewAlertVisible, value); }
        public bool IsPreviewAlertCritical { get => _isPreviewAlertCritical; private set => SetProperty(ref _isPreviewAlertCritical, value); }

        private string _logSearchText = "";
        public string LogSearchText { get => _logSearchText; set { SetProperty(ref _logSearchText, value); FilterLogs(); } }
        private ObservableCollection<ScanRecord> _allLogs = new ObservableCollection<ScanRecord>();
        public ObservableCollection<ScanRecord> FilteredLogs { get; } = new ObservableCollection<ScanRecord>();

        private System.Windows.Rect _lastZoomRect;
        public System.Windows.Rect LastZoomRect { get => _lastZoomRect; private set => SetProperty(ref _lastZoomRect, value); }

        private System.Windows.Size _cameraFrameSize;
        public System.Windows.Size CameraFrameSize { get => _cameraFrameSize; private set => SetProperty(ref _cameraFrameSize, value); }

        private double? _previewZoomScale;
        public double? PreviewZoomScale { get => _previewZoomScale; set => SetProperty(ref _previewZoomScale, value); }

        private CameraBarcodeGuideGeometry? _previewGuideGeometry;
        public CameraBarcodeGuideGeometry? PreviewGuideGeometry
        {
            get => _previewGuideGeometry;
            set => SetProperty(ref _previewGuideGeometry, value);
        }

        private bool _isZoomingActive;
        public bool IsZoomingActive { get => _isZoomingActive; private set => SetProperty(ref _isZoomingActive, value); }

        private volatile bool _suppressVideoPreviewUpdates;
        public bool SuppressVideoPreviewUpdates { get => _suppressVideoPreviewUpdates; set => _suppressVideoPreviewUpdates = value; }
        public BitmapSource VideoFrame { get => _videoFrame; set => SetProperty(ref _videoFrame, value); }
        public string CurrentMode
        {
            get => _currentMode;
            set
            {
                string normalizedMode = AppConfig.NormalizeRecordingMode(value);
                if (!SetProperty(ref _currentMode, normalizedMode))
                    return;

                if (Config != null && Config.RecordingMode != normalizedMode)
                {
                    Config.RecordingMode = normalizedMode;
                    SaveConfig();
                }
                ScheduleRefreshBarcodes();
            }
        }
        public string CurrentOrderId { get => _currentOrderId; set => SetProperty(ref _currentOrderId, value); }
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (!SetProperty(ref _isRecording, value)) return;
                OnPropertyChanged(nameof(CanSwitchWorkstation));
                ScheduleRefreshBarcodes();
                if (!value)
                    ClearPreviewOrderNotice();
            }
        }
        private int _preRecordBufferFrameCount;
        private int _preRecordBufferCapacityFrames;
        private double _preRecordBufferProgress;
        private bool _isPreRecordBufferFull;
        private bool _isPreRecordBufferRolling;
        private bool _preRecordRollingTransitionPending;
        private string _preRecordBufferStatusText = "预录制缓存未开始";
        private string _preRecordBufferFrameSummaryText = "0 / 0 帧";

        public bool IsPreRecordBufferVisible => Config?.EnableEventRecordingBuffer == true;
        public int PreRecordBufferFrameCount { get => _preRecordBufferFrameCount; private set => SetProperty(ref _preRecordBufferFrameCount, value); }
        public int PreRecordBufferCapacityFrames { get => _preRecordBufferCapacityFrames; private set => SetProperty(ref _preRecordBufferCapacityFrames, value); }
        public double PreRecordBufferProgress { get => _preRecordBufferProgress; private set => SetProperty(ref _preRecordBufferProgress, value); }
        public bool IsPreRecordBufferFull { get => _isPreRecordBufferFull; private set => SetProperty(ref _isPreRecordBufferFull, value); }
        public bool IsPreRecordBufferRolling { get => _isPreRecordBufferRolling; private set => SetProperty(ref _isPreRecordBufferRolling, value); }
        public string PreRecordBufferStatusText { get => _preRecordBufferStatusText; private set => SetProperty(ref _preRecordBufferStatusText, value); }
        public string PreRecordBufferFrameSummaryText { get => _preRecordBufferFrameSummaryText; private set => SetProperty(ref _preRecordBufferFrameSummaryText, value); }
        public bool IsShutdownInProgress { get => _isShutdownInProgress; private set => SetProperty(ref _isShutdownInProgress, value); }
        public double DiskUsagePercent { get => _diskUsagePercent; set => SetProperty(ref _diskUsagePercent, value); }
        public string DiskUsageText { get => _diskUsageText; set => SetProperty(ref _diskUsageText, value); }
        public AppConfig Config
        {
            get => _config;
            set
            {
                bool wasEventBufferEnabled = _config?.EnableEventRecordingBuffer == true;
                int previousPreRecordBufferMb = _config?.PreRecordBufferMB ?? 0;
                if (SetProperty(ref _config, value))
                {
                    if (wasEventBufferEnabled && !value.EnableEventRecordingBuffer)
                    {
                        ClearPreRecordBuffer();
                        ClearPendingEventRecordingFrames();
                        RuntimeLog.Info("Recording", "Event recording buffer disabled; released pre-record frames");
                    }
                    else if (value.EnableEventRecordingBuffer
                        && (!wasEventBufferEnabled || previousPreRecordBufferMb != value.PreRecordBufferMB))
                    {
                        RefreshPreRecordBufferCapacityAfterConfigChange();
                    }
                    OnPropertyChanged(nameof(IsCameraBarcodeRecognitionEnabled));
                    OnPropertyChanged(nameof(IsRecordingWorkstation));
                    OnPropertyChanged(nameof(IsMainConnectionVisible));
                    OnPropertyChanged(nameof(MainConnectionButtonText));
                    OnPropertyChanged(nameof(MainConnectionButtonToolTip));
                    OnPropertyChanged(nameof(ComputerDisplayName));
                    OnPropertyChanged(nameof(ScanInputPlaceholder));
                    OnPropertyChanged(nameof(IsPreRecordBufferVisible));
                    PublishPreRecordBufferStatus(force: true);
                }
            }
        }
        public string ScanInputPlaceholder =>
            ResolveScanInputPlaceholder(Config.EnableGlobalKeyboard);

        internal static string ResolveScanInputPlaceholder(bool enableGlobalKeyboard) =>
            AppLanguage.Get(
                enableGlobalKeyboard
                    ? "ScanInput.PlaceholderGlobal"
                    : "ScanInput.PlaceholderLocal");
        public string ComputerDisplayName =>
            string.IsNullOrWhiteSpace(Config?.NodeName) ? "电脑1" : Config.NodeName.Trim();
        public string WorkstationPrintStatusText { get => _workstationPrintStatusText; set => SetProperty(ref _workstationPrintStatusText, value); }
        public string WorkstationStatusToolTip
        {
            get => _workstationStatusToolTip;
            set
            {
                if (SetProperty(ref _workstationStatusToolTip, value))
                    OnPropertyChanged(nameof(MainConnectionButtonToolTip));
            }
        }
        public string OrderIntegrationStatusText { get => _orderIntegrationStatusText; private set => SetProperty(ref _orderIntegrationStatusText, value); }
        public string UserscriptSetupStatusText { get => _userscriptSetupStatusText; private set => SetProperty(ref _userscriptSetupStatusText, value); }
        public string UserscriptSetupShortStatusText { get => _userscriptSetupShortStatusText; private set => SetProperty(ref _userscriptSetupShortStatusText, value); }
        public string UserscriptButtonText { get => _userscriptButtonText; private set => SetProperty(ref _userscriptButtonText, value); }
        public ObservableCollection<MobileBackupDeviceStatus> MobileBackupDeviceStatuses { get; } = new();
        public string ConnectedDeviceText { get => _connectedDeviceText; private set => SetProperty(ref _connectedDeviceText, value); }
        public string ConnectedDeviceToolTip { get => _connectedDeviceToolTip; private set => SetProperty(ref _connectedDeviceToolTip, value); }
        public bool HasConnectedDevices { get => _hasConnectedDevices; private set => SetProperty(ref _hasConnectedDevices, value); }
        public string MonitorAccessAddress
        {
            get => _monitorAccessAddress;
            set
            {
                if (SetProperty(ref _monitorAccessAddress, value))
                    OnPropertyChanged(nameof(ComputerIpAddress));
            }
        }
        public string ComputerIpAddress
        {
            get
            {
                string address = MonitorAccessAddress?.Trim() ?? "";
                if (Uri.TryCreate($"http://{address}", UriKind.Absolute, out Uri uri))
                    return uri.Host;
                int separator = address.LastIndexOf(':');
                return separator > 0 ? address[..separator] : address;
            }
        }

        // 条形码（自动计算）
        private string _barcode1Label;
        private string _barcode2Label;
        private BitmapSource _barcode1Image;
        private BitmapSource _barcode2Image;
        private double _barcode1CooldownProgress;
        private double _barcode2CooldownProgress;
        public string Barcode1Label { get => _barcode1Label; set => SetProperty(ref _barcode1Label, value); }
        public string Barcode2Label { get => _barcode2Label; set => SetProperty(ref _barcode2Label, value); }
        public BitmapSource Barcode1Image { get => _barcode1Image; set => SetProperty(ref _barcode1Image, value); }
        public BitmapSource Barcode2Image { get => _barcode2Image; set => SetProperty(ref _barcode2Image, value); }
        public double Barcode1CooldownProgress { get => _barcode1CooldownProgress; set => SetProperty(ref _barcode1CooldownProgress, value); }
        public double Barcode2CooldownProgress { get => _barcode2CooldownProgress; set => SetProperty(ref _barcode2CooldownProgress, value); }
        public ICommand ClearScanInputCommand { get; }
        public ICommand ClearSearchCommand { get; }
        private CancellationTokenSource _barcode1CooldownCts;
        private CancellationTokenSource _barcode2CooldownCts;
        private bool _barcode1OnCooldown;
        private bool _barcode2OnCooldown;
        private void ScheduleRefreshBarcodes()
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(RefreshBarcodes), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void ResumeVideoPreviewUpdatesAfterWindowMove()
        {
            SuppressVideoPreviewUpdates = false;
            BeginPreviewSession(clearFrame: false);
        }
        private void RefreshBarcodes()
        {
            try
            {
                // 行1: 扫码框有内容→清除；空→切换模式
                string cmd1; string label1;
                if (!string.IsNullOrEmpty(_scanInputText))
                { cmd1 = "CLEAR"; label1 = AppLanguage.Get("Main.BarcodeClear").Replace("\\n", "\n"); }
                else if (_currentMode == "发货")
                { cmd1 = "BACK"; label1 = AppLanguage.Get("Main.BarcodeReturn").Replace("\\n", "\n"); }
                else
                { cmd1 = "SHIP"; label1 = AppLanguage.Get("Main.BarcodeShipping").Replace("\\n", "\n"); }
                // 行2: 未录制→开录；录制中→停录
                string cmd2 = _isRecording ? "STOP" : "START";
                string label2 = AppLanguage.Get(_isRecording ? "Main.BarcodeStop" : "Main.BarcodeStart").Replace("\\n", "\n");
                if (!_barcode1OnCooldown)
                {
                    Barcode1Label = label1;
                    Barcode1Image = BarcodeHelper.Generate(cmd1, 70, 3);
                }
                if (!_barcode2OnCooldown)
                {
                    Barcode2Label = label2;
                    Barcode2Image = BarcodeHelper.Generate(cmd2, 70, 3);
                }
            }
            catch { }
        }

        private async void HideBarcode1Temporarily()
        {
            _barcode1CooldownCts?.Cancel();
            var cts = _barcode1CooldownCts = new CancellationTokenSource();
            _barcode1OnCooldown = true;
            Barcode1Image = null; Barcode1Label = "";
            Barcode1CooldownProgress = 0;
            double totalMs = Config.BarcodeCooldownSeconds * 1000;
            const int step = 50;
            double elapsed = 0;
            try
            {
                while (elapsed < totalMs)
                {
                    await Task.Delay(step, cts.Token);
                    elapsed += step;
                    Barcode1CooldownProgress = Math.Min(100, elapsed / totalMs * 100);
                }
            }
            catch { return; }
            _barcode1OnCooldown = false;
            Barcode1CooldownProgress = 0;
            if (!cts.IsCancellationRequested) RefreshBarcodes();
        }

        private async void HideBarcode2Temporarily()
        {
            _barcode2CooldownCts?.Cancel();
            var cts = _barcode2CooldownCts = new CancellationTokenSource();
            _barcode2OnCooldown = true;
            Barcode2Image = null; Barcode2Label = "";
            Barcode2CooldownProgress = 0;
            double totalMs = Config.BarcodeCooldownSeconds * 1000;
            const int step = 50;
            double elapsed = 0;
            try
            {
                while (elapsed < totalMs)
                {
                    await Task.Delay(step, cts.Token);
                    elapsed += step;
                    Barcode2CooldownProgress = Math.Min(100, elapsed / totalMs * 100);
                }
            }
            catch { return; }
            _barcode2OnCooldown = false;
            Barcode2CooldownProgress = 0;
            if (!cts.IsCancellationRequested) RefreshBarcodes();
        }

        public ICommand ScanCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPlaybackCommand { get; }
        public ICommand ToggleModeCommand { get; }
        public ICommand ToggleRecordingCommand { get; }
        public ICommand OpenStatsCommand { get; } // 打开统计面板
        public ICommand ResetEncoderDetectCommand { get; } // 重置编码器检测
        public ICommand CopyMonitorAddressCommand { get; }
        public ICommand SwitchWorkstationCommand { get; }
        internal static bool AllowLanAccessSetupOnStartup { get; set; } = true;

        public MainViewModel()
        {
            // 跳过 XAML 设计器环境，避免 XDG0003 等设计时错误
            if (DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject()))
            {
                ScanCommand = new RelayCommand<string>(_ => { });
                OpenSettingsCommand = new RelayCommand(() => { });
                OpenPlaybackCommand = new RelayCommand(() => { });
                ToggleModeCommand = new RelayCommand(() => { });
                ToggleRecordingCommand = new RelayCommand(() => { });
                OpenStatsCommand = new RelayCommand(() => { });
                CopyMonitorAddressCommand = new RelayCommand(() => { });
                SwitchWorkstationCommand = new RelayCommand(() => { });
                ClearScanInputCommand = new RelayCommand(() => { });
                ClearSearchCommand = new RelayCommand(() => { });
                return;
            }

            LoadConfig();
            InitializeCameraBarcodeRecognition();
            // 在起动时后台探测可用 GPU 编码器并缓存
            Task.Run(() => {
                _isEncoderDetectRunning = true;
                try
                {
                    if (Config.IsEncoderDetected
                        && Config.EncoderDetectionCacheVersion == CurrentEncoderDetectionCacheVersion
                        && Config.EncoderOptionsCache != null
                        && Config.ValidatedEncodersCache != null)
                    {
                        CachedEncoderOptions = Config.EncoderOptionsCache;
                        ValidatedEncoders = new HashSet<string>(Config.ValidatedEncodersCache);
                        EncoderPerformanceScores = (Config.EncoderPerformanceCache ?? [])
                            .Where(entry => entry.SchemaVersion == EncoderScoreSchemaVersion
                                && entry.CompletedSuccessfully
                                && entry.Width == EncoderScoreMode.Width
                                && entry.Height == EncoderScoreMode.Height
                                && entry.VideoCqp == EncoderScoreCqp
                                && entry.MeasuredEncodingFps > 0)
                            .GroupBy(entry => entry.Encoder, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.TestedAt).First().MeasuredEncodingFps, StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        EncoderDetectionResult detection = DetectAvailableEncodersSync();
                        CachedEncoderOptions = detection.Options;
                        ValidatedEncoders = detection.ValidatedEncoders;
                        EncoderPerformanceScores = detection.PerformanceScores;

                        // 保存到配置中
                        Config.EncoderOptionsCache = detection.Options;
                        Config.ValidatedEncodersCache = detection.ValidatedEncoders.ToList();
                        Config.EncoderPerformanceCache = detection.PerformanceResults;
                        Config.IsEncoderDetected = detection.Succeeded;
                        Config.EncoderDetectionCacheVersion = CurrentEncoderDetectionCacheVersion;
                        UpdateEncoderDriverWarning(Config, detection.NvencDriverIssue);
                    }

                    string driverWarningMessage = BuildEncoderDriverWarningMessage(Config);
                    SaveConfig();
                    if (!string.IsNullOrWhiteSpace(driverWarningMessage))
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                            AppDialog.Warning(
                                null,
                                driverWarningMessage,
                                "显卡驱动版本过低"));
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("EncoderDetect", "Startup encoder detection failed", ex);
                }
                finally
                {
                    _isEncoderDetectRunning = false;
                }
            });
            InitDatabase();
            InitializeRecordingTransfers();
            RefreshTodayStats();
            RestoreRecentScanRecords();
            _speechService = new SpeechService
            {
                EnableSoundPrompt = Config.EnableSoundPrompt,
                MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech,
                EnableAiTts = Config.EnableAiTts,
                AiTtsEngine = Config.AiTtsEngine,
                AiTtsSpeakerId = Config.AiTtsSpeakerId,
                AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId,
                AiTtsSpeed = Config.AiTtsSpeed,
                EdgeTtsVoice = Config.EdgeTtsVoice,
                EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice
            };
            _speechService.UpdateBreakWords(Config.TtsBreakWords);
            if (Config.EnableAiTts)
                _speechService.InitAiTts();
            _alertService = new AlertService(
                PresentAlert,
                _speechService.PlayAlert,
                interruptAudio: _speechService.Stop,
                preGenerate: (text, style) => _speechService.PreGenerateCache(text, style == AlertVoiceStyle.Warning),
                pauseAudio: _speechService.PauseForRecording,
                resumeAudio: _speechService.ResumeAfterRecording);
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            ResetEncoderDetectCommand = new RelayCommand(ResetEncoderDetect);
            CopyMonitorAddressCommand = new RelayCommand(CopyMonitorAddress);
            SwitchWorkstationCommand = new RelayCommand(SwitchWorkstation);
            ClearScanInputCommand = new RelayCommand(() => ScanInputText = "");
            ClearSearchCommand = new RelayCommand(() => LogSearchText = "");
            InitializeSystem();
            StartUiHeartbeat();
            RefreshBarcodes();
            InitGlobalKeyboardHook();
        }

        private void InitGlobalKeyboardHook()
        {
            _globalKeyHook = new GlobalKeyboardHook();
            ApplyGlobalKeyboardConfig();
            _globalKeyHook.BarcodeScanned += OnGlobalBarcodeScanned;
            if (Config.EnableGlobalKeyboard)
                _globalKeyHook.Start();
        }

        private void ApplyGlobalKeyboardConfig()
        {
            _globalKeyHook?.ConfigureAutoSubmit(
                Config.EnableScannerAutoSubmit,
                Config.ScannerAutoSubmitMinLength,
                Config.ScannerAutoSubmitQuietMs,
                Config.ScannerAutoSubmitMaxAverageIntervalMs,
                Config.ScannerAutoSubmitMaxKeyIntervalMs,
                IsAutoSubmitScanCandidate);
        }

        private void OnGlobalBarcodeScanned(string barcode)
        {
            if (_isDisposed || _shutdownRequested) return;
            HandleScan(barcode);
        }

        private void InitializeCameraBarcodeRecognition()
        {
            _cameraBarcodeRecognition = new CameraBarcodeRecognitionService(
                IsAutoSubmitScanCandidate,
                _ => TimeSpan.FromSeconds(Config.CameraSameBarcodeConfirmationSeconds),
                () => TimeSpan.FromSeconds(Config.CameraBarcodeRearmSeconds),
                guideIntervalProvider: () => CameraBarcodeSpeed.GuideIntervalFor(
                    Config.CameraBarcodeRecognitionSpeed,
                    _actualCameraFps),
                guideGeometryProvider: () => new CameraBarcodeGuideGeometry(
                    Config.CameraBarcodeGuideWidthRatio,
                    Config.CameraBarcodeGuideHeightRatio,
                    Config.CameraBarcodeGuideOffsetX,
                    Config.CameraBarcodeGuideOffsetY),
                confirmationHitsProvider: () => Config.CameraSameBarcodeConfirmationHits);
            _cameraBarcodeRecognition.StatusChanged += OnCameraBarcodeStatusChanged;
            _cameraBarcodeRecognition.BarcodeConfirmed += OnCameraBarcodeConfirmed;
            _cameraBarcodeRecognition.InvalidCandidate += OnCameraBarcodeInvalidCandidate;
            // 解码到合法条码立即响的独立反馈音，不依赖候选/确认状态。
            _cameraBarcodeRecognition.BarcodeRecognized += _ => _speechService?.PlayShortBeep();
        }

        private bool CanSubmitCameraBarcode()
        {
            return Config?.EnableCameraBarcodeRecognition == true
                && !_isDisposed
                && !_shutdownRequested
                && !_isSetupWizardActive
                && !_isCameraSleeping
                && !IsBusy;
        }

        private void TrySubmitCameraBarcodeFrame(Mat frame)
        {
            if (!CanSubmitCameraBarcode())
                return;

            _cameraBarcodeRecognition?.TrySubmitFrame(frame);
        }

        public async Task<string> ScanHostPairingQrAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<string> scan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_cameraPairingQrLock)
            {
                if (_cameraPairingQrScan != null)
                    throw new InvalidOperationException("正在识别连接二维码");
                _cameraPairingQrScan = scan;
            }

            try
            {
                return await scan.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_cameraPairingQrLock)
                {
                    if (ReferenceEquals(_cameraPairingQrScan, scan))
                        _cameraPairingQrScan = null;
                }
            }
        }

        private void TrySubmitCameraPairingQrFrame(Mat frame)
        {
            TaskCompletionSource<string> scan;
            lock (_cameraPairingQrLock)
                scan = _cameraPairingQrScan;
            if (scan == null || scan.Task.IsCompleted
                || Interlocked.CompareExchange(ref _cameraPairingQrDecodeBusy, 1, 0) != 0)
                return;

            Mat copy = frame.Clone();
            _ = Task.Run(() =>
            {
                try
                {
                    string result = _cameraPairingQrDecoder.Decode(copy);
                    if (!string.IsNullOrWhiteSpace(result))
                        scan.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("保存主机", $"识别连接二维码失败：{ex.Message}");
                }
                finally
                {
                    copy.Dispose();
                    Interlocked.Exchange(ref _cameraPairingQrDecodeBusy, 0);
                }
            });
        }

        private void OnCameraBarcodeConfirmed(string code)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (CameraBarcodeRuntimeOptions.ShadowMode)
                {
                    LogBarcodeRecordingComparison(code, fromCamera: true, dryRun: true);
                    return;
                }

                if (CanSubmitCameraBarcode())
                {
                    HandleScan(code, fromCamera: true);
                }
            }));
        }

        private void OnCameraBarcodeStatusChanged(CameraBarcodeRecognitionStatus status)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() => ApplyCameraBarcodeStatus(status)));
        }

        private void OnCameraBarcodeInvalidCandidate(string code)
        {
            if (_isDisposed || Config?.EnableCameraBarcodeRecognition != true)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDisposed || Config == null)
                    return;

                string hint = CameraBarcodeCandidatePolicy.GetOrderIdLengthHint(Config.OrderIdRegex);
                string message = string.IsNullOrEmpty(hint)
                    ? $"识别到非面单条码：{code}，已忽略"
                    : $"条码长度不符：实际 {code.Length} 位（{hint}），已忽略";
                RuntimeLog.Warn("CameraBarcode", $"Invalid candidate ignored: {code}");
                ShowToast(message, ToastSeverity.Warning);
            }));
        }

        private void ApplyCameraBarcodeStatus(CameraBarcodeRecognitionStatus status)
        {
            if (_isDisposed || Config?.EnableCameraBarcodeRecognition != true)
                return;

            if (status.State == CameraBarcodeRecognitionState.Confirmed)
            {
                _cameraBarcodeFeedbackCts?.Cancel();
                var cts = _cameraBarcodeFeedbackCts = new CancellationTokenSource();
                IsCameraBarcodeCandidate = false;
                IsCameraBarcodeConfirmed = true;
                CameraBarcodeStatusText = $"已识别 {status.Code}";
                _ = ResetCameraBarcodeFeedbackAsync(cts);
                return;
            }

            if (IsCameraBarcodeConfirmed)
                return;

            IsCameraBarcodeCandidate = status.State == CameraBarcodeRecognitionState.Candidate;
            CameraBarcodeStatusText = IsCameraBarcodeCandidate ? "识别中，请保持稳定" : "将面单条形码放入框内";
        }

        private async Task ResetCameraBarcodeFeedbackAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(1200, cts.Token);
                if (!cts.IsCancellationRequested && !_isDisposed)
                {
                    IsCameraBarcodeConfirmed = false;
                    IsCameraBarcodeCandidate = false;
                    CameraBarcodeStatusText = "将面单条形码放入框内";
                }
            }
            catch (OperationCanceledException) { }
        }

        private void ResetCameraBarcodeRecognition(bool preserveConfirmedCodes = false)
        {
            _cameraBarcodeFeedbackCts?.Cancel();
            _cameraBarcodeRecognition?.Reset(preserveConfirmedCodes);
            IsCameraBarcodeCandidate = false;
            IsCameraBarcodeConfirmed = false;
            CameraBarcodeStatusText = "将面单条形码放入框内";
        }

        private void StartUiHeartbeat()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            _lastUiHeartbeatAt = DateTime.Now;
            Interlocked.Exchange(ref _archiveUiHeartbeatUtcTicks, DateTime.UtcNow.Ticks);
            _uiHeartbeatTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal,
                dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uiHeartbeatTimer.Tick += (_, __) =>
            {
                _lastUiHeartbeatAt = DateTime.Now;
                Interlocked.Exchange(ref _archiveUiHeartbeatUtcTicks, DateTime.UtcNow.Ticks);
                if (_mobileBackupStatusDate != DateTime.Today)
                    RefreshMobileBackupStatuses();
                if (DateTime.Now - _lastUserscriptStatusRefreshAt >= TimeSpan.FromSeconds(15))
                    RefreshUserscriptStatus();
                QueueRecordingWorkstationHeartbeat();
                if (Interlocked.Exchange(ref _archiveBackupSummaryDirty, 0) != 0)
                    RefreshArchiveBackupSummary();
            };
            _uiHeartbeatTimer.Start();
        }

        private void InitDatabase()
        {
            try
            {
                _db = new VideoDatabase(_dbFilePath);
                if (_archiveService != null)
                {
                    _archiveService.BackupTargetAvailabilityChanged -=
                        OnArchiveTargetAvailabilityChanged;
                    _archiveService.WorkerStateChanged -= OnArchiveWorkerStateChanged;
                    _archiveService.ArchiveQueueChanged -= OnArchiveQueueChanged;
                    _archiveService.Dispose();
                }
                _archiveTargetUnavailable = false;
                _archiveUnavailableRoot = "";
                _archiveService = new ArchiveService(
                    _db,
                    new NasArchiveProvider(),
                    archiveTargetResolver: () =>
                        StorageLocationResolver.GetOrderedBackupLocations(Config),
                    loadStateProvider: GetArchiveLoadState);
                _archiveService.BackupTargetAvailabilityChanged +=
                    OnArchiveTargetAvailabilityChanged;
                _archiveService.WorkerStateChanged += OnArchiveWorkerStateChanged;
                _archiveService.ArchiveQueueChanged += OnArchiveQueueChanged;
                _nasCircularCleanup = new NasCircularCleanupService(_db);
                RefreshArchiveBackupSummary();
            }
            catch (Exception ex)
            {
                _archiveService = null;
                _nasCircularCleanup = null;
                AppDialog.Error(null, $"数据库初始化失败，部分功能将不可用：{ex.Message}", "启动警告");
            }
        }

        private void RefreshTodayStats()
        {
            try
            {
                var todayList = _db?.GetAggregatedStats(DateTime.Today, DateTime.Today, "day", "pc");
                if (todayList != null && todayList.Count > 0)
                {
                    var today = todayList[0];
                    TotalPieces = today.TotalPieces;
                    _totalPackTime = TimeSpan.FromSeconds(today.TotalDurationSec);
                }
                else
                {
                    TotalPieces = 0;
                    _totalPackTime = TimeSpan.Zero;
                }
                OnPropertyChanged(nameof(TotalPackTimeDisplay)); OnPropertyChanged(nameof(AveragePackTimeDisplay));
            }
            catch { }
        }

        private void RestoreRecentScanRecords()
        {
            try
            {
                var records = _db?.GetRecentCompletedVideos(DateTime.Today, 20, "pc");
                if (records == null) return;

                _allLogs.Clear();
                foreach (var record in records)
                {
                    _allLogs.Add(new ScanRecord(
                        record.OrderId,
                        "已保存",
                        record.StartTime.ToString("HH:mm:ss"),
                        record.Mode));
                }
                FilterLogs();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("ScanHistory", "Failed to restore recent scan records", ex);
            }
        }

        private void ToggleMode() { CurrentMode = CurrentMode == "发货" ? "退货" : "发货"; ShowToast($"已切换为: {CurrentMode}"); Speak(CurrentMode == "发货" ? DefaultSpeechCatalog.SwitchToShipping : DefaultSpeechCatalog.SwitchToReturn); }

        private void PauseSpeechForRecording() => _alertService?.PauseAudio();

        private void ResumeSpeechWhenCameraIdle()
        {
            if (_alertService == null || _isDisposed) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800);
                    if (_isDisposed || IsRecording || IsBusy) return;
                    _alertService.ResumeAudio();
                }
                catch { }
            });
        }

        // ========================== 核心逻辑：恢复 MAN_ 前缀 ==========================
        private async void ToggleRecording() 
        {
            NotifyUserActivity();
            if (IsBusy || _isDisposed || _shutdownRequested) return;
            if (!await _recorderLock.WaitAsync(0)) return; 

            try 
            {
                if (IsRecording) 
                {
                    PauseSpeechForRecording();
                    await InternalStopRecordingAsync();
                    QueuePostStopMux("手动停止");
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
                    return;
                }
                else 
                {
                    // 恢复逻辑：如果扫码框为空，使用 MAN_ 前缀
                    string input = ScanInputText?.Trim().ToUpper() ?? "";
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        CurrentOrderId = $"MAN_{DateTime.Now:HHmmss}";
                    }
                    else
                    {
                        CurrentOrderId = input;
                    }
                    
                    // 手动触发录制时也开启缩放逻辑
                    _lastScanTime = DateTime.Now;
                    _isScanning = true;
                    _delayBeforeZooming = Config.ZoomDelaySeconds > 0;
                    if (!_delayBeforeZooming)
                    {
                        _zoomPhase = ZoomPhase.ZoomingIn;
                        _zoomPhaseStartTime = DateTime.Now;
                        LastZoomRect = System.Windows.Rect.Empty;
                        IsZoomingActive = true;
                    }

                    Debug.WriteLine($"[Zoom] 手动开启录制触发缩放: ID={CurrentOrderId}, Delay={Config.ZoomDelaySeconds}");

                    if (Config.EnableEventRecordingBuffer)
                    {
                        _pendingPreRecordFrames = SnapshotPreRecordFrames(
                            DateTime.Now,
                            out _pendingPreRecordStartTime,
                            out _pendingPreRecordTimestamps);
                    }
                    await InternalStartRecordingAsync();
                    ScanInputText = ""; // 启动录制后清空

                    // 没有输入单号时语音提示（不打断"开始录制"，排队等播完后再警告）
                    if (CurrentOrderId.StartsWith("MAN_"))
                    {
                        PublishScannerAlert(
                            "missing-order-number",
                            "警告：没有单号",
                            DefaultSpeechCatalog.MissingOrderNumber,
                            repeatCount: 3);
                    }

                    // 语音播报完成后再暂停，避免"开始录制"被延迟
                    if (IsRecording)
                        PauseSpeechForRecording();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ToggleRecording] 严重异常: {ex.Message}");
            }
            finally 
            { 
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
                _recorderLock.Release(); 
            }
        }

        private void HandleScan(string scanResult)
        {
            HandleScan(scanResult, fromCamera: false);
        }

        private async void HandleScan(string scanResult, bool fromCamera)
        {
            NotifyUserActivity();
            BarcodeRecordingDecision decision = EvaluateBarcodeRecordingDecision(scanResult, fromCamera);
            if (CameraBarcodeRuntimeOptions.ShadowMode)
                LogBarcodeRecordingComparison(decision, fromCamera, dryRun: false);

            if (decision.Reason == BarcodeRecordingDecisionReason.CannotProcess)
            {
                ScanInputText = "";
                return;
            }
            if (decision.Reason == BarcodeRecordingDecisionReason.EmptyInput)
                return;

            string upperResult = decision.NormalizedValue;
            // 编码失败后的去抖窗口内，忽略同一单号的摄像头确认，避免连环重扫。
            if (fromCamera
                && _cameraStartFailedSuppression.IsSuppressed(
                    upperResult,
                    DateTimeOffset.UtcNow,
                    Config?.CameraBarcodeRearmSeconds ?? 3))
            {
                RuntimeLog.Info("CameraBarcode", $"Ignored {upperResult} within failed-start rearm window");
                return;
            }
            // 摄像头触发开始/切换录像后，同码消失时间从这一刻起算，
            // 防止启动流程耗时较长时防重复触发提前失效。
            if (fromCamera
                && _cameraBarcodeRecognition != null
                && (decision.Action == BarcodeRecordingDecisionAction.Start
                    || decision.Action == BarcodeRecordingDecisionAction.Switch))
            {
                _cameraBarcodeRecognition.MarkStartTriggered(upperResult);
            }
            // 立即清空扫码框，防止重复触发
            ScanInputText = "";

            switch (decision.Reason)
            {
                case BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored:
                    RuntimeLog.Info("CameraBarcode", $"Ignored current recording barcode while same-code stop is disabled: {upperResult}");
                    return;
                case BarcodeRecordingDecisionReason.ProductBarcodeIgnored:
                    RuntimeLog.Info("Scan", $"Ignored product barcode: {upperResult}");
                    return;
                case BarcodeRecordingDecisionReason.CooldownOrderQueued:
                    _pendingScanDuringCooldown = upperResult;
                    RuntimeLog.Info("Scan", $"Scan queued during cooldown: {upperResult}");
                    ShowToast("扫码过快，已保留最后一个单号", ToastSeverity.Warning);
                    return;
                case BarcodeRecordingDecisionReason.CooldownIgnored:
                    return;
                case BarcodeRecordingDecisionReason.ClearCommand:
                    StartInputCooldown();
                    ShowToast("扫码框已清除", ToastSeverity.Information);
                    return;
                case BarcodeRecordingDecisionReason.ShippingCommand:
                    CurrentMode = "发货";
                    StartInputCooldown();
                    ShowToast("切换为发货模式");
                    Speak(DefaultSpeechCatalog.SwitchToShipping);
                    return;
                case BarcodeRecordingDecisionReason.ReturnCommand:
                    CurrentMode = "退货";
                    StartInputCooldown();
                    ShowToast("切换为退货模式");
                    Speak(DefaultSpeechCatalog.SwitchToReturn);
                    return;
                case BarcodeRecordingDecisionReason.StartCommand:
                    StartInputCooldown();
                    ToggleRecording();
                    return;
                case BarcodeRecordingDecisionReason.StopCommand:
                    StartInputCooldown();
                    _ = SafeStopRecordingAsync(true, mergeAfterStop: true);
                    return;
                case BarcodeRecordingDecisionReason.RecordingOrderMissing:
                    ShowToast("当前录像未绑定单号，无法同码停录", ToastSeverity.Warning);
                    SpeakWarning(DefaultSpeechCatalog.RecordingHasNoOrderNumber);
                    return;
                case BarcodeRecordingDecisionReason.RecordingOrderMismatch:
                    ShowToast($"单号不一致：{upperResult}", ToastSeverity.Warning);
                    SpeakWarning(DefaultSpeechCatalog.OrderNumberMismatch);
                    return;
                case BarcodeRecordingDecisionReason.InvalidOrderNumber:
                    RuntimeLog.Info(
                        "Scan",
                        $"Invalid order number blocked, source={(fromCamera ? "camera" : "scanner/manual")}: {upperResult}");
                    PublishScannerAlert(
                        $"invalid-order-number:{upperResult}",
                        "非法单号，已拦截",
                        DefaultSpeechCatalog.InvalidOrderNumber);
                    return;
            }

            // 同码停录由统一决策器确认，避免影子日志与真实流程分别维护一套规则。
            if (decision.Reason == BarcodeRecordingDecisionReason.SameCodeMatched)
            {
                if (!await _recorderLock.WaitAsync(0))
                {
                    ShowToast("录制状态正在切换，请稍后再试", ToastSeverity.Information);
                    return;
                }

                try
                {
                    _stopReason = "同码停录";
                    if (Config.EnableEventRecordingBuffer && Config.SameCodePostRecordSeconds > 0)
                    {
                        // 同码重复识别只允许首次触发收尾，避免反复重置定时器导致无限录制。
                        CancellationTokenSource pendingPostRoll = _sameCodePostRollCts;
                        if (pendingPostRoll != null && !pendingPostRoll.IsCancellationRequested)
                        {
                            RuntimeLog.Info("Recording", "Same-code post-roll already pending; duplicate stop trigger ignored");
                            ShowToast("已触发停录，正在录制收尾画面", ToastSeverity.Information);
                            return;
                        }

                        // 收尾是异步的，先给用户即时反馈；否则语音会等到收尾结束后才播报。
                        // 在暂停录制期间的 AI 语音生成前入队，避免 PauseForRecording 阻塞这条提示。
                        Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
                        PauseSpeechForRecording();
                        _sameCodePostRollCts?.Cancel();
                        var postRollCts = _sameCodePostRollCts = new CancellationTokenSource();
                        RuntimeLog.Info("Recording", $"Same-code post-roll scheduled seconds={Config.SameCodePostRecordSeconds:F1}");
                        _ = CompleteSameCodePostRollAsync(postRollCts);
                        ShowToast($"已触发停录，将继续录制 {Config.SameCodePostRecordSeconds:F1} 秒");
                        return;
                    }
                    await InternalStopRecordingAsync();
                    QueuePostStopMux("同码停录");
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("单号匹配，已停止录制");
                    Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HandleScan] 同码停录异常: {ex.Message}");
                    RuntimeLog.Error("Scan", "Matched barcode stop failed", ex);
                }
                finally
                {
                    if (!IsRecording)
                        ResumeSpeechWhenCameraIdle();
                    _recorderLock.Release();
                }
                return;
            }

            Debug.WriteLine($"[Zoom] 扫码事件触发: ID={upperResult}, ZoomEnabled={Config.EnableSmartZoom}, Delay={Config.ZoomDelaySeconds}");
            StartInputCooldown();

            CurrentOrderId = upperResult;
            _sameCodePostRollCts?.Cancel();
            if (IsRecording) _stopReason = "扫码切换";
            if (!await _recorderLock.WaitAsync(0))
            {
                RuntimeLog.Warn("Scan", $"Recording switch skipped because recorder lock is busy, order={upperResult}");
                return;
            }
            try
            {
                // 预录快照必须在录制串行锁内获取，避免并发扫码分别消费同一环形缓冲。
                List<Mat> pendingPreRecordFrames = null;
                DateTime? pendingPreRecordStartTime = null;
                List<DateTime> pendingPreRecordTimestamps = new();
                if (Config.EnableEventRecordingBuffer)
                    pendingPreRecordFrames = SnapshotPreRecordFrames(DateTime.Now, out pendingPreRecordStartTime, out pendingPreRecordTimestamps);

                // 扫码切换：立即打断上一轮可能还在播放的语音（如"重复单号"×3）
                _alertService?.InterruptAudio();
                if (IsRecording)
                {
                    PauseSpeechForRecording();
                    RuntimeLog.Info("Recording", "连续扫码切换，暂缓音视频合成，等待 stop 或手动停止");
                    await InternalStopRecordingAsync();
                }
                _pendingPreRecordFrames = pendingPreRecordFrames;
                _pendingPreRecordTimestamps = pendingPreRecordTimestamps;
                _pendingPreRecordStartTime = pendingPreRecordStartTime;
                await InternalStartRecordingAsync();
                PublishExtensionScanTaskIfRecordingStarted(upperResult);
                QueuePrintedRefundCheck(upperResult, CurrentMode);

                // 录制已启动、数据库记录已写入，此时检查重复单号（排除刚刚插入的当前记录）
                bool isDuplicate = _db != null && _db.OrderIdExistsRecent(upperResult, excludeRecordId: _currentRecordId);
                if (isDuplicate)
                {
                    PublishScannerAlert(
                        $"duplicate-order-number:{upperResult}",
                        "警告：重复单号，请确认",
                        DefaultSpeechCatalog.DuplicateOrderNumber,
                        repeatCount: 3);
                }

                // 查询快递助手推送的订单信息，在预览画面持续提示并按设置播报
                if (Config.EnableOrderInfoLog)
                    System.Diagnostics.Debug.WriteLine($"[OrderInfo] 扫码查询: {upperResult}, EnableAnnounce={Config.EnableOrderInfoAnnounce}, WebServer={(_webServer != null ? "已启动" : "未启动")}");
                var orderInfo = _webServer?.GetOrderInfo(upperResult);
                SetPreviewOrderNotice(IsRecording ? orderInfo : null);
                if (Config.EnableOrderInfoLog)
                    System.Diagnostics.Debug.WriteLine($"[OrderInfo] 查询结果: {(orderInfo != null ? $"命中 买家=[{orderInfo.BuyerMessage}] 卖家=[{orderInfo.SellerMemo}] 商品=[{orderInfo.ProductInfo}]" : "未命中")}");
                if (Config.EnableOrderInfoAnnounce && orderInfo != null)
                {
                    foreach (AlertSpeechFollowup announcement in BuildOrderInfoSpeechFollowups(
                                 orderInfo,
                                 Config.EnableOrderInfoAnnounce,
                                 Config.AnnounceBuyerMessage,
                                 Config.AnnounceSellerMemo,
                                 Config.AnnounceProductInfo,
                                 Config.AnnounceTotalItemCount))
                    {
                        PublishVoice(
                            announcement.Text,
                            announcement.VoiceStyle,
                            announcement.Sound,
                            repeatCount: 1,
                            interruptCurrent: false);
                    }
                }

                // 在录制停止/启动之后设置缩放状态（InternalStopRecordingAsync 会重置缩放状态）
                _lastScanTime = DateTime.Now;
                _isScanning = true;
                _delayBeforeZooming = Config.ZoomDelaySeconds > 0;
                if (!_delayBeforeZooming)
                {
                    _zoomPhase = ZoomPhase.ZoomingIn;
                    _zoomPhaseStartTime = DateTime.Now;
                    LastZoomRect = System.Windows.Rect.Empty;
                    IsZoomingActive = true;
                }

                // 语音播报完成后再暂停，避免"开始录制"和订单信息被延迟
                if (IsRecording)
                    PauseSpeechForRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleScan] 严重异常: {ex.Message}");
                RuntimeLog.Error("Scan", $"Recording switch failed order={upperResult}", ex);
            }
            finally
            {
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
                _recorderLock.Release();
            }
        }

        private async Task CompleteSameCodePostRollAsync(CancellationTokenSource owner)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Config.SameCodePostRecordSeconds, 0, 5)), owner.Token);
                if (_isDisposed) return;
                await _recorderLock.WaitAsync(owner.Token);
                try
                {
                    // 新单号切换会取消本次收尾；即使收尾任务已先拿到串行锁，
                    // 也不得再停止随后启动的新录像。
                    if (!owner.IsCancellationRequested
                        && ReferenceEquals(_sameCodePostRollCts, owner)
                        && IsRecording)
                    {
                        await InternalStopRecordingAsync();
                        QueuePostStopMux("同码停录");
                        CurrentOrderId = "";
                        ScanInputText = "";
                    }
                }
                finally { _recorderLock.Release(); }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_sameCodePostRollCts, owner))
                    _sameCodePostRollCts = null;
                owner.Dispose();
            }
        }

        private void LogBarcodeRecordingComparison(string scanResult, bool fromCamera, bool dryRun)
        {
            BarcodeRecordingDecision decision = EvaluateBarcodeRecordingDecision(scanResult, fromCamera);
            LogBarcodeRecordingComparison(decision, fromCamera, dryRun);
        }

        private void LogBarcodeRecordingComparison(
            BarcodeRecordingDecision decision,
            bool fromCamera,
            bool dryRun)
        {
            string source = fromCamera ? "摄像头" : "扫码枪";
            string execution = dryRun ? "仅判定不执行" : "进入真实流程";
            RuntimeLog.Info(
                "CameraBarcodeCompare",
                $"来源={source}, 单号={decision.NormalizedValue}, 判定={GetBarcodeDecisionText(decision.Action)}, 原因={BarcodeRecordingDecisionPolicy.GetReasonText(decision.Reason)}, 执行={execution}, 当前录制={IsRecording}, 当前单号={_recordingOrderId}, 同码停录={Config.EnableSameBarcodeStopRecording}, 冷却中={_isInputOnCooldown}");
        }

        private BarcodeRecordingDecision EvaluateBarcodeRecordingDecision(string scanResult, bool fromCamera) =>
            BarcodeRecordingDecisionPolicy.Evaluate(
                scanResult,
                fromCamera,
                canProcess: fromCamera
                    ? CanSubmitCameraBarcode()
                    : !IsBusy && !_isDisposed && !_shutdownRequested,
                IsRecording,
                _recordingOrderId,
                Config.EnableSameBarcodeStopRecording,
                _isInputOnCooldown,
                Config.OrderIdRegex,
                sameCodePostRollPending: _sameCodePostRollCts is { IsCancellationRequested: false });

        private static string GetBarcodeDecisionText(BarcodeRecordingDecisionAction action) => action switch
        {
            BarcodeRecordingDecisionAction.Queue => "等待处理",
            BarcodeRecordingDecisionAction.Start => "开始录制",
            BarcodeRecordingDecisionAction.Stop => "停止录制",
            BarcodeRecordingDecisionAction.Switch => "切换录制",
            BarcodeRecordingDecisionAction.ClearInput => "清除输入",
            BarcodeRecordingDecisionAction.SwitchToShipping => "切换发货模式",
            BarcodeRecordingDecisionAction.SwitchToReturn => "切换退货模式",
            BarcodeRecordingDecisionAction.ToggleRecording => "切换录制状态",
            _ => "忽略"
        };

        private async void StartInputCooldown()
        {
            if (_isInputOnCooldown) return;
            _isInputOnCooldown = true;
            double cooldownMs = Config.BarcodeCooldownSeconds * 1000;
            HideBarcode1Temporarily();
            HideBarcode2Temporarily();
            await Task.Delay((int)cooldownMs);
            _isInputOnCooldown = false;
            string pending = _pendingScanDuringCooldown;
            _pendingScanDuringCooldown = "";
            if (!string.IsNullOrWhiteSpace(pending) && !_isDisposed)
            {
                RuntimeLog.Info("Scan", $"Processing queued scan after cooldown: {pending}");
                HandleScan(pending);
            }
        }

        private bool IsOrderScan(string upperResult)
        {
            return CameraBarcodeCandidatePolicy.IsValidForWorkScan(upperResult, Config.OrderIdRegex);
        }

        internal static bool ShouldAlertPrintedRefund(string mode, bool alertEnabled, OrderInfo orderInfo)
        {
            if (!alertEnabled ||
                (mode != "发货" && mode != "退货") ||
                orderInfo?.IsPrintedRefund != true)
                return false;

            string[] statuses = ParseRefundStatuses(orderInfo.RefundStatus);
            return statuses.Length == 0 || statuses.Any(status => status != "NO_REFUND");
        }

        internal static string GetRefundStatusDisplayText(OrderInfo orderInfo)
        {
            string[] statuses = ParseRefundStatuses(orderInfo?.RefundStatus);
            if (statuses.Length == 0)
                return "存在打印后退款，请人工核对";

            var descriptions = statuses
                .Where(status => status != "NO_REFUND")
                .Select(status => status switch
                {
                    "WAIT_SELLER_AGREE" => DefaultSpeechCatalog.RefundWaitingSeller,
                    "WAIT_BUYER_RETURN_GOODS" => DefaultSpeechCatalog.RefundWaitingBuyerReturn,
                    "WAIT_SELLER_CONFIRM_GOODS" => DefaultSpeechCatalog.RefundWaitingSellerConfirm,
                    "SUCCESS" => DefaultSpeechCatalog.RefundCompleted,
                    "CLOSED" => DefaultSpeechCatalog.RefundClosed,
                    _ => $"退款状态未知（{status}），请人工核对"
                })
                .Distinct()
                .ToList();

            return descriptions.Count == 0 ? "无退款" : string.Join("，", descriptions);
        }

        private static string[] ParseRefundStatuses(string refundStatus)
        {
            return (refundStatus ?? "")
                .Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(status => status.ToUpperInvariant())
                .Distinct()
                .ToArray();
        }

        internal static TimeSpan GetPrintedRefundLookupDelay(DateTime lastRequestUtc, DateTime nowUtc)
        {
            if (lastRequestUtc == DateTime.MinValue)
                return TimeSpan.Zero;

            TimeSpan remaining = PrintedRefundLookupInterval - (nowUtc - lastRequestUtc);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        public bool CanSwitchWorkstation => !IsRecording && !_purposeSwitchPending;
        public string SwitchWorkstationButtonText
        {
            get => _switchWorkstationButtonText;
            private set => SetProperty(ref _switchWorkstationButtonText, value);
        }

        internal static OrderInfo ResolvePrintedRefundOrderForAlert(
            OrderLookupResult lookupResult,
            string trackingNumber,
            OrderInfo cachedOrder)
        {
            if (lookupResult?.Responded != true)
                return cachedOrder;

            return lookupResult.Orders?.FirstOrDefault(order =>
                string.Equals(
                    order?.TrackingNumber?.Trim(),
                    trackingNumber?.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        private void QueuePrintedRefundCheck(string trackingNumber, string mode)
        {
            if (!Config.EnablePrintedRefundAlert || string.IsNullOrWhiteSpace(trackingNumber))
                return;

            var check = new PrintedRefundScanCheck
            {
                TrackingNumber = trackingNumber.Trim().ToUpperInvariant(),
                Mode = mode
            };

            lock (_printedRefundLookupLock)
            {
                _pendingPrintedRefundChecks.Add(check);
                if (_printedRefundLookupTask == null || _printedRefundLookupTask.IsCompleted)
                    _printedRefundLookupTask = Task.Run(RunPrintedRefundLookupLoopAsync);
            }
        }

        private async Task RunPrintedRefundLookupLoopAsync()
        {
            while (true)
            {
                TimeSpan delay;
                lock (_printedRefundLookupLock)
                {
                    if (_isDisposed || _pendingPrintedRefundChecks.Count == 0)
                    {
                        _printedRefundLookupTask = null;
                        return;
                    }
                    delay = GetPrintedRefundLookupDelay(_lastPrintedRefundLookupUtc, DateTime.UtcNow);
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay);

                WebServer server = _webServer;
                OrderLookupResult result = new() { Responded = false };
                Dictionary<string, OrderInfo> cachedOrders = new(StringComparer.OrdinalIgnoreCase);
                lock (_printedRefundLookupLock)
                    _lastPrintedRefundLookupUtc = DateTime.UtcNow;

                try
                {
                    if (server != null)
                    {
                        string[] trackingNumbers;
                        lock (_printedRefundLookupLock)
                        {
                            trackingNumbers = _pendingPrintedRefundChecks
                                .Select(x => x.TrackingNumber)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                        }
                        foreach (string trackingNumber in trackingNumbers)
                            cachedOrders[trackingNumber] = server.GetOrderInfo(trackingNumber);
                        result = await server.RequestFreshOrderSnapshotAsync(PrintedRefundLookupTimeout, trackingNumbers);
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("Scan", "Printed-refund snapshot request failed", ex);
                }

                List<PrintedRefundScanCheck> checks;
                lock (_printedRefundLookupLock)
                {
                    checks = _pendingPrintedRefundChecks.ToList();
                    _pendingPrintedRefundChecks.Clear();
                }

                foreach (PrintedRefundScanCheck check in checks)
                {
                    cachedOrders.TryGetValue(check.TrackingNumber, out OrderInfo cachedOrder);
                    OrderInfo orderInfo = ResolvePrintedRefundOrderForAlert(result, check.TrackingNumber, cachedOrder);
                    CheckPrintedRefundAndAlert(check, orderInfo, result.Responded ? "最新订单查询" : "请求失败后的最近缓存");
                }

                RuntimeLog.Info(
                    "Scan",
                    $"Printed-refund snapshot checked: responded={result.Responded}, returned={result.Orders.Count}, scans={checks.Count}");
            }
        }

        private void CheckPrintedRefundAndAlert(PrintedRefundScanCheck check, OrderInfo orderInfo, string source)
        {
            if (!ShouldAlertPrintedRefund(check.Mode, Config.EnablePrintedRefundAlert, orderInfo) || !check.TryMarkAlerted())
                return;

            RuntimeLog.Warn(
                "Scan",
                $"Printed-refund order detected: tracking={check.TrackingNumber}, order={orderInfo.OrderId}, status={orderInfo.RefundStatus}, source={source}");
            string statusText = GetRefundStatusDisplayText(orderInfo);
            if (_isDisposed)
                return;

            _alertService?.Publish(new AlertRequest
            {
                Message = $"警告：快递单 {check.TrackingNumber}，{statusText}",
                SpeechText = DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(statusText),
                Priority = AlertPriority.Critical,
                Sound = AlertSound.IndustrialAlarm,
                SoundRepeatCount = 1,
                SpeechRepeatCount = 1,
                DisplayDuration = TimeSpan.FromSeconds(12),
                DeduplicationKey = $"printed-refund:{check.TrackingNumber}:{check.AlertId}",
                DeduplicationWindow = TimeSpan.FromMinutes(1),
                FollowupSpeech = BuildOrderInfoSpeechFollowups(
                    orderInfo,
                    Config.EnableOrderInfoAnnounce,
                    Config.AnnounceBuyerMessage,
                    Config.AnnounceSellerMemo,
                    Config.AnnounceProductInfo,
                    Config.AnnounceTotalItemCount)
            });
        }

        public bool IsAutoSubmitScanCandidate(string scanText)
        {
            return IsOrderScan((scanText ?? "").ToUpper().Trim())
                || CameraBarcodeCandidatePolicy.IsKnownCommandCode(scanText);
        }

        private void QueuePostStopMux(string reason)
        {
            if (_isDisposed)
                return;

            Task previousMuxTask = _postStopMuxTask ?? Task.CompletedTask;
            Task finalizeTask = _lastFinalizeTask ?? Task.CompletedTask;
            _postStopMuxTask = Task.Run(async () =>
            {
                try
                {
                    await previousMuxTask.ConfigureAwait(false);
                    await finalizeTask.ConfigureAwait(false);
                    if (_isDisposed)
                        return;

                    RuntimeLog.Info("MkvToMp4", $"最终停止后开始合成 MP4：reason={reason}");
                    var result = await BatchConvertMkvToMp4Async(
                        new Progress<string>(msg => Debug.WriteLine($"[PostStopMux] {msg}")),
                        CancellationToken.None).ConfigureAwait(false);
                    _recordingTransferService?.EnqueueCompletedRecordings();

                    RuntimeLog.Info(
                        "MkvToMp4",
                        $"最终停止后合成完成：reason={reason}, success={result.SuccessCount}, fail={result.FailureCount}, skip={result.SkippedCount}, deferred={result.DeferredCount}, suppressed={result.SuppressedCount}");
                    VerifyCompletedRecordingSpecifications(result);
                    ShowMkvFailureToastIfNeeded(result);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MkvToMp4", $"最终停止后合成异常：reason={reason}", ex);
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("录像未生成兼容 MP4，原始录像已保留", ToastSeverity.Warning);
                    });
                }
            });
        }

        private async Task SafeStopRecordingAsync(bool isManual = false, bool mergeAfterStop = true)
        {
            if (IsBusy || !IsRecording || _isDisposed) return;
            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                PauseSpeechForRecording();
                await InternalStopRecordingAsync();
                if (mergeAfterStop)
                    QueuePostStopMux(isManual ? "手动停止" : "最终停止");
                if (isManual)
                {
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
                }
            }
            finally
            {
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
                _recorderLock.Release();
            }
        }
        // =======================================================================

        // 录制、编码、磁盘清理逻辑已移动到 MainViewModel.Recording.cs / MainViewModel.Encoder.cs / MainViewModel.Cleanup.cs

        public void ShowToast(string message, ToastSeverity severity = ToastSeverity.Success)
        {
            message = AppLanguage.Translate(message);
            if (_alertService != null)
            {
                _alertService.Publish(new AlertRequest
                {
                    Message = message,
                    Severity = severity,
                    Priority = AlertPriority.Normal,
                    Sound = AlertSound.None,
                    DisplayDuration = GetToastDisplayDuration(severity)
                });
                return;
            }

            PresentToast(message, GetToastDisplayDuration(severity), severity);
        }

        private static TimeSpan GetToastDisplayDuration(ToastSeverity severity) =>
            severity is ToastSeverity.Warning or ToastSeverity.Error
                ? TimeSpan.FromSeconds(4)
                : TimeSpan.FromMilliseconds(2500);

        private void PresentAlert(AlertRequest request)
        {
            if (ShouldShowPreviewAlert(request))
                PresentPreviewAlert(request);
            else
                PresentToast(request.Message, request.DisplayDuration, request.Severity);
        }

        internal static bool ShouldShowPreviewAlert(AlertRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Message))
                return false;
            if (request.Priority == AlertPriority.Critical || request.Sound is AlertSound.Warning or AlertSound.IndustrialAlarm)
                return true;

            string message = request.Message;
            string[] exceptionTerms =
            [
                "警告", "异常", "失败", "错误", "断开", "丢失", "超时", "拦截", "退款", "不一致", "无法", "过短", "太小",
                "warning", "error", "failed", "failure", "exception", "disconnected", "timeout", "invalid", "refund"
            ];
            return exceptionTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        internal static string BuildPreviewOrderNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            string remarks = BuildPreviewOrderRemarkNotice(orderInfo);
            string details = BuildPreviewOrderDetailNotice(orderInfo);
            return string.Join(
                Environment.NewLine,
                new[] { remarks, details }.Where(value => value.Length > 0));
        }

        internal static string BuildPreviewOrderRemarkNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            var lines = new List<string>();
            AddPreviewOrderLine(lines, "Main.PreviewBuyerMessage", orderInfo.BuyerMessage);
            AddPreviewOrderLine(lines, "Main.PreviewSellerMemo", orderInfo.SellerMemo);
            return string.Join(Environment.NewLine, lines);
        }

        internal static string BuildPreviewOrderDetailNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            var lines = new List<string>();
            AddPreviewOrderLine(lines, "Main.PreviewProduct", orderInfo.ProductInfo);

            if (orderInfo.HasRefund || orderInfo.IsPrintedRefund)
            {
                string status = GetRefundStatusDisplayText(orderInfo);
                if (!string.Equals(status, "无退款", StringComparison.Ordinal))
                    lines.Add(AppLanguage.Format("Main.PreviewException", CompactPreviewText(status)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        internal static string BuildPreviewOrderItemCountText(OrderInfo orderInfo)
        {
            return orderInfo?.TotalItemCount > 1
                ? orderInfo.TotalItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }

        internal static IReadOnlyList<AlertSpeechFollowup> BuildOrderInfoSpeechFollowups(
            OrderInfo orderInfo,
            bool announcementsEnabled,
            bool announceBuyerMessage,
            bool announceSellerMemo,
            bool announceProductInfo,
            bool announceTotalItemCount = true)
        {
            if (!announcementsEnabled || orderInfo == null)
                return Array.Empty<AlertSpeechFollowup>();

            var announcements = new List<AlertSpeechFollowup>();
            if (announceTotalItemCount
                && !orderInfo.HasRefund
                && !orderInfo.IsPrintedRefund
                && orderInfo.TotalItemCount > 0)
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateOrderTotalCountAnnouncement(orderInfo.TotalItemCount),
                    Sound = AlertSound.None
                });
            }
            if (announceBuyerMessage && !string.IsNullOrWhiteSpace(orderInfo.BuyerMessage))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(orderInfo.BuyerMessage),
                    Sound = AlertSound.Remark
                });
            }
            if (announceSellerMemo && !string.IsNullOrWhiteSpace(orderInfo.SellerMemo))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateSellerMemoAnnouncement(orderInfo.SellerMemo),
                    Sound = AlertSound.Remark
                });
            }
            if (announceProductInfo && !string.IsNullOrWhiteSpace(orderInfo.ProductInfo))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateProductAnnouncement(orderInfo.ProductInfo),
                    Sound = AlertSound.None
                });
            }
            return announcements;
        }

        private static void AddPreviewOrderLine(List<string> lines, string resourceKey, string value)
        {
            string compact = CompactPreviewText(value);
            if (compact.Length > 0)
                lines.Add(AppLanguage.Format(resourceKey, compact));
        }

        private static string CompactPreviewText(string value)
        {
            string compact = string.Join(" ", (value ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            const int maxLength = 160;
            return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
        }

        private void SetPreviewOrderNotice(OrderInfo orderInfo)
        {
            PreviewOrderRemarkText = BuildPreviewOrderRemarkNotice(orderInfo);
            PreviewOrderDetailText = BuildPreviewOrderDetailNotice(orderInfo);
            PreviewOrderItemCountText = BuildPreviewOrderItemCountText(orderInfo);
            IsPreviewOrderNoticeVisible = PreviewOrderRemarkText.Length > 0 || PreviewOrderDetailText.Length > 0;
        }

        private void ClearPreviewOrderNotice() => SetPreviewOrderNotice(null);

        private void PresentPreviewAlert(AlertRequest request)
        {
            Application.Current?.Dispatcher?.InvokeAsync(async () =>
            {
                _previewAlertCts?.Cancel();
                _previewAlertCts = new CancellationTokenSource();
                var token = _previewAlertCts.Token;
                PreviewAlertText = request.Message;
                IsPreviewAlertCritical = request.Priority == AlertPriority.Critical || request.Sound == AlertSound.IndustrialAlarm;
                IsPreviewAlertVisible = true;
                TimeSpan duration = request.DisplayDuration < TimeSpan.FromSeconds(5)
                    ? TimeSpan.FromSeconds(5)
                    : request.DisplayDuration;
                try { await Task.Delay(duration, token); }
                catch (OperationCanceledException) { return; }
                IsPreviewAlertVisible = false;
            });
        }

        private void PresentToast(
            string message,
            TimeSpan displayDuration,
            ToastSeverity severity = ToastSeverity.Success)
        {
            Application.Current?.Dispatcher?.InvokeAsync(async () =>
            {
                _toastCts?.Cancel();
                _toastCts = new CancellationTokenSource();
                var token = _toastCts.Token;
                ToastMessage = message;
                ToastSeverity = severity;
                IsToastVisible = true;
                try { await Task.Delay(displayDuration, token); }
                catch (OperationCanceledException) { return; }
                IsToastVisible = false;
            });
        }

        private void PublishScannerAlert(
            string deduplicationKey,
            string message,
            string speechText,
            int repeatCount = 1)
        {
            _alertService?.Publish(new AlertRequest
            {
                Message = message,
                SpeechText = speechText,
                Priority = AlertPriority.Normal,
                Sound = AlertSound.Warning,
                SoundRepeatCount = 1,
                SpeechRepeatCount = repeatCount,
                DisplayDuration = TimeSpan.FromMilliseconds(2500),
                DeduplicationKey = deduplicationKey,
                DeduplicationWindow = TimeSpan.FromSeconds(3)
            });
        }

        private void FilterLogs() { FilteredLogs.Clear(); var keyword = LogSearchText?.ToUpper() ?? ""; foreach (var log in _allLogs) { if (string.IsNullOrEmpty(keyword) || log.OrderId.ToUpper().Contains(keyword)) FilteredLogs.Add(log); } }
        private void AddRecord(ScanRecord record) { Application.Current.Dispatcher.InvokeAsync(() => { _allLogs.Insert(0, record); if (string.IsNullOrEmpty(LogSearchText)) FilteredLogs.Insert(0, record); if (_allLogs.Count > 200) _allLogs.RemoveAt(_allLogs.Count - 1); }); }

        private void LoadConfig() 
        { 
            bool containsRemovedStorageQuota = false;
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string configJson = File.ReadAllText(_configFilePath, System.Text.Encoding.UTF8);
                    containsRemovedStorageQuota = configJson.Contains("\"QuotaGB\"", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Config", $"Failed to inspect config migration markers: {ex.Message}");
                }
            }

            Config = WorkstationConfigStore.Load();
            _currentMode = AppConfig.NormalizeRecordingMode(Config.RecordingMode);

            bool configMigrated = containsRemovedStorageQuota;
            if (Config.VideoCqp <= 0)
            {
                Config.VideoCqp = 25;
                configMigrated = true;
            }
            if (Config.AudioSyncOffsetMs == 400)
            {
                Config.AudioSyncOffsetMs = 0;
                configMigrated = true;
            }
            if (!WorkstationRoles.IsKnown(Config.WorkstationRole))
            {
                Config.WorkstationRole = WorkstationRoles.CameraMonitor;
                configMigrated = true;
            }
            int normalizedAudioSyncOffsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
            if (Config.AudioSyncOffsetMs != normalizedAudioSyncOffsetMs)
            {
                Config.AudioSyncOffsetMs = normalizedAudioSyncOffsetMs;
                configMigrated = true;
            }
            configMigrated = AppConfig.NormalizeAfterLoad(Config) || configMigrated;
            if (configMigrated)
                SaveConfig();

            // Apply Theme
            if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
            {
                ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
            }
            else
            {
                ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(ExpressPackingMonitoring.Themes.AppTheme.Auto);
            }
        }
        private bool SaveConfig(bool notifyUser = false) => SaveConfig(Config, notifyUser);

        private bool SaveConfig(AppConfig config, bool notifyUser = false)
        {
            if (WorkstationConfigStore.TrySave(config, out string error))
                return true;

            if (notifyUser)
                ShowToast($"配置保存失败，请检查磁盘空间或权限: {error}", ToastSeverity.Error);
            return false;
        }

        public void OpenSettings() => OpenSettings(selectRecordingCache: false);

        private void OpenSettings(bool selectRecordingCache)
        {
            if (_isEncoderDetectRunning)
            {
                ShowToast("处理中：编码器环境检测中，请稍后打开设置...", ToastSeverity.Information);
                return;
            }
            try
            {
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var settingsWin = new SettingsWindow(this, clonedConfig, DiskUsagePercent, DiskUsageText, IsRecording);
                if (selectRecordingCache)
                    settingsWin.SelectRecordingCacheTab();
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                if (mainWindow != null) settingsWin.Owner = mainWindow;
                mainWindow?.SuspendCapsLockForModalWindow();
                try
                {
                    settingsWin.ShowDialog();
                }
                finally
                {
                    mainWindow?.ResumeCapsLockAfterModalWindow();
                }
            }
            catch (Exception ex) { ShowToast($"设置错误: {ex.Message}", ToastSeverity.Error); }
        }

        internal enum CameraRestartAction
        {
            None,
            RestartNow,
            DeferUntilRecordingEnds
        }

        internal static CameraRestartAction GetCameraRestartAction(
            AppConfig current,
            AppConfig next,
            bool isRecording)
        {
            if (!AppConfig.RequiresCameraRestart(current, next))
                return CameraRestartAction.None;

            return isRecording
                ? CameraRestartAction.DeferUntilRecordingEnds
                : CameraRestartAction.RestartNow;
        }

        public async Task<bool> ApplySettingsAsync(AppConfig nextConfig)
        {
            try
            {
                    AppConfig.NormalizeAfterLoad(nextConfig);
                    CameraRestartAction cameraRestartAction = GetCameraRestartAction(
                        Config,
                        nextConfig,
                        IsRecording);
                    bool themeChanged = Config.Theme != nextConfig.Theme;
                    bool globalKeyChanged = Config.EnableGlobalKeyboard != nextConfig.EnableGlobalKeyboard;
                    bool cameraBarcodeChanged = Config.EnableCameraBarcodeRecognition != nextConfig.EnableCameraBarcodeRecognition;
                    bool workstationChanged = !string.Equals(
                        Config.DeploymentPreset,
                        nextConfig.DeploymentPreset,
                        StringComparison.OrdinalIgnoreCase);
                    bool aiTtsChanged = Config.EnableAiTts != nextConfig.EnableAiTts
                        || Config.AiTtsEngine != nextConfig.AiTtsEngine;
                    bool webServerChanged = Config.EnableWebServer != nextConfig.EnableWebServer
                        || Config.WebServerPort != nextConfig.WebServerPort
                        || Config.TranscodeCacheMaxMB != nextConfig.TranscodeCacheMaxMB
                        || Config.EnableOrderInfoLog != nextConfig.EnableOrderInfoLog
                        || Config.EnableExtensionApi != nextConfig.EnableExtensionApi
                        || Config.RequireWebAccessKey != nextConfig.RequireWebAccessKey
                        || !string.Equals(Config.WebAccessKey, nextConfig.WebAccessKey, StringComparison.Ordinal)
                        || (string.Equals(
                                DeploymentPresets.Normalize(nextConfig.DeploymentPreset),
                                DeploymentPresets.RecordingHost,
                                StringComparison.Ordinal)
                            && !string.Equals(Config.NodeName, nextConfig.NodeName, StringComparison.Ordinal));
                    bool computerNicknameChanged =
                        !string.Equals(Config.NodeName, nextConfig.NodeName, StringComparison.Ordinal)
                        || Config.NodeNameCustomized != nextConfig.NodeNameCustomized;
                    bool webServerNeedsRecovery = nextConfig.EnableWebServer && _webServer == null;

                    if (workstationChanged)
                        return await RunPurposeSwitchAsync(nextConfig);

                    if (!SaveConfig(nextConfig, notifyUser: true))
                        return false;
                    // 必须先切换到 nextConfig，录制结束后的 RestartCamera 才会读取新的网络摄像头地址/协议。
                    Config = nextConfig;
                    RefreshArchiveBackupSummary();
                    if (computerNicknameChanged && IsRecordingWorkstation)
                        QueueRecordingWorkstationHeartbeat(force: true);
                    if (cameraBarcodeChanged)
                        ResetCameraBarcodeRecognition();

                    // 同步语音服务配置
                    if (_speechService != null)
                    {
                        _speechService.EnableSoundPrompt = Config.EnableSoundPrompt;
                        _speechService.MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech;
                        _speechService.EnableAiTts = Config.EnableAiTts;
                        _speechService.AiTtsEngine = Config.AiTtsEngine;
                        _speechService.AiTtsSpeakerId = Config.AiTtsSpeakerId;
                        _speechService.AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId;
                        _speechService.AiTtsSpeed = Config.AiTtsSpeed;
                        _speechService.EdgeTtsVoice = Config.EdgeTtsVoice;
                        _speechService.EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice;
                        _speechService.UpdateBreakWords(Config.TtsBreakWords);
                        if (aiTtsChanged && Config.EnableAiTts && !_speechService.IsAiTtsAvailable)
                            _speechService.InitAiTts();
                    }

                    if (themeChanged)
                    {
                        if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
                        {
                            ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                        }
                    }
                    ForceCheckDiskAndCleanup();
                    RunRecordingCacheCleanup();

                    ApplyGlobalKeyboardConfig();
                    if (globalKeyChanged && _globalKeyHook != null)
                    {
                        if (Config.EnableGlobalKeyboard)
                            _globalKeyHook.Start();
                        else
                            _globalKeyHook.Stop();
                    }
                    bool webServerApplied = true;
                    bool webServerShouldApply = (webServerChanged || webServerNeedsRecovery) && !workstationChanged;
                    if (webServerShouldApply)
                    {
                        ShowToast("正在应用局域网服务设置...", ToastSeverity.Information);
                        webServerApplied = await RestartWebServerAsync(allowAccessSetup: Config.EnableWebServer);
                    }
                    else if (!webServerChanged && !webServerNeedsRecovery)
                    {
                        _ = RefreshWorkstationStatusAsync();
                    }

                    if (cameraRestartAction != CameraRestartAction.None)
                    {
                        if (cameraRestartAction == CameraRestartAction.DeferUntilRecordingEnds)
                        {
                            ShowToast("配置已保存，摄像头配置将在录制结束后生效", ToastSeverity.Information);
                            _pendingCameraRestart = true;
                        }
                        else
                        {
                            ShowToast("配置已保存，重启相机", ToastSeverity.Information);
                            _consecutiveRestartFailures = 0;
                            RestartCamera();
                        }
                    }
                    else
                    {
                        if (!webServerShouldApply || webServerApplied)
                            ShowToast(webServerShouldApply ? "配置已保存，局域网服务已应用" : "提示：配置已保存");
                    }
                    return true;
            }
            catch (Exception ex)
            {
                ShowToast($"设置错误: {ex.Message}", ToastSeverity.Error);
                return false;
            }
        }

        public async void RunStartupSetupFlowsIfNeeded(System.Windows.Window owner)
        {
            bool isExistingUser = Config.FirstUseWizardCompleted;
            if (!isExistingUser)
            {
                await RunFirstUseSetupWizardIfNeededAsync(owner);
                if (_isDisposed || !Config.FirstUseWizardCompleted)
                    return;
                await RunRecordingWorkstationHostBindingPromptIfNeededAsync(owner);
                return;
            }

            await RunRecordingWorkstationHostBindingPromptIfNeededAsync(owner);
            if (_isDisposed)
                return;
            RunCameraBarcodeUpgradePromptIfNeeded(owner);
            await RunMobileConnectionSetupPromptIfNeededAsync(owner);
        }

        private async Task RunFirstUseSetupWizardIfNeededAsync(System.Windows.Window owner)
        {
            if (Config.FirstUseWizardCompleted || _isDisposed)
                return;

            bool pausedCamera = false;
            try
            {
                _isSetupWizardActive = true;
                if (!IsRecording)
                {
                    pausedCamera = StopCamera();
                    if (!pausedCamera)
                    {
                        ShowToast("摄像头未能停止，暂时无法打开配置向导", ToastSeverity.Warning);
                        return;
                    }
                }

                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var wizard = new FirstUseSetupWizardWindow(clonedConfig, allowSkip: false) { Owner = owner };
                MainWindow setupOwner = owner as MainWindow;
                setupOwner?.SuspendCapsLockForModalWindow();

                bool accepted;
                try
                {
                    accepted = wizard.ShowDialog() == true;
                }
                finally
                {
                    setupOwner?.ResumeCapsLockAfterModalWindow();
                }

                if (!accepted)
                    return;

                AppConfig nextConfig = wizard.WasSkipped ? clonedConfig : wizard.ResultConfig;
                AppConfig.ApplyFirstUseDefaults(nextConfig);
                AppConfig.NormalizeAfterLoad(nextConfig);
                if (!SaveConfig(nextConfig, notifyUser: true))
                    return;
                Config = nextConfig;
                ResetCameraBarcodeRecognition();
                ApplyGlobalKeyboardConfig();
                if (_globalKeyHook != null)
                {
                    if (Config.EnableGlobalKeyboard)
                        _globalKeyHook.Start();
                    else
                        _globalKeyHook.Stop();
                }

                if (Config.EnableWebServer)
                {
                    ShowToast("正在应用局域网服务设置...", ToastSeverity.Information);
                    bool webServerReady = await RestartWebServerAsync(allowAccessSetup: true);
                    _webServerStartupTask = Task.FromResult(webServerReady);
                    if (!webServerReady)
                        return;
                }

                ShowToast(
                    wizard.WasSkipped ? "已跳过配置向导" : "配置向导已完成",
                    wizard.WasSkipped ? ToastSeverity.Information : ToastSeverity.Success);
                ShowMobileConnectionSetupPromptIfReady(owner);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("SetupWizard", "First-use setup wizard failed", ex);
                ShowToast($"配置向导错误: {ex.Message}", ToastSeverity.Error);
            }
            finally
            {
                _isSetupWizardActive = false;
                if (pausedCamera && !IsRecording && !_isDisposed)
                {
                    _consecutiveRestartFailures = 0;
                    RestartCamera();
                }
            }
        }

        private void RunCameraBarcodeUpgradePromptIfNeeded(System.Windows.Window owner)
        {
            if (_isDisposed || !AppConfig.ShouldPromptCameraBarcodeUpgrade(Config))
                return;

            var dialog = new CameraBarcodeUpgradeDialog { Owner = owner };
            bool enableRecognition = dialog.ShowDialog() == true;
            var nextConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            AppConfig.ApplyCameraBarcodeUpgradeChoice(nextConfig, enableRecognition);
            if (!SaveConfig(nextConfig, notifyUser: true))
                return;

            Config = nextConfig;
            ResetCameraBarcodeRecognition();
            RuntimeLog.Info("CameraBarcode", $"Upgrade choice saved enabled={Config.EnableCameraBarcodeRecognition}");
            ShowToast(enableRecognition
                ? "已启用摄像头识别面单"
                : "已保留当前设置，可随时在设置中开启");
        }

        private async Task RunMobileConnectionSetupPromptIfNeededAsync(System.Windows.Window owner)
        {
            if (_isDisposed || !AppConfig.ShouldPromptMobileConnection(Config))
                return;

            Task<bool> startupTask = _webServerStartupTask;
            if (startupTask != null)
            {
                bool webServerReady;
                try
                {
                    webServerReady = await startupTask;
                }
                catch
                {
                    return;
                }

                if (!webServerReady)
                    return;
            }

            ShowMobileConnectionSetupPromptIfReady(owner);
        }

        private void ShowMobileConnectionSetupPromptIfReady(System.Windows.Window owner)
        {
            if (!AppConfig.ShouldPromptMobileConnection(Config)
                || !TryGetMobileConnectionUrl(out string url))
            {
                return;
            }

            ShowMobileConnectionWindow(owner, url);

            var nextConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            AppConfig.MarkMobileConnectionSetupCompleted(nextConfig);
            if (!SaveConfig(nextConfig, notifyUser: true))
                return;

            Config = nextConfig;
            RuntimeLog.Info("MobileConnection", "Mobile connection setup prompt completed");
        }

        public bool SuspendCameraForSetupWizard()
        {
            if (IsRecording || _isDisposed)
                return false;

            _isSetupWizardActive = true;
            if (StopCamera())
                return true;

            _isSetupWizardActive = false;
            ShowToast("摄像头未能停止，暂时无法打开配置向导", ToastSeverity.Warning);
            return false;
        }

        public void ResumeCameraAfterSetupWizard()
        {
            _isSetupWizardActive = false;
            if (IsRecording || _isDisposed)
                return;

            _consecutiveRestartFailures = 0;
            RestartCamera();
        }

        private void OpenStatsWindow()
        {
            if (ActivateExistingWindow(_statisticsWindow))
                return;

            var statsWindow = new StatisticsWindow(_db);
            _statisticsWindow = statsWindow;
            statsWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_statisticsWindow, statsWindow))
                    _statisticsWindow = null;
            };
            statsWindow.Show();
        }

        private void OpenPlaybackWindow()
        {
            if (ActivateExistingWindow(_playbackWindow))
                return;

            string folderPath;
            try
            {
                folderPath = ResolveBestStoragePath();
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            }
            catch (Exception ex)
            {
                AppDialog.Error(null, $"无法访问存储路径：{ex.Message}", "存储错误");
                return;
            }

            try
            {
                VideoFolderImportService importService = CreateVideoImportService(folderPath);
                var playbackWindow = new PlaybackWindow(
                    folderPath,
                    _db,
                    Config.ShowDeletedVideos,
                    importService,
                    Config.LastVideoImportFolder,
                    saveImportFolder: path =>
                    {
                        Config.LastVideoImportFolder = path;
                        SaveConfig();
                    },
                    videosImported: () =>
                    {
                        _recordingTransferService?.EnqueueCompletedRecordings();
                        RefreshRecordingTransferSummary();
                        RunRecordingCacheCleanup();
                    },
                    localComputerName: Config.NodeName);
                _playbackWindow = playbackWindow;
                playbackWindow.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_playbackWindow, playbackWindow))
                        _playbackWindow = null;
                };
                playbackWindow.Show();
            }
            catch (Exception ex)
            {
                _playbackWindow = null;
                AppDialog.Error(null, $"打开回放窗口失败：{ex.Message}", "回放错误");
            }
        }

        private static bool ActivateExistingWindow(System.Windows.Window window)
        {
            if (window == null)
                return false;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            return true;
        }

        /// <summary>
        /// 批量将数据库中的旧 MKV 文件转换为 MP4（无损容器转换）
        /// </summary>
        public async Task<MkvBatchConversionResult> BatchConvertMkvToMp4Async(
            IProgress<string> progress,
            CancellationToken token,
            bool forceRetry = false)
        {
            await _mkvBatchLock.WaitAsync(token);
            try
            {
                return await BatchConvertMkvToMp4CoreAsync(progress, token, forceRetry);
            }
            finally
            {
                _mkvBatchLock.Release();
            }
        }

        private async Task<MkvBatchConversionResult> BatchConvertMkvToMp4CoreAsync(
            IProgress<string> progress,
            CancellationToken token,
            bool forceRetry)
        {
            var batchResult = new MkvBatchConversionResult();
            if (_db == null) return batchResult;

            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                progress?.Report("未找到 FFmpeg，无法执行转换");
                return batchResult;
            }

            var mkvPaths = GetMkvConversionTargets();
            int total = mkvPaths.Count;
            var failedPaths = new List<string>();

            for (int i = 0; i < total; i++)
            {
                if (token.IsCancellationRequested) break;

                string mkvPath = mkvPaths[i];
                batchResult.MarkProcessedSource(mkvPath);
                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                string fileName = Path.GetFileName(mkvPath);
                MkvConversionFailureState failureState = _db.GetMkvConversionFailureState(mkvPath);

                // 如果 MKV 已不存在但 MP4 存在，只更新数据库
                if (!File.Exists(mkvPath))
                {
                    if (File.Exists(mp4Path)
                        && ValidateConvertedMp4(ffmpegPath, mp4Path, requireAudio: false, audioLogPath: null, cancellationToken: token))
                    {
                        DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
                        _db.UpdateVideoFilePath(mkvPath, mp4Path);
                        _archiveService?.Wake();
                        batchResult.SuccessCount++;
                        batchResult.AddFinalFile(mkvPath, mp4Path);
                        progress?.Report($"[{i + 1}/{total}] 已更新数据库: {fileName}");
                    }
                    else
                    {
                        batchResult.SkippedCount++;
                        progress?.Report($"[{i + 1}/{total}] 文件不存在，跳过: {fileName}");
                    }
                    continue;
                }

                // 如果 MP4 已存在，直接删 MKV 并更新数据库；但带 WAV/audio.log 的 MKV 需要重新合并，避免误用录制中生成的半截 MP4。
                if (File.Exists(mp4Path)
                    && ValidateConvertedMp4(ffmpegPath, mp4Path, requireAudio: false, audioLogPath: null, cancellationToken: token))
                {
                    if (HasMuxRecoverySidecar(mkvPath))
                    {
                        RuntimeLog.Warn("MkvRecover", $"Existing MP4 ignored because MKV sidecar remains file={fileName}");
                        progress?.Report($"[{i + 1}/{total}] 发现疑似半截 MP4，重新合并: {fileName}");
                    }
                    else
                    {
                        try { File.Delete(mkvPath); } catch { }
                        DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
                        _db.UpdateVideoFilePath(mkvPath, mp4Path);
                        _archiveService?.Wake();
                        batchResult.SuccessCount++;
                        batchResult.AddFinalFile(mkvPath, mp4Path);
                        progress?.Report($"[{i + 1}/{total}] MP4 已存在，已清理 MKV: {fileName}");
                        continue;
                    }
                }

                // 历史失败记录在完成文件对账后直接进入终态，不再后台重复转换。
                MkvAutomaticRetryDecision retryDecision =
                    MkvConversionRetryPolicy.GetAutomaticRetryDecision(failureState, DateTime.Now);
                if (!forceRetry && retryDecision == MkvAutomaticRetryDecision.Suppressed)
                {
                    batchResult.SuppressedCount++;
                    if (File.Exists(mkvPath))
                        batchResult.AddFinalFile(mkvPath, mkvPath);
                    _db.MarkArchivePendingByFilePath(mkvPath);
                    _archiveService?.Wake();
                    progress?.Report($"[{i + 1}/{total}] 未生成兼容 MP4，已保留原始录像: {fileName}");
                    continue;
                }

                progress?.Report($"[{i + 1}/{total}] 正在转换: {fileName}");

                MkvConversionResult conversionResult = await Task.Run(() =>
                {
                    var result = ConvertMkvToMp4ForPlayback(mkvPath, token);
                    if (!result.Success)
                        RuntimeLog.Warn("MkvRecover", $"Convert failed file={fileName}, error={result.ErrorMessage}");
                    return result;
                }, token);

                if (conversionResult.Success)
                {
                    try { File.Delete(mkvPath); } catch { }
                    _db.ClearMkvConversionFailure(mkvPath);
                    _db.UpdateVideoFilePath(mkvPath, mp4Path);
                    _archiveService?.Wake();
                    batchResult.SuccessCount++;
                    batchResult.AddFinalFile(
                        mkvPath,
                        string.IsNullOrWhiteSpace(conversionResult.FilePath)
                            ? mp4Path
                            : conversionResult.FilePath);
                    progress?.Report($"[{i + 1}/{total}] 转换成功: {fileName}");
                }
                else
                {
                    DateTime failedAt = DateTime.Now;
                    _db.RecordMkvConversionFailure(mkvPath, failedAt, conversionResult.ErrorMessage);
                    batchResult.FailureCount++;
                    string retainedPath = !string.IsNullOrWhiteSpace(conversionResult.FilePath)
                        && File.Exists(conversionResult.FilePath)
                            ? conversionResult.FilePath
                            : mkvPath;
                    if (File.Exists(retainedPath))
                        batchResult.AddFinalFile(mkvPath, retainedPath);
                    failedPaths.Add(mkvPath);
                    if (MkvConversionRetryPolicy.GetAutomaticRetryDecision(
                            _db.GetMkvConversionFailureState(mkvPath),
                            failedAt) == MkvAutomaticRetryDecision.Suppressed)
                    {
                        batchResult.SuppressedCount++;
                        _db.MarkArchivePendingByFilePath(mkvPath);
                        _archiveService?.Wake();
                    }
                    progress?.Report($"[{i + 1}/{total}] 转换失败: {fileName}");
                }
            }

            if (!forceRetry && failedPaths.Count > 0)
                batchResult.NotificationCount = _db.ClaimMkvFailureNotifications(failedPaths, DateTime.Now);

            return batchResult;
        }

        private void ShowMkvFailureToastIfNeeded(MkvBatchConversionResult result)
        {
            if (result?.ShouldNotify != true)
                return;

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_isDisposed)
                {
                    ShowToast(
                        $"有 {result.NotificationCount} 个录像未生成兼容 MP4，原始录像已保留，可在维护工具中查看",
                        ToastSeverity.Warning);
                }
            });
        }

        private void VerifyCompletedRecordingSpecifications(MkvBatchConversionResult result)
        {
            if (result == null || result.ProcessedSources.Count == 0)
                return;

            string ffmpegPath = FindFFmpeg();
            foreach (string sourcePath in result.ProcessedSources)
            {
                if (!_pendingRecordingSpecificationChecks.TryRemove(
                        sourcePath,
                        out ExpectedRecordingSpecification expected))
                {
                    continue;
                }

                // 尽力而为：下一单已经开录时直接跳过，不与实时录制竞争资源，也不轮询补做。
                if (IsRecording || _isDisposed)
                    continue;

                MkvFinalizedFile finalizedFile = result.FinalFiles.FirstOrDefault(item =>
                    string.Equals(item.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (finalizedFile == null)
                    continue;

                if (!CompletedVideoSpecificationProbe.TryRead(
                        ffmpegPath,
                        finalizedFile.FinalPath,
                        out CompletedVideoMetadata actual))
                {
                    continue;
                }

                CompletedVideoSpecificationEvaluation evaluation =
                    CompletedVideoSpecificationProbe.Evaluate(expected, actual);
                if (!evaluation.ShouldEvaluate || evaluation.MeetsSpecification)
                    continue;

                RuntimeLog.Warn(
                    "RecordingProfile",
                    $"completed file={Path.GetFileName(finalizedFile.FinalPath)}, expected={expected.Width}x{expected.Height}@{expected.Fps}/{expected.DurationSeconds:F1}s, actual={actual.Width}x{actual.Height}@{actual.AverageFrameRate:F2}/{actual.DurationSeconds:F1}s, reason={evaluation.Reason}");
                _alertService?.Publish(new AlertRequest
                {
                    Message = "实际录制规格未达到设置值，可到“设置 → 高级设置 → 检测并推荐录制规格”获取推荐，或在录制设置中自行降低分辨率或帧率",
                    Priority = AlertPriority.Normal,
                    Sound = AlertSound.None,
                    DisplayDuration = TimeSpan.FromSeconds(5),
                    DeduplicationKey = "recording-specification-below-target",
                    DeduplicationWindow = TimeSpan.FromMinutes(30)
                });
            }
        }

        private List<string> GetMkvConversionTargets()
        {
            var paths = _db?.QueryActiveVideoFilePaths() ?? [];
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    if (HasMkvConversionCandidate(path))
                        targets.Add(path);
                    continue;
                }

                if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    continue;

                string mkvPath = Path.ChangeExtension(path, ".mkv");
                if (File.Exists(mkvPath) && HasMuxRecoverySidecar(mkvPath))
                {
                    RuntimeLog.Warn("MkvRecover", $"Database points to MP4 but MKV sidecar remains, scheduling recovery file={Path.GetFileName(mkvPath)}");
                    targets.Add(mkvPath);
                }
            }

            return targets.ToList();
        }

        private ArchiveLoadState GetArchiveLoadState()
        {
            if (Volatile.Read(ref _isRecording))
                return ArchiveLoadState.Paused;

            long nowTicks = DateTime.UtcNow.Ticks;
            long heartbeatTicks = Interlocked.Read(ref _archiveUiHeartbeatUtcTicks);
            if (heartbeatTicks > 0
                && nowTicks - heartbeatTicks > UiHeartbeatStaleThreshold.Ticks)
            {
                return ArchiveLoadState.Degraded;
            }

            if (Volatile.Read(ref _archiveCameraActive) == 0)
                return ArchiveLoadState.Healthy;

            long frameTicks = Interlocked.Read(ref _archiveFrameUtcTicks);
            long previewTicks = Interlocked.Read(ref _archivePreviewUtcTicks);
            return (frameTicks > 0
                    && nowTicks - frameTicks > PreviewFreezeWarnThreshold.Ticks)
                || (previewTicks > 0
                    && nowTicks - previewTicks > PreviewFreezeWarnThreshold.Ticks)
                ? ArchiveLoadState.Degraded
                : ArchiveLoadState.Healthy;
        }

        internal static bool HasMkvConversionCandidate(string mkvPath)
        {
            if (string.IsNullOrWhiteSpace(mkvPath)
                || !mkvPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return File.Exists(mkvPath)
                || File.Exists(Path.ChangeExtension(mkvPath, ".mp4"));
        }

        private void InitializeSystem()
        {
            _cts = new CancellationTokenSource();
            _lastActivityTime = DateTime.Now;
            RuntimeLog.Info("System", "InitializeSystem");
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
            Task.Run(CameraIdleWatchdog);
            // 正常启动时发现监听权限或防火墙规则缺失，会立即请求管理员授权。
            // 自动化临时运行模式禁用该系统级变更。
            _webServerStartupTask = RestartWebServerAsync(
                allowAccessSetup: ShouldRepairLanAccessAtStartup(Config, AllowLanAccessSetupOnStartup));

            // 启动时自动将上次断电残留的 MKV 转换为 MP4
            _mkvRecoveryTask = Task.Run(RecoverOrphanedMkvAsync);
        }

        internal static bool ShouldRepairLanAccessAtStartup(
            AppConfig config,
            bool allowLanAccessSetup = true)
        {
            if (config == null || !allowLanAccessSetup)
                return false;
            return config.EnableWebServer
                || string.Equals(
                    config.DeploymentPreset,
                    DeploymentPresets.RecordingWorkstation,
                    StringComparison.OrdinalIgnoreCase);
        }

        private async Task RecoverOrphanedMkvAsync()
        {
            try
            {
                var result = await BatchConvertMkvToMp4Async(
                    new Progress<string>(msg => Debug.WriteLine($"[MkvRecover] {msg}")),
                    CancellationToken.None);

                if (result.SuccessCount > 0 || result.FailureCount > 0)
                {
                    Debug.WriteLine($"[MkvRecover] 启动恢复完成: 成功={result.SuccessCount}, 失败={result.FailureCount}, 跳过={result.SkippedCount}, 暂缓={result.DeferredCount}, 静默={result.SuppressedCount}");
                    if (result.SuccessCount > 0)
                    {
                        _ = Application.Current.Dispatcher.BeginInvoke(() =>
                            ShowToast($"已恢复 {result.SuccessCount} 个断电残留视频"));
                    }
                    // 启动对账静默处理历史记录，不重复提醒用户旧版本失败。
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvRecover] 异常: {ex.Message}");
            }
        }

        public async Task<bool> SaveRecordingsBeforeShutdownAsync(IProgress<string> progress = null)
        {
            if (_isDisposed) return true;
            if (!await _shutdownLock.WaitAsync(0)) return false;

            bool previousBusy = IsBusy;
            string previousBusyText = BusyText;
            bool prepared = false;
            _shutdownRequested = true;
            IsShutdownInProgress = true;
            try
            {
                RuntimeLog.Info("Shutdown", $"Save before shutdown start recording={IsRecording}");
                progress?.Report("正在保存录像，请稍候...");
                IsBusy = true;
                BusyText = "正在关闭程序...";

                if (IsRecording)
                {
                    progress?.Report("正在停止当前录像...");
                    await _recorderLock.WaitAsync();
                    try
                    {
                        if (IsRecording)
                        {
                            _stopReason = "程序退出";
                            PauseSpeechForRecording();
                            await InternalStopRecordingAsync();
                        }
                    }
                    finally
                    {
                        if (!IsRecording)
                            ResumeSpeechWhenCameraIdle();
                        _recorderLock.Release();
                    }
                }

                if (_lastFinalizeTask != null)
                {
                    progress?.Report("正在写入录像记录...");
                    await _lastFinalizeTask;
                }

                if (_postStopMuxTask != null && !_postStopMuxTask.IsCompleted)
                {
                    progress?.Report("正在等待当前录像合成...");
                    await _postStopMuxTask;
                }

                if (_mkvRecoveryTask != null && !_mkvRecoveryTask.IsCompleted)
                {
                    progress?.Report("正在等待后台录像恢复...");
                    await _mkvRecoveryTask;
                }

                progress?.Report("正在合成 MP4 录像...");
                var result = await BatchConvertMkvToMp4Async(progress, CancellationToken.None);
                RuntimeLog.Info("Shutdown", $"Save before shutdown done success={result.SuccessCount}, fail={result.FailureCount}, skip={result.SkippedCount}, deferred={result.DeferredCount}, suppressed={result.SuppressedCount}");

                if (result.ShouldNotify)
                {
                    ShowMkvFailureToastIfNeeded(result);
                    RuntimeLog.Warn("Shutdown", $"Save before shutdown has failed historical conversions, allowing exit. failedConversions={result.FailureCount}");
                }

                _shutdownPrepared = true;
                prepared = true;
                return true;
            }
            catch (Exception ex)
            {
                ShowToast("录像保存失败，请检查日志", ToastSeverity.Error);
                RuntimeLog.Error("Shutdown", "Save before shutdown exception", ex);
                return false;
            }
            finally
            {
                if (!prepared)
                {
                    _shutdownRequested = false;
                    IsShutdownInProgress = false;
                    IsBusy = previousBusy && !_isDisposed;
                    BusyText = previousBusy ? previousBusyText : "";
                }
                else
                {
                    IsBusy = true;
                    BusyText = "正在关闭程序...";
                }
                _shutdownLock.Release();
            }
        }

        private async Task<bool> RestartWebServerAsync(
            bool allowAccessSetup,
            bool showFailureToast = true)
        {
            await _webServerLifecycleLock.WaitAsync();
            WebServer newServer = null;
            ExtensionRuntime newExtensionRuntime = null;
            try
            {
                bool orderReceiverOnly = IsRecordingWorkstation;
                Interlocked.Increment(ref _workstationAddressRefreshVersion);
                WebServer previousServer = _webServer;
                _webServer = null;
                try { previousServer?.Dispose(); } catch { }
                ExtensionRuntime previousExtensionRuntime = _extensionRuntime;
                _extensionRuntime = null;
                try { previousExtensionRuntime?.Dispose(); } catch { }

                if ((!Config.EnableWebServer && !orderReceiverOnly) || _db == null || _isDisposed)
                {
                    MonitorAccessAddress = "";
                    WorkstationPrintStatusText = "未连接";
                    WorkstationStatusToolTip = "开启局域网查看后，可点击手机/电脑连接查看二维码或复制网址";
                    SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceDisabled"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                    return true;
                }

                WorkstationPrintStatusText = orderReceiverOnly
                    ? "订单联动接收：等待启动"
                    : "启动中";
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceStarting"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                int port = Config.WebServerPort;
                int cacheMaxMb = Config.TranscodeCacheMaxMB;
                bool enableOrderInfoLog = Config.EnableOrderInfoLog;
                bool requireAccessKey = !orderReceiverOnly && Config.RequireWebAccessKey;
                string accessKey = Config.WebAccessKey;

                newServer = await Task.Run(() =>
                {
                    var server = new WebServer(
                        _db,
                        port,
                        cacheMaxMb,
                        () => IsRecording,
                        ConvertRecordMkvToMp4,
                        () => _currentVideoFilePath,
                        requireAccessKey,
                        accessKey,
                        mobileConnectionUrlProvider: BuildMonitorAccessUrl,
                        mobileBackupComputerId: Config.MobileBackupComputerId,
                        mobileBackupComputerName: Config.NodeName,
                        mobileBackupStateDirectory: AppPaths.MobileBackupStateDir,
                        mobileBackupRecordingRootResolver: ResolveBestStoragePath,
                        mobileBackupArchiveTargetResolver: () =>
                        {
                            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                                Config,
                                allowDefaultFallback: false);
                            return plan.RequiresNetworkArchive ? plan.ArchiveTarget : null;
                        },
                        mobileBackupArchivePendingCallback: () => _archiveService?.Wake(),
                        nodeId: Config.NodeId,
                        nodeName: Config.NodeName,
                        deploymentPreset: Config.DeploymentPreset,
                        orderReceiverOnly: orderReceiverOnly,
                        nodeNameCustomized: Config.NodeNameCustomized,
                        backupDeviceEnrollmentApprover: ApproveBackupDeviceEnrollment,
                        extensionApiEnabled: Config.EnableExtensionApi)
                    {
                        EnableOrderInfoLog = enableOrderInfoLog
                    };
                    if (Config.EnableExtensionApi)
                    {
                        _extensionAuthorizationStore ??= new ExtensionAuthorizationStore(
                            AppPaths.MobileBackupStateDir);
                        string extensionNodeId = Config.NodeId;
                        string extensionNodeName = Config.NodeName;
                        server.ConfigureExtensionEnrollment(
                            _extensionAuthorizationStore,
                            request => ExtensionEnrollmentApprovalPrompt.Show(
                                null,
                                request,
                                extensionNodeId,
                                extensionNodeName));
                        newExtensionRuntime = new ExtensionRuntime(
                            _db,
                            _dbFilePath,
                            Config.NodeId,
                            _extensionAuthorizationStore,
                            OnRecordingExtensionDataChanged,
                            order => OnOrderInfoReceived([order]));
                        server.ConfigureExtensionTaskApi(
                            newExtensionRuntime.Broker,
                            newExtensionRuntime.Coordinator,
                            newExtensionRuntime.ProcessAvailableResults);
                    }
                    try
                    {
                        server.OrderInfoReceived += OnOrderInfoReceived;
                        server.RecordingExtensionDataChanged += OnRecordingExtensionDataChanged;
                        server.ConnectedClientsChanged += OnConnectedClientsChanged;
                        server.MobileAppUpdateAvailable += OnMobileAppUpdateAvailable;
                        server.MobileBackupCompleted += OnMobileBackupCompleted;
                        server.Start(allowAccessSetup);
                        return server;
                    }
                    catch
                    {
                        server.Dispose();
                        throw;
                    }
                });

                if (_isDisposed)
                {
                    newServer.Dispose();
                    return false;
                }

                _webServer = newServer;
                newServer = null;
                _extensionRuntime = newExtensionRuntime;
                newExtensionRuntime = null;
                await RefreshWorkstationStatusAsync();
                QueueRecordingWorkstationHeartbeat(force: true);
                RuntimeLog.Info(
                    "Web",
                    $"LAN service started port={port}, cacheMaxMB={cacheMaxMb}, orderReceiverOnly={orderReceiverOnly}, extensionApiEnabled={Config.EnableExtensionApi}");
                return true;
            }
            catch (Exception ex)
            {
                try { newServer?.Dispose(); } catch { }
                try { newExtensionRuntime?.Dispose(); } catch { }
                RuntimeLog.Error("Web", "LAN service start failed", ex);
                string userMessage = WebServer.GetLanAccessFailureUserMessage(
                    repairAttempted: false,
                    exception: ex);
                MonitorAccessAddress = "";
                WorkstationPrintStatusText = IsRecordingWorkstation
                    ? "订单联动接收：启动失败"
                    : "启动失败";
                WorkstationStatusToolTip = userMessage;
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceUnavailable"), userMessage);
                if (showFailureToast)
                    ShowToast(userMessage, ToastSeverity.Error);
                return false;
            }
            finally
            {
                _webServerLifecycleLock.Release();
            }
        }

        private void OnRecordingExtensionDataChanged(string recordingSessionId, IReadOnlyList<RecordingExtensionField> fields)
        {
            if (string.IsNullOrWhiteSpace(recordingSessionId)
                || !string.Equals(recordingSessionId, _recordingSessionId, StringComparison.Ordinal))
                return;

            var lines = (fields ?? Array.Empty<RecordingExtensionField>())
                .Where(field => field != null && !string.IsNullOrWhiteSpace(field.FieldName))
                .OrderBy(field => field.Namespace, StringComparer.Ordinal)
                .ThenBy(field => field.FieldName, StringComparer.Ordinal)
                .Take(4)
                .Select(field =>
                {
                    string value = field.Value ?? "";
                    if (value.Length > 96) value = value[..96] + "…";
                    string prefix = string.IsNullOrWhiteSpace(field.Namespace)
                        ? field.FieldName
                        : $"{field.Namespace}.{field.FieldName}";
                    return $"{prefix}: {value}";
                })
                .ToArray();
            _recordingWatermarkSnapshot = new WatermarkSnapshot(recordingSessionId, lines);
        }

        private async Task RefreshWorkstationStatusAsync()
        {
            int version = Interlocked.Increment(ref _workstationAddressRefreshVersion);
            if (_webServer == null)
            {
                MonitorAccessAddress = "";
                WorkstationPrintStatusText = IsRecordingWorkstation
                    ? "订单联动接收：未连接"
                    : "未连接";
                WorkstationStatusToolTip = "其他设备暂时无法连接这台电脑";
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceDisabled"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                return;
            }

            MonitorAccessAddress = "";
            WorkstationPrintStatusText = IsRecordingWorkstation
                ? "订单联动接收：等待连接"
                : "启动中";
            WorkstationStatusToolTip = "正在准备给其他电脑浏览器使用的网址。两台电脑需要在同一局域网内";
            SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceStarting"), AppLanguage.Get("Main.ConnectionEmptyTip"));

            string verifiedAddress;
            try
            {
                verifiedAddress = await WorkstationNetwork.GetVerifiedLocalAccessAddressAsync(Config.WebServerPort);
            }
            catch
            {
                verifiedAddress = WorkstationNetwork.GetBestLocalAccessAddress(Config.WebServerPort);
            }

            if (version != _workstationAddressRefreshVersion || _webServer == null)
                return;

            MonitorAccessAddress = verifiedAddress;
            if (IsRecordingWorkstation)
            {
                WorkstationPrintStatusText = $"订单联动接收 · {verifiedAddress}";
                WorkstationStatusToolTip = "此地址仅用于接收订单联动，不提供本机录像浏览或备份主机服务";
            }
            else
            {
                WorkstationPrintStatusText = "已就绪";
                WorkstationStatusToolTip = Config.RequireWebAccessKey
                    ? "访问保护已开启。请点击手机/电脑连接查看二维码或复制完整访问链接，再发送到需要查看录像的设备"
                    : $"其他电脑在浏览器输入 http://{MonitorAccessAddress}，即可搜索、下载和播放视频。若打不开，请确认两台电脑在同一局域网，并检查防火墙";
            }
            UpdateConnectedClients(_webServer.GetConnectedClients());
            RefreshMobileBackupStatuses();
        }

        private void OnConnectedClientsChanged(IReadOnlyList<ConnectedClientInfo> clients)
        {
            Application application = Application.Current;
            if (application == null || application.Dispatcher.CheckAccess())
            {
                UpdateConnectedClients(clients);
                return;
            }

            _ = application.Dispatcher.InvokeAsync(() =>
            {
                if (!_isDisposed) UpdateConnectedClients(clients);
            });
        }

        private void OnMobileAppUpdateAvailable(MobileAppUpdateAvailableInfo update)
        {
            Application application = Application.Current;
            if (application == null)
                return;

            _ = application.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed)
                    return;
                ShowMobileAppUpdate(update);
            });
        }

        private VideoFolderImportService CreateVideoImportService(string activeFolderPath)
        {
            string preset = DeploymentPresets.Normalize(Config.DeploymentPreset);
            if (preset is not (DeploymentPresets.RecordingHost or DeploymentPresets.RecordingWorkstation)
                || _db == null)
                return null;

            IEnumerable<string> managedRoots = preset == DeploymentPresets.RecordingWorkstation
                ? [activeFolderPath]
                : Config.StorageLocations
                    .Where(location => StorageVolumeInfo.IsConfirmedLocal(location.Path))
                    .Select(location =>
                    {
                        try { return StorageLocationResolver.Resolve(location); }
                        catch { return ""; }
                    });
            return new VideoFolderImportService(
                _db,
                managedRoots,
                Config.NodeId,
                Config.NodeName);
        }

        private BackupDeviceEnrollmentApprovalDecision ApproveBackupDeviceEnrollment(
            BackupDeviceEnrollmentRequest request) =>
            BackupDeviceEnrollmentApprovalPrompt.Show(null, request);

        private void ShowMobileAppUpdate(MobileAppUpdateAvailableInfo update)
        {
            System.Windows.Window owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsLoaded)
                MobileAppUpdatePrompt.Show(owner, update);
        }

        private void UpdateConnectedClients(IReadOnlyList<ConnectedClientInfo> clients)
        {
            if (_isDisposed) return;
            _connectedClientSnapshot = clients ?? [];
            int count = ConnectedClientRegistry.CountDistinctAddresses(clients);
            HasConnectedDevices = count > 0;
            ConnectedDeviceText = count > 0
                ? AppLanguage.Format("Main.ConnectedDevices", count)
                : AppLanguage.Get("Main.NoConnectedDevices");
            if (count == 0)
            {
                ConnectedDeviceToolTip = AppLanguage.Get("Main.ConnectionEmptyTip");
                RefreshMobileBackupStatuses();
                return;
            }

            string[] details = clients
                .GroupBy(client => GetConnectedClientTypeLabel(client.ClientType))
                .OrderBy(group => group.Key, StringComparer.CurrentCulture)
                .Select(group => $"{group.Key} {group.Count()}")
                .ToArray();
            ConnectedDeviceToolTip = string.Join("\n", details);
            RefreshMobileBackupStatuses();
        }

        private void OnMobileBackupCompleted(string deviceId, string deviceName)
        {
            Application application = Application.Current;
            if (application == null || application.Dispatcher.CheckAccess())
            {
                RefreshMobileBackupStatuses();
                return;
            }
            _ = application.Dispatcher.InvokeAsync(RefreshMobileBackupStatuses);
        }

        private void RefreshMobileBackupStatuses()
        {
            if (_isDisposed)
                return;

            _mobileBackupStatusDate = DateTime.Today;
            IReadOnlyList<MobileBackupDailyCount> counts =
                _db?.GetMobileBackupDailyCounts(_mobileBackupStatusDate) ?? [];
            var statusByDevice = counts
                .Where(item => BackupDeviceIdentity.IsRemote(item.DeviceId, Config.NodeId))
                .ToDictionary(
                item => item.DeviceId,
                item => new
                {
                    Name = string.IsNullOrWhiteSpace(item.DeviceName)
                        ? GetFallbackDeviceName(item.DeviceId, item.DeviceKind)
                        : item.DeviceName,
                    Kind = item.DeviceKind,
                    Count = item.VideoCount,
                    Online = false
                },
                StringComparer.OrdinalIgnoreCase);

            foreach (ConnectedClientInfo client in _connectedClientSnapshot
                .Where(client => ShouldIncludeBackupDeviceClient(client, Config.NodeId)))
            {
                string deviceId = string.IsNullOrWhiteSpace(client.NodeId)
                    ? client.ClientId
                    : client.NodeId;
                if (!BackupDeviceIdentity.IsRemote(deviceId, Config.NodeId))
                    continue;

                statusByDevice.TryGetValue(deviceId, out var existing);
                bool isComputer = string.Equals(
                    client.ClientType,
                    "recording-workstation",
                    StringComparison.OrdinalIgnoreCase);
                statusByDevice[deviceId] = new
                {
                    Name = string.IsNullOrWhiteSpace(client.DisplayName)
                        ? existing?.Name ?? (isComputer ? "电脑设备" : "手机设备")
                        : client.DisplayName,
                    Kind = isComputer ? "pc" : "mobile",
                    Count = existing?.Count ?? 0,
                    Online = true
                };
            }

            MobileBackupDeviceStatuses.Clear();
            foreach (var item in statusByDevice
                .OrderBy(pair => pair.Value.Name, StringComparer.CurrentCulture)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                MobileBackupDeviceStatuses.Add(new MobileBackupDeviceStatus
                {
                    DeviceId = item.Key,
                    DisplayText = $"{item.Value.Name} · 今日备份 {item.Value.Count} 个",
                    IsOnline = item.Value.Online
                });
            }

            if (MobileBackupDeviceStatuses.Count == 0)
            {
                MobileBackupDeviceStatuses.Add(new MobileBackupDeviceStatus
                {
                    DisplayText = "暂无手机/电脑设备",
                    IsOnline = false
                });
            }
            RefreshUserscriptStatus();
        }

        private void RefreshUserscriptStatus()
        {
            _lastUserscriptStatusRefreshAt = DateTime.Now;
            IReadOnlyList<RecordingDeviceInfo> devices = _webServer?.GetRecordingDevices(
                MonitorAccessAddress,
                includeKnown: true) ?? [];
            UserscriptTargetStatus status = UserscriptTargetState.GetStatus(Config, devices);
            (string shortStatus, string detailText) =
                UserscriptStatusCardModel.GetCardTexts(status);
            UserscriptSetupStatusText = AppLanguage.Get(detailText);
            UserscriptSetupShortStatusText = AppLanguage.Get(shortStatus);
            UserscriptButtonText = AppLanguage.Get(status.ButtonText);
        }

        private static string GetFallbackDeviceName(string deviceId, string deviceKind)
        {
            string normalized = new((deviceId ?? "")
                .Where(char.IsLetterOrDigit)
                .ToArray());
            string fallback = string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "电脑设备"
                : "手机设备";
            return normalized.Length == 0
                ? fallback
                : $"{fallback} {normalized[^Math.Min(6, normalized.Length)..].ToUpperInvariant()}";
        }

        internal static bool ShouldIncludeBackupDeviceClient(
            ConnectedClientInfo client,
            string localNodeId)
        {
            if (client == null)
                return false;

            bool supportedType =
                string.Equals(client.ClientType, "mobile-app", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(client.ClientType, "recording-workstation", StringComparison.OrdinalIgnoreCase);
            if (!supportedType)
                return false;

            string deviceId = string.IsNullOrWhiteSpace(client.NodeId)
                ? client.ClientId
                : client.NodeId;
            return BackupDeviceIdentity.IsRemote(deviceId, localNodeId);
        }

        private void SetConnectedDeviceUnavailable(string text, string tooltip)
        {
            HasConnectedDevices = false;
            ConnectedDeviceText = text;
            ConnectedDeviceToolTip = tooltip;
        }

        private static string GetConnectedClientTypeLabel(string clientType) => clientType switch
        {
            "web-desktop" => AppLanguage.Get("Main.ClientWebDesktop"),
            "web-mobile" => AppLanguage.Get("Main.ClientWebMobile"),
            "userscript" => AppLanguage.Get("Main.ClientUserscript"),
            "print-station" => AppLanguage.Get("Main.ClientPrintStation"),
            "mobile-app" => AppLanguage.Get("Main.ClientMobileApp"),
            "recording-workstation" => AppLanguage.Get("录制电脑"),
            _ => AppLanguage.Get("Main.ClientOther")
        };

        public void CopyMonitorAddress()
        {
            if (!TryGetMobileConnectionUrl(out string url))
            {
                ShowToast(GetMobileConnectionUnavailableMessage(), ToastSeverity.Warning);
                return;
            }

            bool copied = false;
            for (int i = 0; i < 3 && !copied; i++)
            {
                try
                {
                    Clipboard.SetDataObject(url, true);
                    copied = true;
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }

            bool opened = WorkstationNetwork.TryOpenUrl(url, out string openError);
            if (copied && opened)
                ShowToast("已复制并打开监控网页");
            else if (copied)
                ShowToast($"已复制地址，打开网页失败: {openError}", ToastSeverity.Error);
            else if (opened)
                ShowToast("已打开监控网页，复制失败请重试", ToastSeverity.Warning);
            else
                ShowToast($"复制和打开都失败: {openError}", ToastSeverity.Error);
        }

        private string BuildMonitorAccessUrl()
        {
            return MobileConnectionService.TryBuildUsableAccessUrl(
                MonitorAccessAddress,
                Config.RequireWebAccessKey,
                Config.WebAccessKey,
                out string url)
                ? url
                : "";
        }

        public async void ShowMobileConnection(System.Windows.Window owner = null)
        {
            if (ShouldEnableWebServerForMobileConnection(Config))
            {
                Config.EnableWebServer = true;
                if (!SaveConfig(notifyUser: false))
                {
                    Config.EnableWebServer = false;
                    ShowToast("设备连接服务启用失败，请检查配置文件权限", ToastSeverity.Error);
                }
                else
                {
                    ShowToast("正在启动设备连接服务...", ToastSeverity.Information);
                    await RestartWebServerAsync(allowAccessSetup: true);
                }
            }

            string unavailableMessage = GetMobileConnectionUnavailableMessage();
            string url = "";
            if (string.IsNullOrEmpty(unavailableMessage))
                TryGetMobileConnectionUrl(out url);

            var dialogOwner = owner ?? Application.Current?.MainWindow;
            var dialog = new MobileConnectionWindow(
                url,
                Config.RequireWebAccessKey,
                unavailableMessage,
                canOpenSettings: owner is not SettingsWindow,
                repairLanAccessAsync: RepairLanAccessForMobileConnectionAsync)
            {
                Owner = dialogOwner
            };

            MainWindow mainWindow = Application.Current?.MainWindow as MainWindow;
            mainWindow?.SuspendCapsLockForModalWindow();
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                mainWindow?.ResumeCapsLockAfterModalWindow();
            }

            if (dialog.OpenSettingsRequested && owner is not SettingsWindow)
                OpenSettings();
        }

        private async Task<MobileConnectionRepairResult> RepairLanAccessForMobileConnectionAsync()
        {
            Exception repairException = null;
            try
            {
                RuntimeLog.Info("Web", $"Repairing LAN access from connection dialog port={Config.WebServerPort}");
                await Task.Run(() => WebServer.RepairLanAccess(Config.WebServerPort));
                bool started = await RestartWebServerAsync(
                    allowAccessSetup: false,
                    showFailureToast: false);
                if (started && TryGetMobileConnectionUrl(out string url))
                {
                    ShowToast("局域网连接已修复");
                    return new MobileConnectionRepairResult(
                        true,
                        url,
                        Config.RequireWebAccessKey,
                        "");
                }
            }
            catch (Exception ex)
            {
                repairException = ex;
                RuntimeLog.Error("Web", "LAN access repair from connection dialog failed", ex);
            }

            string message = WebServer.GetLanAccessFailureUserMessage(
                repairAttempted: true,
                exception: repairException);
            ShowToast(message, ToastSeverity.Error);
            return new MobileConnectionRepairResult(
                false,
                "",
                Config.RequireWebAccessKey,
                message);
        }

        internal static bool ShouldEnableWebServerForMobileConnection(AppConfig config) =>
            config != null
            && !config.EnableWebServer
            && string.Equals(
                config.DeploymentPreset,
                DeploymentPresets.RecordingHost,
                StringComparison.OrdinalIgnoreCase);

        public void CopyMobileConnectionUrl()
        {
            if (!TryGetMobileConnectionUrl(out string url))
            {
                ShowToast(GetMobileConnectionUnavailableMessage(), ToastSeverity.Warning);
                return;
            }

            if (!ClipboardHelper.TrySetDataObject(url, out Exception error))
            {
                ShowToast($"复制网址失败: {error.Message}", ToastSeverity.Error);
                return;
            }

            ShowToast(MobileConnectionService.ContainsAccessKey(url)
                ? "连接网址已复制，包含访问密钥，请勿发送给无关人员"
                : "连接网址已复制");
        }

        private void ShowMobileConnectionWindow(System.Windows.Window owner, string url)
        {
            var dialog = new MobileConnectionWindow(url, Config.RequireWebAccessKey) { Owner = owner };
            MainWindow mainWindow = Application.Current?.MainWindow as MainWindow;
            mainWindow?.SuspendCapsLockForModalWindow();
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                mainWindow?.ResumeCapsLockAfterModalWindow();
            }
        }

        private bool TryGetMobileConnectionUrl(out string url)
        {
            url = "";
            return Config.EnableWebServer
                && _webServer != null
                && MobileConnectionService.TryBuildUsableAccessUrl(
                    MonitorAccessAddress,
                    Config.RequireWebAccessKey,
                    Config.WebAccessKey,
                    out url);
        }

        private string GetMobileConnectionUnavailableMessage()
        {
            if (!Config.EnableWebServer)
                return "局域网查看尚未开启，请先在设置中启用";
            if (_webServer == null)
                return "局域网服务暂时不可用，请检查端口、权限或防火墙设置";
            if (!MobileConnectionService.TryBuildUsableAccessUrl(
                    MonitorAccessAddress,
                    Config.RequireWebAccessKey,
                    Config.WebAccessKey,
                    out _))
            {
                return "尚未取得可供手机访问的局域网地址，请确认电脑已连接局域网";
            }

            return "";
        }

        public async void SwitchWorkstation()
        {
            if (!CanSwitchWorkstation)
                return;

            var selector = new WorkstationSelectionWindow(Config.DeploymentPreset)
            {
                Owner = Application.Current?.MainWindow
            };
            if (selector.ShowDialog() == true && !string.IsNullOrWhiteSpace(selector.SelectedPreset))
            {
                if (string.Equals(
                        Config.DeploymentPreset,
                        selector.SelectedPreset,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ShowToast($"当前已经是{DeploymentPresets.GetDisplayName(Config.DeploymentPreset)}", ToastSeverity.Information);
                    return;
                }

                AppConfig nextConfig =
                    JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                nextConfig.DeploymentPreset = selector.SelectedPreset;
                if (selector.SelectedPreset == DeploymentPresets.RecordingWorkstation)
                {
                    nextConfig.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                    RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                        nextConfig,
                        preserveExistingLocation: true);
                }
                nextConfig.WorkstationRole = DeploymentCapabilities
                    .ForPreset(selector.SelectedPreset)
                    .IsRecordingDevice
                        ? WorkstationRoles.CameraMonitor
                        : selector.SelectedPreset == DeploymentPresets.MobileBackupHost
                            ? WorkstationRoles.PrintStation
                            : "";
                nextConfig.EnableWebServer = DeploymentCapabilities
                    .ForPreset(selector.SelectedPreset)
                    .CanRunWebServer;
                await RunPurposeSwitchAsync(nextConfig);
            }
        }

        private async Task<bool> RunPurposeSwitchAsync(AppConfig nextConfig)
        {
            if (_purposeSwitchPending || IsRecording)
                return false;

            _purposeSwitchPending = true;
            OnPropertyChanged(nameof(CanSwitchWorkstation));
            SwitchWorkstationButtonText = "正在切换";
            try
            {
                if (_webServer?.HasActiveMobileBackups == true)
                {
                    SwitchWorkstationButtonText = "等待备份完成";
                    ShowToast("设备录像正在备份，完成后将自动重启", ToastSeverity.Warning);
                    await _webServer.WaitForMobileBackupsAsync(_purposeSwitchCts.Token);
                }

                while (IsRecording)
                {
                    SwitchWorkstationButtonText = "等待录像完成";
                    await Task.Delay(250, _purposeSwitchCts.Token);
                }

                _purposeSwitchCts.Token.ThrowIfCancellationRequested();
                if (!SaveConfig(nextConfig, notifyUser: true))
                    return false;

                Config = nextConfig;
                return WorkstationNetwork.RestartAfterPurposeChange(Application.Current?.MainWindow);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                if (!WorkstationNetwork.IsRestartPending)
                {
                    _purposeSwitchPending = false;
                    SwitchWorkstationButtonText = "切换用途";
                    OnPropertyChanged(nameof(CanSwitchWorkstation));
                }
            }
        }

        /// <summary>收到油猴脚本推送的订单信息时，提前生成 TTS 缓存</summary>
        private void OnOrderInfoReceived(List<OrderInfo> orders)
        {
            if (orders == null) return;

            bool hasTestOrder = orders.Any(x => x.IsTest);
            string printStatusText = hasTestOrder
                ? AppLanguage.Format("Main.PrintTestOrder", DateTime.Now.ToString("HH:mm"))
                : orders.Count == 0
                    ? AppLanguage.Format("Main.PrintNoRefund", DateTime.Now.ToString("HH:mm"))
                    : AppLanguage.Format("Main.PrintOrders", DateTime.Now.ToString("HH:mm"), orders.Count);
            Application application = Application.Current;
            if (application != null)
            {
                _ = application.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    if (_webServer != null)
                    {
                        OrderIntegrationStatusText = printStatusText;
                    }

                    string activeOrderId = IsRecording ? _recordingOrderId : CurrentOrderId;
                    OrderInfo activeOrder = orders.FirstOrDefault(info =>
                        !info.IsTest
                        && string.Equals(info.TrackingNumber?.Trim(), activeOrderId?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (IsRecording && activeOrder != null)
                        SetPreviewOrderNotice(activeOrder);

                    if (hasTestOrder)
                    {
                        ShowToast("已收到测试订单");
                        SpeakWithRemarkTone(DefaultSpeechCatalog.TestOrderReceived, cancelPrevious: false);
                    }
                });
            }

            if (orders.Count == 0) return;

            var realOrders = orders.Where(x => !x.IsTest).ToList();
            if (realOrders.Count == 0)
                return;

            if (_alertService == null) return;
            if (Config.EnablePrintedRefundAlert)
            {
                foreach (string statusText in realOrders
                    .Where(info => info.IsPrintedRefund)
                    .Select(GetRefundStatusDisplayText)
                    .Distinct())
                {
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(statusText), AlertVoiceStyle.Warning);
                }
            }
            if (!Config.EnableOrderInfoAnnounce) return;
            foreach (var info in realOrders)
            {
                if (Config.AnnounceTotalItemCount
                    && !info.HasRefund
                    && !info.IsPrintedRefund
                    && info.TotalItemCount > 0)
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateOrderTotalCountAnnouncement(info.TotalItemCount));
                if (Config.AnnounceBuyerMessage && !string.IsNullOrWhiteSpace(info.BuyerMessage))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(info.BuyerMessage));
                if (Config.AnnounceSellerMemo && !string.IsNullOrWhiteSpace(info.SellerMemo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateSellerMemoAnnouncement(info.SellerMemo));
                if (Config.AnnounceProductInfo && !string.IsNullOrWhiteSpace(info.ProductInfo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateProductAnnouncement(info.ProductInfo));
            }
        }

        public void OpenUserscriptGuide()
        {
            if (_webServer == null || string.IsNullOrWhiteSpace(MonitorAccessAddress))
            {
                ShowToast("局域网服务尚未就绪，暂时无法生成快递助手脚本", ToastSeverity.Warning);
                return;
            }

            if (!UserscriptGuideNavigation.TryOpen($"http://{MonitorAccessAddress}", out string error))
            {
                ShowToast($"打开快递助手联动安装向导失败：{error}", ToastSeverity.Error);
                return;
            }

            UserscriptTargetState.MarkGuideOpened(
                Config,
                _webServer.GetRecordingDevices(MonitorAccessAddress, includeKnown: true));
            RefreshUserscriptStatus();
        }

        public void ImportUserscript()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "油猴脚本 (*.user.js)|*.user.js|所有文件 (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true,
                Title = "导入自定义油猴脚本"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var catalog = new UserscriptCatalog();
                UserscriptDescriptor descriptor = catalog.Import(dialog.FileName);
                string warning = descriptor.Warnings.Count == 0
                    ? ""
                    : $"\n\n检查提示：\n· {string.Join("\n· ", descriptor.Warnings)}";
                if (descriptor.Warnings.Count > 0 && !AppDialog.Confirm(
                        null,
                        $"脚本“{descriptor.Name}”已读取，但存在维护或安全提示：{warning}\n\n是否仍然导入？",
                        "导入自定义脚本",
                        AppDialogSeverity.Warning,
                        "确认导入"))
                {
                    catalog.Remove(descriptor.Id);
                    return;
                }
                ShowToast("自定义脚本已导入，可在安装订单联动中选择", ToastSeverity.Success);
            }
            catch (Exception ex)
            {
                AppDialog.Error(null, $"导入自定义脚本失败：{ex.Message}", "导入失败");
            }
        }

        private void PublishExtensionScanTaskIfRecordingStarted(string trackingNumber)
        {
            ExtensionRuntime runtime = _extensionRuntime;
            if (!Config.EnableExtensionApi
                || runtime == null
                || !IsRecording
                || _currentRecordId <= 0
                || string.IsNullOrWhiteSpace(_recordingSessionId))
                return;
            try
            {
                ExtensionScanPublishResult result = runtime.Publish(
                    Config.NodeId,
                    _recordingSessionId,
                    trackingNumber,
                    _recordingMode);
                if (result.Deliveries.Count > 0 || result.SkippedTargets.Count > 0)
                {
                    RuntimeLog.Info(
                        "ExtensionTask",
                        $"Published scan task session={_recordingSessionId}, deliveries={result.Deliveries.Count}, skipped={result.SkippedTargets.Count}");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("ExtensionTask", "Failed to publish scan task", ex);
            }
        }

        internal IReadOnlyList<ExtensionAuthorizationDisplayItem> GetExtensionAuthorizations()
        {
            if (_extensionAuthorizationStore == null)
            {
                string extensionDirectory = Path.Combine(AppPaths.MobileBackupStateDir, "extensions");
                if (!Directory.Exists(extensionDirectory))
                    return [];
                _extensionAuthorizationStore = new ExtensionAuthorizationStore(AppPaths.MobileBackupStateDir);
            }
            return _extensionAuthorizationStore.GetAll(includeRevoked: false)
                .Select(value => new ExtensionAuthorizationDisplayItem(
                    value.ExtensionInstanceId,
                    value.DisplayName,
                    string.IsNullOrWhiteSpace(value.RuntimeVersion) ? value.Version : value.RuntimeVersion,
                    value.Source,
                    string.Join("、", value.Permissions),
                    value.RoutingScope == ExtensionRoutingScope.AllLocalRecordingNodes
                        ? "所有本机录像工位"
                        : string.Join("、", value.BoundOriginNodeIds),
                    value.CredentialGeneration,
                    value.UpdatedAtUtc,
                    value.LastSeenUtc.HasValue
                        && DateTimeOffset.UtcNow - value.LastSeenUtc.Value <= TimeSpan.FromSeconds(45),
                    value.LastBusinessActivityUtc.HasValue
                        ? $"{value.LastBusinessActivityUtc.Value.ToLocalTime():HH:mm} 收到 {value.LastBusinessDataCount} 条数据"
                        : "暂未收到数据"))
                .ToArray();
        }

        internal IReadOnlyList<OrderIntegrationDeviceDisplayItem> GetOrderIntegrationDevices() =>
            (_webServer?.GetOrderIntegrationDeviceStatuses() ?? [])
                .Select(device => new OrderIntegrationDeviceDisplayItem(
                    device.NodeId,
                    device.DisplayName,
                    string.Equals(device.DeviceType, "mobile", StringComparison.OrdinalIgnoreCase)
                        ? "手机录像设备"
                        : "电脑录像设备",
                    device.Online,
                    FormatOrderIntegrationDeviceActivity(device.LastActivityUtc, device.ReceivedCount)))
                .ToArray();

        internal static string FormatOrderIntegrationDeviceActivity(
            DateTimeOffset? lastActivityUtc,
            int processedCount)
        {
            return lastActivityUtc.HasValue
                ? $"{lastActivityUtc.Value.ToLocalTime():HH:mm} 已处理 {processedCount} 条联动数据"
                : "暂无联动数据";
        }

        internal bool RevokeExtensionAuthorization(string extensionInstanceId) =>
            _extensionAuthorizationStore?.Revoke(extensionInstanceId) == true;

        private void Speak(string text, bool cancelPrevious = true) => PublishVoice(
            text,
            AlertVoiceStyle.Normal,
            AlertSound.None,
            repeatCount: 1,
            interruptCurrent: cancelPrevious);

        private void SpeakWithRemarkTone(string text, bool cancelPrevious = true) => PublishVoice(
            text,
            AlertVoiceStyle.Normal,
            AlertSound.Remark,
            repeatCount: 1,
            interruptCurrent: cancelPrevious);

        private void SpeakWarning(string text, int repeatCount = 1, bool cancelPrevious = true, bool playTonePerRepeat = false) => PublishVoice(
            text,
            AlertVoiceStyle.Warning,
            AlertSound.Warning,
            repeatCount: repeatCount,
            interruptCurrent: cancelPrevious,
            soundRepeatCount: playTonePerRepeat ? repeatCount : 1);

        private void PublishVoice(
            string text,
            AlertVoiceStyle voiceStyle,
            AlertSound sound,
            int repeatCount,
            bool interruptCurrent,
            int soundRepeatCount = 1)
        {
            _alertService?.Publish(new AlertRequest
            {
                SpeechText = text,
                VoiceStyle = voiceStyle,
                Sound = sound,
                SoundRepeatCount = soundRepeatCount,
                SpeechRepeatCount = repeatCount,
                InterruptCurrent = interruptCurrent,
                DisplayDuration = TimeSpan.Zero
            });
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _shutdownRequested = true;
            _isDisposed = true;
            try { _playbackWindow?.Close(); } catch { }
            try { _statisticsWindow?.Close(); } catch { }
            _cts?.Cancel();
            _cameraBarcodeFeedbackCts?.Cancel();
            _previewAlertCts?.Cancel();
            try { _cameraBarcodeRecognition?.Dispose(); } catch { }
            try { _cameraPairingQrDecoder.Dispose(); } catch { }
            try { _uiHeartbeatTimer?.Stop(); } catch { }
            _stopReason = "程序退出";

            string videoFileToConvert = null;
            string audioFileToConvert = null;
            string audioLogFileToUse = null;
            bool audioFailedForRecording = false;
            long audioBytesWrittenForRecording = 0;
            long recordId = 0;
            DateTime recordStart = DateTime.MinValue;

            try
            {
                if (IsRecording)
                {
                    videoFileToConvert = _currentVideoFilePath;
                    audioLogFileToUse = _currentAudioLogPath;
                    recordId = _currentRecordId;
                    recordStart = _recordStartTime;

                    _videoWriteQueue?.CompleteAdding();
                    _writeCts?.Cancel();
                    audioFileToConvert = StopAudioRecording();
                    audioFailedForRecording = _audioFailedForCurrentRecording;
                    audioBytesWrittenForRecording = _audioBytesWritten;
                    _writeTask?.Wait(5000); // 等待写入线程关闭 stdin，让 FFmpeg 正常结束

                    // 如果 FFmpeg 还没退出，再等一会儿让它写完尾部
                    if (_currentFfmpegProcess != null && !_currentFfmpegProcess.HasExited)
                    {
                        if (!_currentFfmpegProcess.WaitForExit(3000))
                        {
                            try { _currentFfmpegProcess.Kill(); } catch { }
                        }
                    }

                    IsRecording = false;
                }
            }
            catch { }

            // 正常关闭流程已异步等待录像收尾，不在 UI 关闭阶段重复阻塞。
            if (!_shutdownPrepared)
            {
                try { _lastFinalizeTask?.Wait(5000); } catch { }
                try { _postStopMuxTask?.Wait(5000); } catch { }
            }

            // 录制中退出：更新数据库并转换 MP4
            if (!string.IsNullOrEmpty(videoFileToConvert) && File.Exists(videoFileToConvert))
            {
                try
                {
                    long fileSize = GetCompletedRecordingSizeBytes(videoFileToConvert, audioFileToConvert);
                    long minFileSizeBytes = GetMinVideoFileSizeBytes();
                    bool tooSmall = minFileSizeBytes > 0 && fileSize < minFileSizeBytes;
                    if (!tooSmall && recordId > 0)
                    {
                        int durSec = Math.Max(1, (int)(DateTime.Now - recordStart).TotalSeconds);
                        _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, _stopReason, _currentVideoCodec, _currentVideoEncoder);
                        RuntimeLog.Info("Recording", $"Exit finalized MKV, queued for startup/web conversion: {Path.GetFileName(videoFileToConvert)}");
                    }
                    else
                    {
                        if (tooSmall && recordId > 0)
                        {
                            string deleteReason = $"文件过小，小于 {FormatMinVideoFileSize(Config.MinVideoFileSizeKB)}";
                            int durSec = Math.Max(1, (int)(DateTime.Now - recordStart).TotalSeconds);
                            _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, deleteReason, _currentVideoCodec, _currentVideoEncoder);
                            if (DeleteVideoFileForRule(videoFileToConvert, deleteReason))
                                _db?.MarkVideoDeleted(videoFileToConvert, deleteReason);
                        }
                        DeleteAudioTempFile(audioFileToConvert);
                    }
                }
                catch { }
            }

            StopCamera();
            ClearPreRecordBuffer();
            try { _videoTask?.Wait(1000); } catch { }
            _cts?.Dispose();
            lock (_videoLock)
            {
                _previousCheckFrame?.Dispose();
                _motionCurrentSmall?.Dispose();
                _motionPreviousSmall?.Dispose();
                _motionCurrentGray?.Dispose();
                _motionPreviousGray?.Dispose();
                _motionDiff?.Dispose();
                _motionThreshold?.Dispose();
            }
            _alertService?.Dispose();
            _speechService?.Dispose();
            _speechService = null;
            _purposeSwitchCts.Cancel();
            _purposeSwitchCts.Dispose();
            try { _globalKeyHook?.Dispose(); } catch { }
            try { _webServer?.Dispose(); } catch { }
            try { _extensionRuntime?.Dispose(); } catch { }
            _extensionRuntime = null;
            DisposeRecordingTransfers();
            if (_archiveService != null)
            {
                _archiveService.BackupTargetAvailabilityChanged -=
                    OnArchiveTargetAvailabilityChanged;
                _archiveService.WorkerStateChanged -= OnArchiveWorkerStateChanged;
                _archiveService.ArchiveQueueChanged -= OnArchiveQueueChanged;
                try { _archiveService.Dispose(); } catch { }
            }
            try { _db?.Dispose(); } catch { }
        }
    }
}
