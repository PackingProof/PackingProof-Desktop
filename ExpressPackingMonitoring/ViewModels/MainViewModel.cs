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
        private long _lastRecordingFrameProcessedTimestamp;
        private int _recordingFrameRecoveryRequested;
        private readonly RecordingFramePipelineDiagnostics _recordingFramePipelineDiagnostics = new();
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
        private Task _cameraIdleWatchdogTask;
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
        private readonly PrintedRefundLookupCoordinator _printedRefundLookupCoordinator;
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

        private void FilterLogs() { FilteredLogs.Clear(); var keyword = LogSearchText?.ToUpper() ?? ""; foreach (var log in _allLogs) { if (string.IsNullOrEmpty(keyword) || log.OrderId.ToUpper().Contains(keyword)) FilteredLogs.Add(log); } }
        private void AddRecord(ScanRecord record) { Application.Current.Dispatcher.InvokeAsync(() => { _allLogs.Insert(0, record); if (string.IsNullOrEmpty(LogSearchText)) FilteredLogs.Insert(0, record); if (_allLogs.Count > 200) _allLogs.RemoveAt(_allLogs.Count - 1); }); }

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
