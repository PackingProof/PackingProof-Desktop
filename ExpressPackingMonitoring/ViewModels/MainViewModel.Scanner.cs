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
    public partial class MainViewModel
    {
        private void ScheduleRefreshBarcodes()
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(RefreshBarcodes), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void RefreshBarcodesForDpiChange() => ScheduleRefreshBarcodes();

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
                    Barcode1Image = BarcodeHelper.Generate(cmd1, 52, 3);
                }
                if (!_barcode2OnCooldown)
                {
                    Barcode2Label = label2;
                    Barcode2Image = BarcodeHelper.Generate(cmd2, 52, 3);
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
            _printedRefundLookupCoordinator = new PrintedRefundLookupCoordinator(
                () => _webServer is { } server
                    ? new WebServerPrintedRefundOrderSource(server)
                    : null,
                CheckPrintedRefundAndAlert,
                () => _isDisposed);

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
            ScanCommand = new AsyncRelayCommand<string>(
                scanResult => HandleScanAsync(scanResult),
                AsyncRelayCommandOptions.AllowConcurrentExecutions);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            ResetEncoderDetectCommand = new AsyncRelayCommand(ResetEncoderDetectAsync);
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
            _ = HandleScanAsync(barcode);
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
                    _ = HandleScanAsync(code, fromCamera: true);
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
        private async Task ToggleRecordingAsync()
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
                RuntimeLog.Error("Recording", "Manual recording toggle failed", ex);
            }
            finally
            {
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
                _recorderLock.Release();
            }
        }

        private async Task HandleScanAsync(string scanResult, bool fromCamera = false)
        {
            try
            {
                await HandleScanCoreAsync(scanResult, fromCamera);
            }
            catch (Exception exception)
            {
                RuntimeLog.Error(
                    "Scan",
                    $"Unhandled scan failure source={(fromCamera ? "camera" : "scanner/manual")}",
                    exception);
            }
        }

        private async Task HandleScanCoreAsync(string scanResult, bool fromCamera)
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
                    await ToggleRecordingAsync();
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
                _ = HandleScanAsync(pending);
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

        public bool CanSwitchWorkstation => !IsRecording && !_purposeSwitchPending;
        public string SwitchWorkstationButtonText
        {
            get => _switchWorkstationButtonText;
            private set => SetProperty(ref _switchWorkstationButtonText, value);
        }

        private void QueuePrintedRefundCheck(string trackingNumber, string mode)
        {
            if (!Config.EnablePrintedRefundAlert || string.IsNullOrWhiteSpace(trackingNumber))
                return;
            _printedRefundLookupCoordinator.Queue(trackingNumber, mode);
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

    }
}
