#nullable disable
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.LensControl;
using ExpressPackingMonitoring.Services.MediaFoundation;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 「镜头焦距」面板：监控画面上直接调摄像头的光学变焦与对焦模式（issue #52）。
    ///
    /// 只对走 Media Foundation 后端、且驱动声明了手动变焦/手动对焦的摄像头开放
    /// （汇博士 W7 这类带马达镜头的 USB 机位）；普通定焦摄像头不显示入口。
    /// 设置会按摄像头记进 <see cref="AppConfig.CameraLensStates"/>，
    /// 重启、重连、休眠唤醒后自动恢复 —— 镜头是物理状态，记不住就得现场重调。
    ///
    /// 拖动滑条时的写入按"停稳再写"防抖：KS 属性是同步 IO，
    /// 拖动过程每个刻度都发一次会堵住 UI 线程，马达也跟不上。
    /// </summary>
    public partial class MainViewModel
    {
        internal sealed record LensUiSnapshot(
            LensRange ZoomRange,
            LensRange FocusRange,
            int? ZoomValue,
            (int Value, bool Auto)? FocusState);

        private const int LensApplyDebounceMilliseconds = 250;

        private ILensController _lensController;
        private string _lensConfigKey = "";
        private CameraLensSettings _lensStored;
        private DispatcherTimer _lensZoomApplyTimer;
        private DispatcherTimer _lensFocusApplyTimer;
        private bool _lensUpdatingUiState;
        private double _lensZoomMinimum;
        private double _lensZoomMaximum;
        private double _lensZoomTick = 1;
        private double _lensZoomValue;
        private double _lensFocusMinimum;
        private double _lensFocusMaximum;
        private double _lensFocusTick = 1;
        private double _lensFocusValue;
        private bool _lensFocusAuto = true;
        private bool _lensControlAvailable;
        private bool _lensPanelOpen;
        private bool _lensZoomControlEnabled;
        private bool _lensFocusControlEnabled;
        /// <summary>挂接代数：停止/换设备时递增，作废仍在途的异步挂接（网络探测是秒级往返）</summary>
        private int _lensAttachGeneration;
        private Func<Action, Task> _uiThreadInvoker = RunOnApplicationUiThreadAsync;
        /// <summary>镜头设备写入串行化：快速拖动/切换时保持下发顺序，避免旧值最后落到设备。</summary>
        private readonly SemaphoreSlim _lensCommandGate = new(1, 1);
        private long _lensZoomApplyVersion;
        private long _lensFocusApplyVersion;
        private int _lensStateRefreshRunning;

        private ICommand _toggleLensPanelCommand;
        private ICommand _setLensFocusAutoCommand;
        private ICommand _setLensFocusManualCommand;
        private string _lensStatusText = "";

        /// <summary>面板开合命令；懒建，避免字段初始化器引用实例方法。</summary>
        public ICommand ToggleLensPanelCommand =>
            _toggleLensPanelCommand ??= new RelayCommand(ToggleLensPanel);

        /// <summary>显式切到自动/手动对焦；设备拒绝时会回读实际状态并提示用户。</summary>
        public ICommand SetLensFocusAutoCommand =>
            _setLensFocusAutoCommand ??= new RelayCommand(() => LensFocusAuto = true);

        public ICommand SetLensFocusManualCommand =>
            _setLensFocusManualCommand ??= new RelayCommand(() => LensFocusAuto = false);

        /// <summary>当前摄像头是否提供镜头控制；采集停止/换设备时回收，界面据此显隐入口。</summary>
        public bool IsLensControlAvailable
        {
            get => _lensControlAvailable;
            private set => SetProperty(ref _lensControlAvailable, value);
        }

        /// <summary>镜头面板开合。设备不可用或摄像头停止时自动收起。</summary>
        public bool IsLensPanelOpen
        {
            get => _lensPanelOpen;
            private set
            {
                if (!SetProperty(ref _lensPanelOpen, value) || value)
                    return;

                // 收起时不再等防抖：把还没写进设备的滑条位置立刻应用并落盘。
                // 对应控件在设备状态无法回读时已被禁用，Apply 方法会拒绝下发。
                ApplyLensZoomNow();
                ApplyLensFocusNow();
            }
        }

        /// <summary>有没有变焦可调；没有这一项的摄像头只显示对焦区。</summary>
        public bool HasLensZoom { get; private set; }

        /// <summary>有没有手动对焦可调。</summary>
        public bool HasLensFocus { get; private set; }

        public bool IsLensZoomControlEnabled
        {
            get => _lensZoomControlEnabled;
            private set => SetProperty(ref _lensZoomControlEnabled, value);
        }

        public bool IsLensFocusControlEnabled
        {
            get => _lensFocusControlEnabled;
            private set => SetProperty(ref _lensFocusControlEnabled, value);
        }

        public double LensZoomMinimum
        {
            get => _lensZoomMinimum;
            private set => SetProperty(ref _lensZoomMinimum, value);
        }

        public double LensZoomMaximum
        {
            get => _lensZoomMaximum;
            private set => SetProperty(ref _lensZoomMaximum, value);
        }

        /// <summary>滑条刻度间隔 = 设备步进；步进乱报时至少保持 1，避免滑条除零。</summary>
        public double LensZoomTick
        {
            get => _lensZoomTick;
            private set => SetProperty(ref _lensZoomTick, value);
        }

        public double LensZoomValue
        {
            get => _lensZoomValue;
            set
            {
                if (!IsLensZoomControlEnabled
                    || _lensUpdatingUiState
                    || Math.Abs(_lensZoomValue - value) < 0.001)
                {
                    return;
                }

                Interlocked.Increment(ref _lensZoomApplyVersion);
                _lensZoomValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LensZoomPercentText));
                ScheduleLensZoomApply();
            }
        }

        /// <summary>变焦量在设备范围内的百分比，给滑条旁边一个可读的反馈。</summary>
        public string LensZoomPercentText
        {
            get
            {
                double span = _lensZoomMaximum - _lensZoomMinimum;
                if (span <= 0)
                    return "";
                int percent = (int)Math.Round((_lensZoomValue - _lensZoomMinimum) * 100.0 / span);
                return $"{Math.Clamp(percent, 0, 100)}%";
            }
        }

        /// <summary>对焦是否自动；false = 手动。切到手动时以设备当前对焦位置起步，避免画面跳焦。</summary>
        public bool LensFocusAuto
        {
            get => _lensFocusAuto;
            set
            {
                if (!IsLensFocusControlEnabled
                    || _lensUpdatingUiState
                    || _lensFocusAuto == value)
                {
                    return;
                }

                Interlocked.Increment(ref _lensFocusApplyVersion);
                _lensFocusAuto = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLensFocusManual));
                RuntimeLog.Info("Camera", $"对焦模式切换请求：auto={value}");
                ApplyLensFocusMode();
            }
        }

        /// <summary>手动对焦模式（= 不是自动）：对焦滑条只在手动时可用。</summary>
        public bool IsLensFocusManual => !LensFocusAuto;

        /// <summary>镜头写入失败时显示在面板底部；成功或重新挂接后清空。</summary>
        public string LensStatusText
        {
            get => _lensStatusText;
            private set => SetProperty(ref _lensStatusText, value);
        }

        public double LensFocusMinimum
        {
            get => _lensFocusMinimum;
            private set => SetProperty(ref _lensFocusMinimum, value);
        }

        public double LensFocusMaximum
        {
            get => _lensFocusMaximum;
            private set => SetProperty(ref _lensFocusMaximum, value);
        }

        public double LensFocusTick
        {
            get => _lensFocusTick;
            private set => SetProperty(ref _lensFocusTick, value);
        }

        public double LensFocusValue
        {
            get => _lensFocusValue;
            set
            {
                if (!IsLensFocusControlEnabled
                    || _lensUpdatingUiState
                    || Math.Abs(_lensFocusValue - value) < 0.001)
                {
                    return;
                }

                Interlocked.Increment(ref _lensFocusApplyVersion);
                _lensFocusValue = value;
                OnPropertyChanged();
                ScheduleLensFocusApply();
            }
        }

        /// <summary>
        /// MF 采集源启动成功后挂上镜头控制：先按记忆恢复镜头，
        /// 再公布界面状态。在采集启动线程上调用，控制器自己负责 STA 规避。
        /// </summary>
        internal void AttachCameraLens(MfCameraSource source, string moniker)
        {
            // 重复挂接（重连后没先走 StopCamera 的路径）时先清一次旧状态。
            DetachCameraLens();
            string configKey = moniker ?? "";
            CameraLensSettings stored = ReadLensStoredState(configKey);
            ILensController controller = source?.LensController;
            bool available = controller != null
                && CameraLensPolicy.ShouldShowControls(controller.ZoomRange, controller.FocusRange);
            LensUiSnapshot snapshot = available
                ? ApplyLensStartupPlan(controller, stored)
                : CaptureLensUiSnapshot(controller);

            int generation = Volatile.Read(ref _lensAttachGeneration);
            _ = RunOnUiThreadAsync(() =>
            {
                if (Volatile.Read(ref _lensAttachGeneration) != generation)
                    return;

                PublishLensController(controller, configKey, stored, available, snapshot);
            });
        }

        /// <summary>
        /// 网络摄像头启动后异步挂接 ONVIF 镜头控制：探测是几个 HTTP 往返（秒级），
        /// 不能堵住拉流启动；探测期间不显示入口，完成后统一公布。
        /// </summary>
        internal void AttachNetworkCameraLens()
        {
            DetachCameraLens();
            AppConfig config = Config;
            string streamUrl = config?.NetworkCameraUrl;
            if (config == null || string.IsNullOrWhiteSpace(streamUrl))
                return;

            string configKey = AppConfig.GetCameraConfigKey("network", streamUrl);
            int generation = Interlocked.Increment(ref _lensAttachGeneration);
            _ = AttachNetworkCameraLensAsync(streamUrl, configKey, generation);
        }

        private async Task AttachNetworkCameraLensAsync(string streamUrl, string configKey, int generation)
        {
            OnvifLensController controller = null;
            try
            {
                controller = await OnvifLensController.TryAttachAsync(streamUrl).ConfigureAwait(false);
                if (controller == null || Volatile.Read(ref _lensAttachGeneration) != generation)
                    return;

                CameraLensSettings stored = null;
                await RunOnUiThreadAsync(() => stored = ReadLensStoredState(configKey)).ConfigureAwait(false);
                bool available = CameraLensPolicy.ShouldShowControls(controller.ZoomRange, controller.FocusRange);
                LensUiSnapshot snapshot = available
                    ? ApplyLensStartupPlan(controller, stored)
                    : CaptureLensUiSnapshot(controller);

                await RunOnUiThreadAsync(() =>
                {
                    if (Volatile.Read(ref _lensAttachGeneration) != generation || _isDisposed)
                        return;

                    PublishLensController(controller, configKey, stored, available, snapshot);
                    // ViewModel 已接管控制器及其 HttpClient 生命周期。
                    controller = null;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"ONVIF 镜头挂接收尾失败：{ex.Message}");
            }
            finally
            {
                controller?.Dispose();
            }
        }

        /// <summary>采集源停止/被清理时回收镜头状态；重连后会重新 Attach 并恢复记忆。</summary>
        internal void DetachCameraLens()
        {
            // 递增代数让在途的网络探测作废，探测完成后不会把旧相机的状态发布出来
            Interlocked.Increment(ref _lensAttachGeneration);
            if (_lensController == null && !IsLensControlAvailable)
                return;

            if (_lensController is IDisposable disposable)
                disposable.Dispose();
            _lensController = null;
            _lensStored = null;
            IsLensControlAvailable = false;
            IsLensPanelOpen = false;
        }

        internal void DisposeCameraLens()
        {
            DetachCameraLens();
            try { _lensZoomApplyTimer?.Stop(); } catch { }
            try { _lensFocusApplyTimer?.Stop(); } catch { }
            _lensZoomApplyTimer = null;
            _lensFocusApplyTimer = null;
        }

        private CameraLensSettings ReadLensStoredState(string configKey)
        {
            try
            {
                return Config?.CameraLensStates?.TryGetValue(configKey, out CameraLensSettings stored) == true
                    ? stored
                    : null;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"读镜头记忆失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>挂接收尾公共路径：恢复记忆中的镜头状态，再公布界面可见性与初值。</summary>
        private void PublishLensController(
            ILensController controller,
            string configKey,
            CameraLensSettings stored,
            bool available,
            LensUiSnapshot snapshot)
        {
            _lensController = controller;
            _lensConfigKey = configKey;
            _lensStored = stored ?? new CameraLensSettings();
            LensStatusText = "";
            IsLensControlAvailable = available;
            if (!available)
                IsLensPanelOpen = false;

            NotifyLensUiState(snapshot);
            RuntimeLog.Info(
                "Camera",
                $"镜头控制挂接：available={available}, key={SanitizeLensKeyForLog(configKey)}, storedZoom={_lensStored.Zoom}, focusAuto={_lensStored.FocusAuto}, focus={_lensStored.Focus}");
        }

        /// <summary>面板开合入口（主画面按钮）。</summary>
        public void ToggleLensPanel()
        {
            if (!IsLensControlAvailable)
                return;

            bool opening = !IsLensPanelOpen;
            IsLensPanelOpen = opening;
            bool needsRefresh = (HasLensZoom && !IsLensZoomControlEnabled)
                || (HasLensFocus && !IsLensFocusControlEnabled);
            if (opening
                && needsRefresh
                && Interlocked.CompareExchange(ref _lensStateRefreshRunning, 1, 0) == 0)
            {
                _ = RefreshLensControlStateAsync();
            }
        }

        /// <summary>打开面板时重试一次设备状态读取，瞬时 COM 失败后不必重连摄像头。</summary>
        internal async Task RefreshLensControlStateAsync()
        {
            ILensController controller = _lensController;
            try
            {
                if (controller == null)
                    return;

                long zoomVersion = Volatile.Read(ref _lensZoomApplyVersion);
                long focusVersion = Volatile.Read(ref _lensFocusApplyVersion);
                LensUiSnapshot snapshot = await Task.Run(
                    () => CaptureLensUiSnapshot(controller));
                await RunOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(_lensController, controller) || _isDisposed)
                        return;

                    NotifyLensUiState(
                        snapshot,
                        updateZoom: zoomVersion == Volatile.Read(ref _lensZoomApplyVersion),
                        updateFocus: focusVersion == Volatile.Read(ref _lensFocusApplyVersion));
                });
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"重新读取镜头状态失败：{ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _lensStateRefreshRunning, 0);
            }
        }

        internal LensUiSnapshot ApplyLensStartupPlan(
            ILensController controller,
            CameraLensSettings stored)
        {
            LensUiSnapshot snapshot = CaptureLensUiSnapshot(controller);
            CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(
                controller.ZoomRange,
                controller.FocusRange,
                stored);
            plan = CameraLensPolicy.RequireReadableStateForStartup(
                plan,
                controller.ZoomRange,
                controller.FocusRange,
                snapshot.ZoomValue,
                snapshot.FocusState);
            if (plan.IsEmpty)
                return snapshot;

            bool zoomApplied = plan.Zoom == null || controller.TrySetZoom(plan.Zoom.Value);
            bool focusModeApplied = true;
            if (plan.FocusAuto == false)
            {
                // 先恢复位置再切手动：直接切手动再动位置会有一次"先跳到旧焦点再拉回"的抖动。
                focusModeApplied = plan.FocusManual != null
                    ? controller.TrySetFocusManual(plan.FocusManual.Value)
                    : controller.TrySetFocusManual(controller.FocusRange.Default);
            }

            RuntimeLog.Info(
                "Camera",
                $"镜头恢复：zoom={(plan.Zoom == null ? "不动" : plan.Zoom.Value.ToString())}"
                + $"，focusAuto={(plan.FocusAuto == null ? "不动" : plan.FocusAuto.Value.ToString())}"
                + $"，focus={(plan.FocusManual == null ? "-" : plan.FocusManual.Value.ToString())}"
                + $"，结果 zoomOk={zoomApplied} focusOk={focusModeApplied}");
            return CaptureLensUiSnapshot(controller);
        }

        /// <summary>
        /// 在后台线程一次读取设备能力与当前值。ONVIF 读取是 HTTP 往返，
        /// 不能等界面挂接完成后再从 UI 线程同步调用。
        /// </summary>
        private static LensUiSnapshot CaptureLensUiSnapshot(ILensController controller)
        {
            if (controller == null)
                return new LensUiSnapshot(null, null, null, null);

            LensRange zoomRange = controller.ZoomRange;
            LensRange focusRange = controller.FocusRange;
            int? zoomValue = null;
            (int Value, bool Auto)? focusState = null;
            try
            {
                if (zoomRange != null)
                    zoomValue = controller.TryReadZoom();
                if (focusRange != null)
                    focusState = controller.TryReadFocusState();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"读取镜头当前值失败，按记忆值或设备默认值显示：{ex.Message}");
            }

            return new LensUiSnapshot(zoomRange, focusRange, zoomValue, focusState);
        }

        /// <summary>按当前控制器与记忆值刷新面板的区间与初值；只在挂接/换设备时调用。</summary>
        private void NotifyLensUiState(
            LensUiSnapshot snapshot,
            bool updateZoom = true,
            bool updateFocus = true)
        {
            _lensUpdatingUiState = true;
            try
            {
                LensRange zoomRange = snapshot.ZoomRange;
                LensRange focusRange = snapshot.FocusRange;

                HasLensZoom = zoomRange is { IsValid: true, SupportsManual: true };
                HasLensFocus = CameraLensPolicy.SupportsManualFocus(focusRange);
                if (updateZoom)
                {
                    IsLensZoomControlEnabled = CameraLensPolicy.CanOperateZoom(
                        zoomRange,
                        snapshot.ZoomValue);
                }
                if (updateFocus)
                {
                    IsLensFocusControlEnabled = CameraLensPolicy.CanOperateFocus(
                        focusRange,
                        snapshot.FocusState);
                }

                bool zoomUnreadable = updateZoom
                    && HasLensZoom
                    && snapshot.ZoomValue == null;
                bool focusUnreadable = updateFocus
                    && HasLensFocus
                    && snapshot.FocusState == null;
                if (zoomUnreadable || focusUnreadable)
                {
                    LensStatusText = AppLanguage.Get(
                        "镜头设置未生效，请重试或使用摄像头厂商软件");
                }
                else if (updateZoom && updateFocus)
                {
                    LensStatusText = "";
                }

                if (zoomRange != null)
                {
                    LensZoomMinimum = zoomRange.Min;
                    LensZoomMaximum = zoomRange.Max;
                    LensZoomTick = Math.Max(1, zoomRange.Step);
                    if (updateZoom)
                    {
                        _lensZoomValue = CameraLensPolicy.ResolveZoomDisplayValue(
                            zoomRange,
                            snapshot.ZoomValue,
                            _lensStored?.Zoom ?? CameraLensSettings.Unset);
                        OnPropertyChanged(nameof(LensZoomValue));
                        OnPropertyChanged(nameof(LensZoomPercentText));
                    }
                }

                if (focusRange != null)
                {
                    LensFocusMinimum = focusRange.Min;
                    LensFocusMaximum = focusRange.Max;
                    LensFocusTick = Math.Max(1, focusRange.Step);

                    if (updateFocus)
                    {
                        // 模式优先信设备当前状态，读不到再按记忆：重连后设备未必回到上次的模式。
                        (int Value, bool Auto)? deviceFocus = snapshot.FocusState;
                        if (deviceFocus != null)
                        {
                            _lensFocusAuto = deviceFocus.Value.Auto;
                            _lensFocusValue = focusRange.Clamp(deviceFocus.Value.Value);
                        }
                        else
                        {
                            _lensFocusAuto = _lensStored?.FocusAuto ?? true;
                            _lensFocusValue = CameraLensPolicy.ResolveFocusDisplayValue(
                                focusRange,
                                null,
                                _lensStored?.Focus ?? CameraLensSettings.Unset);
                        }

                        OnPropertyChanged(nameof(LensFocusAuto));
                        OnPropertyChanged(nameof(IsLensFocusManual));
                        OnPropertyChanged(nameof(LensFocusValue));
                    }
                }

                OnPropertyChanged(nameof(HasLensZoom));
                OnPropertyChanged(nameof(HasLensFocus));
            }
            finally
            {
                _lensUpdatingUiState = false;
            }
        }

        private void ScheduleLensZoomApply()
        {
            EnsureLensApplyTimers();
            _lensZoomApplyTimer.Stop();
            _lensZoomApplyTimer.Start();
        }

        private void ScheduleLensFocusApply()
        {
            EnsureLensApplyTimers();
            _lensFocusApplyTimer.Stop();
            _lensFocusApplyTimer.Start();
        }

        /// <summary>防抖计时器在 UI 线程上建（滑条回调本来就是 UI 线程，这里兜底）。</summary>
        private void EnsureLensApplyTimers()
        {
            if (_lensZoomApplyTimer != null)
                return;

            _lensZoomApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(LensApplyDebounceMilliseconds),
            };
            _lensZoomApplyTimer.Tick += (_, _) => ApplyLensZoomNow();
            _lensFocusApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(LensApplyDebounceMilliseconds),
            };
            _lensFocusApplyTimer.Tick += (_, _) => ApplyLensFocusNow();
        }

        /// <summary>
        /// 控制调用一律放线程池：UVC 是毫秒级 KS 属性，ONVIF 是几十到几百毫秒的 HTTP 往返，
        /// 都不能在防抖计时器（UI 线程）上同步等。
        /// </summary>
        private void ApplyLensZoomNow()
        {
            _lensZoomApplyTimer?.Stop();
            if (!IsLensZoomControlEnabled)
                return;

            ILensController controller = _lensController;
            LensRange zoomRange = controller?.ZoomRange;
            if (controller == null || zoomRange == null)
                return;

            int target = zoomRange.Clamp((int)Math.Round(_lensZoomValue));
            RuntimeLog.Info(
                "Camera",
                $"变焦下发：ui={_lensZoomValue:0.###}, target={target}, range={zoomRange.Min}~{zoomRange.Max}, step={zoomRange.Step}");
            long version = Interlocked.Increment(ref _lensZoomApplyVersion);
            _ = ApplyLensZoomAsync(controller, target, version);
        }

        private void ApplyLensFocusNow()
        {
            _lensFocusApplyTimer?.Stop();
            if (!IsLensFocusControlEnabled)
                return;

            ILensController controller = _lensController;
            LensRange focusRange = controller?.FocusRange;
            if (controller == null || focusRange == null || LensFocusAuto)
                return;

            int target = focusRange.Clamp((int)Math.Round(_lensFocusValue));
            RuntimeLog.Info(
                "Camera",
                $"手动对焦下发：ui={_lensFocusValue:0.###}, target={target}, range={focusRange.Min}~{focusRange.Max}, step={focusRange.Step}");
            long version = Interlocked.Increment(ref _lensFocusApplyVersion);
            _ = ApplyLensFocusAsync(controller, target, version);
        }

        private void ApplyLensFocusMode()
        {
            ILensController controller = _lensController;
            LensRange focusRange = controller?.FocusRange;
            if (controller == null || focusRange == null)
                return;

            bool auto = LensFocusAuto;
            RuntimeLog.Info("Camera", $"对焦模式下发：auto={auto}");
            long version = Interlocked.Increment(ref _lensFocusApplyVersion);
            _ = ApplyLensFocusModeAsync(controller, focusRange, auto, version);
        }

        private async Task ApplyLensZoomAsync(ILensController controller, int target, long version)
        {
            await _lensCommandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool applied = await Task.Run(() => controller.TrySetZoom(target)).ConfigureAwait(false);
                if (!applied)
                {
                    RuntimeLog.Warn("Camera", $"设置变焦失败：value={target}");
                    await HandleLensZoomFailureAsync(
                            controller,
                            version)
                        .ConfigureAwait(false);
                    return;
                }

                RuntimeLog.Info("Camera", $"设置变焦成功：value={target}");
                await RunOnUiThreadAsync(() =>
                {
                    if (ReferenceEquals(_lensController, controller)
                        && version == Volatile.Read(ref _lensZoomApplyVersion))
                    {
                        LensStatusText = "";
                        PersistLensState(zoom: target);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"设置变焦异常：value={target}, error={ex.Message}");
                await HandleLensZoomFailureAsync(
                        controller,
                        version)
                    .ConfigureAwait(false);
            }
            finally
            {
                _lensCommandGate.Release();
            }
        }

        private async Task ApplyLensFocusAsync(ILensController controller, int target, long version)
        {
            await _lensCommandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool applied = await Task.Run(() => controller.TrySetFocusManual(target)).ConfigureAwait(false);
                if (!applied)
                {
                    RuntimeLog.Warn("Camera", $"设置手动对焦失败：value={target}");
                    await HandleLensFocusFailureAsync(
                            controller,
                            version,
                            fallbackAuto: LensFocusAuto)
                        .ConfigureAwait(false);
                    return;
                }

                RuntimeLog.Info("Camera", $"设置手动对焦成功：value={target}");
                await RunOnUiThreadAsync(() =>
                {
                    if (ReferenceEquals(_lensController, controller)
                        && version == Volatile.Read(ref _lensFocusApplyVersion))
                    {
                        LensStatusText = "";
                        PersistLensState(focus: target);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"设置手动对焦异常：value={target}, error={ex.Message}");
                await HandleLensFocusFailureAsync(
                        controller,
                        version,
                        fallbackAuto: LensFocusAuto)
                    .ConfigureAwait(false);
            }
            finally
            {
                _lensCommandGate.Release();
            }
        }

        private async Task ApplyLensFocusModeAsync(
            ILensController controller,
            LensRange focusRange,
            bool auto,
            long version)
        {
            await _lensCommandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                int startValue = 0;
                bool applied = await Task.Run(() =>
                {
                    if (auto)
                        return controller.TrySetFocusAuto();

                    // 切手动：以设备当前对焦位置起步，避免先跳到驱动默认焦。
                    startValue = controller.TryReadFocusState()?.Value ?? focusRange.Default;
                    return controller.TrySetFocusManual(startValue);
                }).ConfigureAwait(false);

                if (!applied)
                {
                    RuntimeLog.Warn("Camera", auto ? "切回自动对焦失败" : "切手动对焦失败");
                    await HandleLensFocusFailureAsync(
                            controller,
                            version,
                            fallbackAuto: !auto)
                        .ConfigureAwait(false);
                    return;
                }

                RuntimeLog.Info(
                    "Camera",
                    auto
                        ? "切回自动对焦成功"
                        : $"切手动对焦成功：value={startValue}");
                await RunOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(_lensController, controller)
                        || version != Volatile.Read(ref _lensFocusApplyVersion))
                    {
                        return;
                    }

                    if (!auto)
                    {
                        _lensUpdatingUiState = true;
                        try
                        {
                            _lensFocusValue = startValue;
                            OnPropertyChanged(nameof(LensFocusValue));
                        }
                        finally
                        {
                            _lensUpdatingUiState = false;
                        }
                    }

                    LensStatusText = "";
                    PersistLensState(focusAuto: auto, focus: auto ? null : startValue);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"切换对焦模式异常：auto={auto}, error={ex.Message}");
                await HandleLensFocusFailureAsync(
                        controller,
                        version,
                        fallbackAuto: !auto)
                    .ConfigureAwait(false);
            }
            finally
            {
                _lensCommandGate.Release();
            }
        }

        private async Task HandleLensZoomFailureAsync(
            ILensController controller,
            long version)
        {
            int? actual = null;
            try
            {
                actual = controller.TryReadZoom();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"变焦失败后回读状态异常：{ex.Message}");
            }

            await RunOnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_lensController, controller)
                    && version == Volatile.Read(ref _lensZoomApplyVersion))
                {
                    if (actual.HasValue)
                    {
                        _lensUpdatingUiState = true;
                        try
                        {
                            _lensZoomValue = actual.Value;
                            OnPropertyChanged(nameof(LensZoomValue));
                            OnPropertyChanged(nameof(LensZoomPercentText));
                        }
                        finally
                        {
                            _lensUpdatingUiState = false;
                        }
                    }
                    else
                    {
                        IsLensZoomControlEnabled = false;
                    }

                    LensStatusText = AppLanguage.Get(
                        "镜头设置未生效，请重试或使用摄像头厂商软件");
                }
            }).ConfigureAwait(false);
        }

        private async Task HandleLensFocusFailureAsync(
            ILensController controller,
            long version,
            bool fallbackAuto)
        {
            (int Value, bool Auto)? actual;
            try
            {
                actual = controller.TryReadFocusState();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"对焦失败后回读状态异常：{ex.Message}");
                actual = null;
            }

            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_lensController, controller)
                    || version != Volatile.Read(ref _lensFocusApplyVersion))
                {
                    return;
                }

                _lensUpdatingUiState = true;
                try
                {
                    _lensFocusAuto = actual?.Auto ?? fallbackAuto;
                    if (actual != null)
                        _lensFocusValue = controller.FocusRange!.Clamp(actual.Value.Value);
                    else
                        IsLensFocusControlEnabled = false;
                    OnPropertyChanged(nameof(LensFocusAuto));
                    OnPropertyChanged(nameof(IsLensFocusManual));
                    OnPropertyChanged(nameof(LensFocusValue));
                }
                finally
                {
                    _lensUpdatingUiState = false;
                }

                LensStatusText = AppLanguage.Get(
                    "镜头设置未生效，请重试或使用摄像头厂商软件");
            }).ConfigureAwait(false);
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            Func<Action, Task> invoker = _uiThreadInvoker ?? RunOnApplicationUiThreadAsync;
            return invoker(action);
        }

        private static Task RunOnApplicationUiThreadAsync(Action action)
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private static string SanitizeLensKeyForLog(string configKey)
        {
            const string networkPrefix = "network:";
            return configKey.StartsWith(networkPrefix, StringComparison.Ordinal)
                ? networkPrefix + NetworkCameraUrlPolicy.SanitizeForLog(configKey[networkPrefix.Length..])
                : configKey;
        }

        /// <summary>把镜头状态记到当前摄像头的名下并落盘；传 null 的项保持原值。</summary>
        private void PersistLensState(int? zoom = null, bool? focusAuto = null, int? focus = null)
        {
            AppConfig config = Config;
            if (config == null || string.IsNullOrEmpty(_lensConfigKey))
                return;

            CameraLensSettings stored = _lensStored;
            if (stored == null)
            {
                stored = new CameraLensSettings();
                _lensStored = stored;
            }

            if (zoom != null)
                stored.Zoom = zoom.Value;
            if (focusAuto != null)
                stored.FocusAuto = focusAuto.Value;
            if (focus != null)
                stored.Focus = focus.Value;

            config.CameraLensStates[_lensConfigKey] = stored;
            SaveConfig(notifyUser: false);
        }
    }
}
