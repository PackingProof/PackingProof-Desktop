using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 叠加画面（画中画）：配置里有几路就起几路，各自独立采集，再统一叠到主画面上。
    ///
    /// 只做"采集 + 合成进同一帧"，不新建发布管线：叠加画面画进录像与预览共用的那一帧之后，
    /// 预览、录像、缩略图天然一致，也不会出现"预览里有、录像里没有"的分叉。
    /// 叠加画面不参与运动检测、不写预录缓冲区，掉线也不重启主路。
    ///
    /// 通道号从 1 起（1 = 副画面 1），对应 <see cref="AppConfig.CameraChannels"/> 的下标减一。
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>一路叠加画面的运行时状态：采集源、最新帧、合成与落位记录。</summary>
        private sealed class OverlayChannel
        {
            internal OverlayChannel(int number, CameraChannelConfig config)
            {
                Number = number;
                Config = config;
            }

            /// <summary>通道号：1 = 副画面 1。识别来源、设置页与日志都用它。</summary>
            internal int Number { get; }

            internal CameraChannelConfig Config { get; }

            /// <summary>采集源启动/停止的串行锁。</summary>
            internal readonly object SourceLock = new();

            /// <summary>
            /// 帧取用锁：实时帧由 VideoProcessLoop 合成、预录帧由录像写线程合成，
            /// 两个线程都会从槽里取走这一路的帧，必须串行，否则会出现"帧已被释放还在读"。
            /// </summary>
            internal readonly object OverlayLock = new();

            /// <summary>这一路最新帧的交接槽：被顶掉的旧帧由槽自己释放。</summary>
            internal readonly LatestFrameHandoffSlot<Mat> LatestFrame = new();

            internal VideoCaptureDevice? UsbSource;
            internal NewFrameEventHandler? UsbFrameHandler;
            internal VideoSourceErrorEventHandler? UsbErrorHandler;
            internal MfCameraSource? MfSource;
            internal EventHandler<MfFrameEventArgs>? MfFrameHandler;
            internal EventHandler<MfSourceErrorEventArgs>? MfErrorHandler;
            internal NetworkCameraSource? NetworkSource;
            internal EventHandler<NetworkCameraFrameEventArgs>? NetworkFrameHandler;
            internal EventHandler<NetworkCameraErrorEventArgs>? NetworkErrorHandler;

            /// <summary>合成用的这一路帧（谁持有谁释放）。</summary>
            internal Mat? OverlayFrame;

            /// <summary>
            /// 最新帧尺寸 + 上一帧**实际合成**的落位（主帧坐标系）。合成线程整体换一份，
            /// 界面线程直接读一份快照：拖动框按同一套尺寸与落位换算，框和画面才会必然重合。
            /// </summary>
            internal OverlayGeometrySnapshot Geometry = OverlayGeometrySnapshot.Empty;

            /// <summary>最近这次合成失败是否已经落过日志：连续失败是每帧都进 catch 的，不能每帧写一条。</summary>
            internal bool ComposeFailureLogged;

            /// <summary>
            /// 启动后"到底有没有画面"的观察令牌。每次启动/停止都换一个，
            /// 迟到的旧观察直接失效，不会对已经换过的设备报错。
            /// </summary>
            internal CancellationTokenSource? FrameWatchCts;

            internal bool HasFrame;
            internal System.Windows.Media.Imaging.BitmapSource? PreviewFrame;
            internal DateTime LastPreviewPublishedAt = DateTime.MinValue;

            /// <summary>出帧统计：副画面看着卡时，先分清是相机没给够帧，还是我们画丢了。</summary>
            internal int StatsFrames;
            internal long StatsWindowStartTicks = Stopwatch.GetTimestamp();
            internal long StatsLastFrameTicks;
            internal double StatsMaxGapMs;
            internal int StatsQuietWindows;

            /// <summary>这一路当前是否已启动。</summary>
            internal bool IsRunning => UsbSource != null || MfSource != null || NetworkSource != null;
        }

        /// <summary>
        /// 合成线程写入、界面线程读取的一组状态，整体替换。
        ///
        /// 原来拆成"最新帧尺寸 / 上次落位 / 上次落位那一帧的尺寸"三个字段各自更新：
        /// 界面正好落在两次写之间时，会拿新尺寸配旧落位算出一个错误的换算比例，拖动框跳一下。
        /// 收成一份快照之后，读的一侧要么看到换之前的一份、要么看到换之后的一份。
        /// </summary>
        internal readonly record struct OverlayGeometrySnapshot(
            CameraOverlayRect? ComposedRect,
            int ComposedFrameWidth,
            int ComposedFrameHeight,
            int SourceWidth,
            int SourceHeight)
        {
            internal static readonly OverlayGeometrySnapshot Empty = new(null, 0, 0, 0, 0);

            /// <summary>换了最新帧尺寸：落位那一半原样带过来，不能丢。</summary>
            internal OverlayGeometrySnapshot WithSourceSize(int width, int height) =>
                new(ComposedRect, ComposedFrameWidth, ComposedFrameHeight, width, height);

            /// <summary>换了这一帧的落位：帧尺寸那一半原样带过来，不能丢。</summary>
            internal OverlayGeometrySnapshot WithComposition(
                CameraOverlayRect rect,
                int frameWidth,
                int frameHeight) =>
                new(rect, frameWidth, frameHeight, SourceWidth, SourceHeight);
        }

        /// <summary>
        /// 当前各路的运行时列表。重建时**整体换一份新的**，不再就地增删。
        ///
        /// 这条列表在设置变更时于 UI 线程重建，而处理循环线程与副摄采集线程每帧都要遍历它：
        /// 原来的 List.Clear()/Add() 会被读成半成品，且处理循环看到空列表时也会去重建，
        /// 两边同时重建就可能留下重复通道（同一台设备被开两次，画中画直接没有画面）。
        /// 数组引用赋值本身是原子的，读的一侧因此不需要取锁，看到的永远是完整的一份。
        /// </summary>
        private OverlayChannel[] _overlayChannels = Array.Empty<OverlayChannel>();

        /// <summary>
        /// 只保护"比对 + 整体替换列表"这一下。重建里的停旧流是慢动作（Media Foundation 一次约 1.8 秒），
        /// 必须放在锁外，否则处理循环会卡在等锁上，预览跟着停。
        /// </summary>
        private readonly object _overlayChannelSyncLock = new();

        private int _overlayPlacementVersion;

        /// <summary>
        /// 识别框是否显示。框只出现在识别来源那一路：
        /// 来源是主摄时画在主画面上；来源是叠加画面时贴到画中画上（画中画就是框内那块裁剪结果），
        /// 这样绿/黄识别状态与提示文字才有地方显示。
        /// </summary>
        public bool IsBarcodeGuideVisible => true;

        /// <summary>
        /// 框上的小锁只在"这个框能编辑"的时候出现：主摄取景、或叠加画面的取景编辑屏。
        /// 识别来源是叠加画面时，框只是画中画上的状态反馈（取景在"点画中画"的编辑屏里改），不显示锁。
        /// </summary>
        public bool IsCameraBarcodeGuideLockVisible =>
            !ShouldUseOverlayChannelForBarcode || IsEditingOverlayPreview;

        /// <summary>
        /// 叠加画面实际落位的版本号。任一路的落位一变就自增并通知界面重摆拖动框 ——
        /// 叠加画面是直接画进帧里的，界面那个拖动框平时不跟着每帧走，
        /// 不在这里通知就会停在上一帧的位置，看起来就是"框和画面对不上"。
        /// </summary>
        public int OverlayPlacementVersion => _overlayPlacementVersion;

        private void NotifyOverlayPlacementChanged()
        {
            // 处理循环和副摄采集线程都会调这里，普通的 ++ 会丢更新，界面就漏掉一次重摆。
            Interlocked.Increment(ref _overlayPlacementVersion);
            OnPropertyChanged(nameof(OverlayPlacementVersion));
        }

        /// <summary>配置里是否至少接了一路叠加画面。</summary>
        internal bool HasConfiguredOverlayChannels =>
            Config is { } config
            && config.CameraChannels.Any(channel => channel.IsConfigured);

        /// <summary>任意一路出过帧：界面据此决定什么时候可以摆拖动框。</summary>
        internal bool HasOverlayFrame => _overlayChannels.Any(channel => channel.HasFrame);

        /// <summary>
        /// 需要显示拖动框的通道号（1 起）：配置里接了设备的每一路都要有一个框，
        /// 点哪个框就进哪一路的取景编辑。以后开放第三、第四路时界面不用再改。
        /// </summary>
        internal IReadOnlyList<int> VisibleOverlayChannelNumbers
        {
            get
            {
                if (Config is not { } config)
                    return Array.Empty<int>();

                var numbers = new List<int>();
                for (int i = 0; i < config.CameraChannels.Count; i++)
                {
                    if (config.CameraChannels[i].IsConfigured)
                        numbers.Add(i + 1);
                }

                return numbers;
            }
        }

        /// <summary>叠加画面是否正在显示，供主界面的拖动框显隐使用。</summary>
        internal bool IsOverlayVisible => HasConfiguredOverlayChannels;

        /// <summary>按通道号取运行时；找不到（或还没配置）返回 null。</summary>
        private OverlayChannel? FindOverlayChannel(int channelNumber) =>
            _overlayChannels.FirstOrDefault(channel => channel.Number == channelNumber);

        /// <summary>
        /// 用户没拖过时贴哪一角：第 1 路右下（沿用老习惯）、第 2 路左下、第 3 路右上、第 4 路左上，
        /// 四路各占一角不会互相叠住。超过四路时按奇偶继续轮流贴下面两角。
        /// </summary>
        private static CameraOverlayAnchor OverlayAnchorFor(OverlayChannel channel) =>
            channel.Number switch
            {
                1 => CameraOverlayAnchor.BottomRight,
                2 => CameraOverlayAnchor.BottomLeft,
                3 => CameraOverlayAnchor.TopRight,
                4 => CameraOverlayAnchor.TopLeft,
                _ => channel.Number % 2 == 0
                    ? CameraOverlayAnchor.BottomLeft
                    : CameraOverlayAnchor.BottomRight,
            };

        /// <summary>
        /// 按当前配置对齐运行时通道：路数或配置对象变了就整组重建（设置页应用配置会换一份 AppConfig）。
        /// 重建前先把旧通道全部停掉，避免换设备时两路抢同一台。
        /// </summary>
        private void SyncOverlayChannelRuntimes()
        {
            OverlayChannel[] stale;
            lock (_overlayChannelSyncLock)
            {
                if (Config is not { } config)
                {
                    if (_overlayChannels.Length == 0)
                        return;

                    stale = _overlayChannels;
                    _overlayChannels = Array.Empty<OverlayChannel>();
                }
                else
                {
                    if (OverlayChannelsMatchConfigLocked(config))
                        return;

                    // 先在局部拼好新的一份，再整体替换：读的一侧不会看到"清空了一半"的列表。
                    var rebuilt = new OverlayChannel[config.CameraChannels.Count];
                    for (int i = 0; i < rebuilt.Length; i++)
                        rebuilt[i] = new OverlayChannel(i + 1, config.CameraChannels[i]);

                    stale = _overlayChannels;
                    _overlayChannels = rebuilt;
                }
            }

            // 停旧通道放在锁外：换列表这一下已经做完，处理循环可以直接按新的一份跑。
            foreach (OverlayChannel channel in stale)
                StopOverlayChannel(channel);
        }

        /// <summary>调用方必须持有 <see cref="_overlayChannelSyncLock"/>。</summary>
        private bool OverlayChannelsMatchConfigLocked(AppConfig config)
        {
            OverlayChannel[] current = _overlayChannels;
            if (current.Length != config.CameraChannels.Count)
                return false;

            for (int i = 0; i < current.Length; i++)
            {
                if (!ReferenceEquals(current[i].Config, config.CameraChannels[i]))
                    return false;
            }

            return true;
        }

        /// <summary>这一路是否还在当前列表里：启动是慢动作，启动期间被换掉的话不能再把设备攥在手里。</summary>
        private bool IsCurrentOverlayChannel(OverlayChannel channel) =>
            Array.IndexOf(_overlayChannels, channel) >= 0;

        /// <summary>
        /// 启动所有接了设备的叠加画面。任何一路失败都只记日志/提示，绝不影响主路与其它路——
        /// 叠加画面是增强项，不能因为它连不上就把主录像拖下水。
        /// </summary>
        internal void StartOverlayChannels()
        {
            SyncOverlayChannelRuntimes();

            // 副画面刚起来之前采到的那几秒预录帧里是**没有**副画面的。
            // 留着就会在录像开头留一段"副画面忽有忽无"的接缝 —— 现场看到的就是
            // "副摄像头画面直接没了"。这里把预录缓冲重新攒：宁可从更短的一段开始，
            // 也不要留下半截画面。（设置里换设备、休眠唤醒都属于这种重新启动。）
            if (HasConfiguredOverlayChannels && !_overlayChannels.Any(channel => channel.IsRunning))
                ClearPreRecordBuffer();

            // 按这一份快照启动；启动一路要开设备（秒级），期间列表可能被换成新的一份，
            // 换掉的这一路不能再留着设备不放，否则那台相机就被一个已经不在列表里的通道占着。
            foreach (OverlayChannel channel in _overlayChannels)
            {
                StartOverlayChannel(channel);
                if (!IsCurrentOverlayChannel(channel))
                    StopOverlayChannel(channel);
            }
        }

        /// <summary>停止所有叠加画面并释放各自缓存的帧。可重复调用。</summary>
        internal void StopOverlayChannels()
        {
            foreach (OverlayChannel channel in _overlayChannels)
                StopOverlayChannel(channel);
        }

        /// <summary>按当前配置重启全部叠加画面：设置里换设备、换来源或换地址后调用。</summary>
        internal void RestartOverlayChannels()
        {
            StopOverlayChannels();
            StartOverlayChannels();
        }

        private void StartOverlayChannel(OverlayChannel channel)
        {
            if (!channel.Config.IsConfigured)
            {
                StopOverlayChannel(channel);
                return;
            }

            lock (channel.SourceLock)
            {
                if (channel.IsRunning)
                    return;

                try
                {
                    if (IsNetworkChannelConfigured(channel))
                        StartNetworkChannel(channel);
                    else
                        StartUsbChannel(channel);
                    // 打开设备不代表有画面：USB 摄像头被别的程序占用、两路选了同一台设备、
                    // 网络地址挂着一台不存在的相机，都会"启动成功但一帧都不给"。
                    // 最终以出帧为准，所以启动后观察一段时间。
                    ScheduleOverlayFrameWatch(channel);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("OverlayChannel", $"启动第 {channel.Number} 路叠加画面失败：{ex.Message}");
                    StopOverlayChannelCore(channel);
                }
            }
        }

        private void StopOverlayChannel(OverlayChannel channel)
        {
            lock (channel.SourceLock)
            {
                StopOverlayChannelCore(channel);
            }
        }

        private void StopOverlayChannelCore(OverlayChannel channel)
        {
            VideoCaptureDevice? usb = channel.UsbSource;
            MfCameraSource? mf = channel.MfSource;
            NetworkCameraSource? network = channel.NetworkSource;
            channel.UsbSource = null;
            channel.MfSource = null;
            channel.NetworkSource = null;

            CancellationTokenSource? watch = channel.FrameWatchCts;
            channel.FrameWatchCts = null;
            if (watch != null)
            {
                try { watch.Cancel(); } catch { }
                try { watch.Dispose(); } catch { }
            }

            if (usb != null)
            {
                try
                {
                    if (channel.UsbFrameHandler != null)
                        usb.NewFrame -= channel.UsbFrameHandler;
                    if (channel.UsbErrorHandler != null)
                        usb.VideoSourceError -= channel.UsbErrorHandler;
                    if (usb.IsRunning)
                    {
                        usb.SignalToStop();
                        usb.WaitForStop();
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("OverlayChannel", $"停止第 {channel.Number} 路 USB 摄像头失败：{ex.Message}");
                }
            }

            if (network != null)
            {
                try
                {
                    if (channel.NetworkFrameHandler != null)
                        network.FrameReady -= channel.NetworkFrameHandler;
                    if (channel.NetworkErrorHandler != null)
                        network.SourceError -= channel.NetworkErrorHandler;
                    network.Stop();
                    network.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("OverlayChannel", $"停止第 {channel.Number} 路网络摄像头失败：{ex.Message}");
                }
            }

            if (mf != null)
            {
                try
                {
                    if (channel.MfFrameHandler != null)
                        mf.FrameReady -= channel.MfFrameHandler;
                    if (channel.MfErrorHandler != null)
                        mf.SourceError -= channel.MfErrorHandler;
                    mf.Stop();
                    mf.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("OverlayChannel", $"停止第 {channel.Number} 路 Media Foundation 摄像头失败：{ex.Message}");
                }
            }

            channel.UsbFrameHandler = null;
            channel.UsbErrorHandler = null;
            channel.MfFrameHandler = null;
            channel.MfErrorHandler = null;
            channel.NetworkFrameHandler = null;
            channel.NetworkErrorHandler = null;

            channel.LatestFrame.Clear();
            lock (channel.OverlayLock)
            {
                DisposeOverlayFrame(channel);
                channel.Geometry = OverlayGeometrySnapshot.Empty;
            }

            // 这一路停了，主界面的拖动框要跟着消失，等重新出帧再出现。
            SetOverlayFrameFlag(channel, false);
        }

        private bool IsNetworkChannelConfigured(OverlayChannel channel) =>
            string.Equals(channel.Config.SourceKind, "network", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(channel.Config.NetworkCameraUrl);

        private void StartNetworkChannel(OverlayChannel channel)
        {
            if (!NetworkCameraUrlPolicy.TryNormalize(
                    channel.Config.NetworkCameraUrl,
                    out string url,
                    out string error))
            {
                RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路网络摄像头地址无效：{error}");
                ShowToast($"副画面 {channel.Number} 的网络摄像头地址无效：{error}", ToastSeverity.Error);
                return;
            }

            // 与主路或别的叠加路同一个地址，等于两个 ffmpeg 进程拉同一路流，纯浪费带宽和解码。
            if (IsNetworkUrlUsedElsewhere(channel, url))
            {
                RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路网络摄像头地址与其它路相同，已跳过");
                ShowToast($"副画面 {channel.Number} 不能和其它路用同一个网络摄像头地址", ToastSeverity.Warning);
                return;
            }

            int fps = channel.Config.FrameFps > 0
                ? channel.Config.FrameFps
                : AppConfig.DefaultOverlayFrameFps;
            var source = new NetworkCameraSource(url, channel.Config.NetworkCameraRtspTransport, fps);
            var frameHandler = new EventHandler<NetworkCameraFrameEventArgs>(
                (_, e) => OnNetworkChannelFrame(channel, e));
            var errorHandler = new EventHandler<NetworkCameraErrorEventArgs>(
                (_, e) => RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路网络摄像头错误：{e.Description}"));
            channel.NetworkFrameHandler = frameHandler;
            channel.NetworkErrorHandler = errorHandler;
            source.FrameReady += frameHandler;
            source.SourceError += errorHandler;

            if (!source.Start())
            {
                RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路网络摄像头连接失败：{source.LastError}");
                ShowToast($"副画面 {channel.Number} 的网络摄像头连接失败：{source.LastError}", ToastSeverity.Warning);
                source.FrameReady -= frameHandler;
                source.SourceError -= errorHandler;
                channel.NetworkFrameHandler = null;
                channel.NetworkErrorHandler = null;
                source.Dispose();
                return;
            }

            channel.NetworkSource = source;
            RuntimeLog.Info(
                "OverlayChannel",
                $"第 {channel.Number} 路网络摄像头已启动 url={NetworkCameraUrlPolicy.SanitizeForLog(url)}, "
                    + $"transport={channel.Config.NetworkCameraRtspTransport}");
        }

        /// <summary>这个网络地址是不是已经被主摄或别的叠加路占用。</summary>
        private bool IsNetworkUrlUsedElsewhere(OverlayChannel channel, string url)
        {
            if (NetworkCameraUrlPolicy.TryNormalize(Config?.NetworkCameraUrl, out string primaryUrl, out _)
                && string.Equals(primaryUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (OverlayChannel other in _overlayChannels)
            {
                if (ReferenceEquals(other, channel))
                    continue;
                if (!string.Equals(other.Config.SourceKind, "network", StringComparison.Ordinal))
                    continue;
                if (NetworkCameraUrlPolicy.TryNormalize(other.Config.NetworkCameraUrl, out string otherUrl, out _)
                    && string.Equals(otherUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void StartUsbChannel(OverlayChannel channel)
        {
            var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            if (devices.Count == 0)
            {
                RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路启动失败：没有检测到视频设备");
                return;
            }

            string selectedMoniker = ResolveOverlayChannelMoniker(channel, devices);
            if (selectedMoniker.Length == 0)
                return;

            // 先走新后端：原生格式协商 + GPU 解码与色彩校正，与主摄同一条路径。
            // 软件虚拟摄像头（MF 枚举不到）、驱动异常或探测不通过时再回退 DirectShow。
            if (TryStartMediaFoundationChannel(channel, selectedMoniker))
            {
                channel.Config.MonikerString = selectedMoniker;
                return;
            }

            var source = new VideoCaptureDevice(selectedMoniker);
            var frameHandler = new NewFrameEventHandler((_, e) => OnUsbChannelFrame(channel, e));
            var errorHandler = new VideoSourceErrorEventHandler(
                (_, e) => RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路摄像头发送错误：{e.Description}"));
            channel.UsbFrameHandler = frameHandler;
            channel.UsbErrorHandler = errorHandler;
            source.NewFrame += frameHandler;
            source.VideoSourceError += errorHandler;
            source.Start();
            channel.UsbSource = source;
            channel.Config.MonikerString = selectedMoniker;

            RuntimeLog.Info("OverlayChannel", $"第 {channel.Number} 路摄像头已启动（DirectShow）");
        }

        /// <summary>
        /// 解析这一路要打开的设备。
        ///
        /// **不做"随便回落一台"**：以前在没配过或设备拔掉时会退到索引 1/0，结果经常正好是主摄那一台，
        /// 两路抢同一台设备 —— 表现就是"主副冲突"提示和叠加画面永远没有图像。
        /// 现在解析不到就返回空，由调用方直接不启动这一路。
        /// </summary>
        private string ResolveOverlayChannelMoniker(OverlayChannel channel, FilterInfoCollection devices)
        {
            string mainMoniker = Config?.CameraMonikerString ?? "";
            string configured = channel.Config.MonikerString ?? "";
            if (configured.Length > 0)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (!string.Equals(devices[i].MonikerString, configured, StringComparison.Ordinal))
                        continue;
                    if (IsMonikerUsedElsewhere(channel, configured))
                        break;
                    return configured;
                }

                RuntimeLog.Info(
                    "OverlayChannel",
                    $"第 {channel.Number} 路配置的摄像头当前不在（或已被别的路占用），先不启动");
                return "";
            }

            int index = channel.Config.Index;
            if (index >= 0 && index < devices.Count)
            {
                string moniker = devices[index].MonikerString;
                if (!IsMonikerUsedElsewhere(channel, moniker))
                    return moniker;
            }

            RuntimeLog.Info("OverlayChannel", $"第 {channel.Number} 路还没有选定设备，先不启动");
            return "";
        }

        /// <summary>这台设备是不是已经被主摄或别的叠加路占用。</summary>
        private bool IsMonikerUsedElsewhere(OverlayChannel channel, string moniker) =>
            string.Equals(moniker, Config?.CameraMonikerString ?? "", StringComparison.Ordinal)
            || _overlayChannels.Any(other =>
                !ReferenceEquals(other, channel)
                && string.Equals(other.Config.MonikerString, moniker, StringComparison.Ordinal));

        /// <summary>
        /// 叠加画面的 Media Foundation 启动路径，与主摄同一套：直接协商原生 YUY2/NV12，
        /// 交给 GPU 做解码、色彩校正与缩放，不走 DirectShow 的系统转换器。
        /// 任何一步不成立都返回 false，由调用方回退 DirectShow。
        /// </summary>
        private bool TryStartMediaFoundationChannel(OverlayChannel channel, string monikerString)
        {
            if (Config is not { } config)
                return false;
            if (CameraBackendPolicy.IsMediaFoundationDisabled(config.CameraBackend))
                return false;

            try
            {
                using MfPlatform? platform = MfPlatform.TryStart();
                if (platform == null)
                    return false;

                MfCaptureDevice? device = MfDeviceMatcher.FindByMoniker(
                    monikerString,
                    MfCaptureDevice.Enumerate());
                if (device == null)
                    return false;

                (int width, int height) = AppConfig.ResolveOverlayFrameSize(
                    channel.Config.ResolutionPreset,
                    channel.Config.FrameWidth,
                    channel.Config.FrameHeight);
                int fps = channel.Config.FrameFps > 0
                    ? channel.Config.FrameFps
                    : AppConfig.DefaultOverlayFrameFps;

                MfCaptureProbe.Result probe = MfCaptureProbe.Probe(
                    device.SymbolicLink,
                    width,
                    height,
                    fps,
                    config.CameraColorMatrix);
                if (CameraBackendPolicy.Decide(config.CameraBackend, probe.Usable)
                    != CameraBackendKind.MediaFoundation)
                {
                    RuntimeLog.Info(
                        "OverlayChannel",
                        $"第 {channel.Number} 路 Media Foundation 后端不可用（{probe.Failure}），改用 DirectShow 后端");
                    return false;
                }

                var source = new MfCameraSource(
                    device.SymbolicLink,
                    width,
                    height,
                    fps,
                    config.CameraColorMatrix,
                    channel.Config.RotationDegrees);
                var frameHandler = new EventHandler<MfFrameEventArgs>(
                    (_, e) => OnMfChannelFrame(channel, e));
                var errorHandler = new EventHandler<MfSourceErrorEventArgs>(
                    (_, e) => RuntimeLog.Warn(
                        "OverlayChannel",
                        $"第 {channel.Number} 路 Media Foundation 采集错误：{e.Description}（deviceLost={e.DeviceLost}）"));
                channel.MfFrameHandler = frameHandler;
                channel.MfErrorHandler = errorHandler;
                source.FrameReady += frameHandler;
                source.SourceError += errorHandler;
                if (!source.Start())
                {
                    source.FrameReady -= frameHandler;
                    source.SourceError -= errorHandler;
                    channel.MfFrameHandler = null;
                    channel.MfErrorHandler = null;
                    source.Dispose();
                    RuntimeLog.Warn(
                        "OverlayChannel",
                        $"第 {channel.Number} 路 Media Foundation 探测通过但启动失败（{source.LastStartFailure}），改用 DirectShow 后端");
                    return false;
                }

                channel.MfSource = source;
                RuntimeLog.Info(
                    "OverlayChannel",
                    $"第 {channel.Number} 路已启动（Media Foundation）{source.ActualWidth}x{source.ActualHeight}@{source.ActualFps:F0}"
                        + $"，格式={source.ActualFormat}，bt709={source.UsesBt709}"
                        + $"，configured={width}x{height}@{fps}");
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "OverlayChannel",
                    $"第 {channel.Number} 路 Media Foundation 启动异常，改用 DirectShow：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 新后端的帧到达。帧已经是 BGR24，所有权交给订阅方，后续处理与 DirectShow 路径一致。
        /// </summary>
        private void OnMfChannelFrame(OverlayChannel channel, MfFrameEventArgs e)
        {
            Mat? frame = null;
            try
            {
                frame = e.Frame;
                if (frame == null || frame.Empty())
                {
                    frame?.Dispose();
                    return;
                }

                // 旋转同样在采集层完成（MF 用 GPU 着色器，AForge 回退在帧回调里转），这里不再转一次。
                TrySubmitOverlayBarcodeFrame(channel, frame);
                PublishOverlayFrame(channel, frame);
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                RuntimeLog.Error("OverlayChannel", $"第 {channel.Number} 路 Media Foundation 帧处理失败", ex);
            }
        }

        private void OnUsbChannelFrame(OverlayChannel channel, NewFrameEventArgs eventArgs)
        {
            Mat? frame = null;
            try
            {
                frame = CameraFrameConverter.ConvertToBgrMat(eventArgs.Frame);
                frame = CameraFrameOrientation.Apply(frame, channel.Config.RotationDegrees);
                TrySubmitOverlayBarcodeFrame(channel, frame);
                PublishOverlayFrame(channel, frame);
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                RuntimeLog.Error("OverlayChannel", $"第 {channel.Number} 路摄像头帧转换失败", ex);
            }
        }

        private void OnNetworkChannelFrame(OverlayChannel channel, NetworkCameraFrameEventArgs e)
        {
            Mat? frame = null;
            try
            {
                frame = e.Frame;
                if (frame == null || frame.Empty())
                {
                    frame?.Dispose();
                    return;
                }

                frame = CameraFrameOrientation.Apply(frame, channel.Config.RotationDegrees);
                TrySubmitOverlayBarcodeFrame(channel, frame);
                PublishOverlayFrame(channel, frame);
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                RuntimeLog.Error("OverlayChannel", $"第 {channel.Number} 路摄像头帧处理失败", ex);
            }
        }

        /// <summary>整帧所有权交给槽：被顶掉的旧帧由槽自己释放。</summary>
        private void PublishOverlayFrame(OverlayChannel channel, Mat frame)
        {
            TrackOverlayFrameRate(channel);
            // 编辑态下顺便出一张预览位图；三种采集回调都经过这里，不必各自处理。
            PublishOverlayPreviewFrameIfDue(channel, frame);
            channel.LatestFrame.Publish(frame);
        }

        /// <summary>
        /// 每 10 秒给这一路落一条出帧统计：多少帧、平均 fps、最大间隔。
        /// 副画面在录像里"卡"的时候，这条日志能直接分清是相机没给够帧，还是我们合成时把它丢了。
        /// </summary>
        private void TrackOverlayFrameRate(OverlayChannel channel)
        {
            long now = Stopwatch.GetTimestamp();
            if (channel.StatsLastFrameTicks > 0)
            {
                double gapMs = (now - channel.StatsLastFrameTicks) * 1000.0 / Stopwatch.Frequency;
                if (gapMs > channel.StatsMaxGapMs)
                    channel.StatsMaxGapMs = gapMs;
            }

            channel.StatsLastFrameTicks = now;
            channel.StatsFrames++;

            double windowSeconds = (now - channel.StatsWindowStartTicks) / (double)Stopwatch.Frequency;
            if (windowSeconds < 10)
                return;

            double averageFps = channel.StatsFrames / windowSeconds;
            int expectedFps = channel.Config.FrameFps;
            bool abnormal = (expectedFps > 0 && averageFps < expectedFps * 0.8) || channel.StatsMaxGapMs > 150;

            // 正常时每 60 秒汇总一条（不刷屏），掉帧或长时间没出帧立刻落一条。
            if (!abnormal && ++channel.StatsQuietWindows < 6)
            {
                channel.StatsWindowStartTicks = now;
                channel.StatsFrames = 0;
                channel.StatsMaxGapMs = 0;
                return;
            }

            RuntimeLog.Info(
                "OverlayChannel",
                $"第 {channel.Number} 路出帧{(abnormal ? "偏慢" : "")}：{channel.StatsFrames} 帧 / {windowSeconds:F1} 秒"
                + $"（平均 {averageFps:F1} fps，期望 {expectedFps} fps，最大间隔 {channel.StatsMaxGapMs:F0} ms）");

            channel.StatsWindowStartTicks = now;
            channel.StatsFrames = 0;
            channel.StatsMaxGapMs = 0;
            channel.StatsQuietWindows = 0;
        }

        /// <summary>
        /// 接了的每一路副画面当前都有一帧可用（合成时贴得上）。
        /// 只给诊断用：不加锁读一次，够判断"这一帧到底有没有副画面"。
        /// </summary>
        private bool AllConfiguredOverlayChannelsHaveFrame =>
            _overlayChannels
                .Where(channel => channel.Config.IsConfigured)
                .All(channel => channel.OverlayFrame is { IsDisposed: false } frame && !frame.Empty());

        /// <summary>
        /// 启动后观察一段时间：打开设备成功不等于有画面。叠加画面最常见的失败形态是
        /// "与主摄是同一台设备被独占""被其它程序占用""网络地址挂着不存在的相机"，这些都不会抛异常。
        /// 这里只提示用户、不动主路、也不自动换设备。
        /// </summary>
        private void ScheduleOverlayFrameWatch(OverlayChannel channel)
        {
            CancellationTokenSource? previous = channel.FrameWatchCts;
            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
                try { previous.Dispose(); } catch { }
            }

            var cts = new CancellationTokenSource();
            channel.FrameWatchCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(6), cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested || channel.HasFrame || !channel.IsRunning)
                        return;

                    RuntimeLog.Warn(
                        "OverlayChannel",
                        $"第 {channel.Number} 路叠加画面启动后 6 秒没有画面：可能与主摄是同一台设备被独占、"
                            + "被其它程序占用，或地址不可用");
                    System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
                    dispatcher?.BeginInvoke(new Action(() => ShowToast(
                        $"副画面 {channel.Number} 没有画面，请检查它是否与主摄冲突、被其它程序占用或地址不可用",
                        ToastSeverity.Warning)));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("OverlayChannel", $"第 {channel.Number} 路出帧观察失败：{ex.Message}");
                }
            });
        }

        /// <summary>
        /// 处理循环里调用：把每一路叠加画面叠进这一帧。
        /// 只有"要录像"或"这一帧真的要发布预览"时才动手，空闲降档时不白做缩放与拷贝。
        /// </summary>
        internal void ComposeOverlayChannelsIfNeeded(Mat frame, bool previewPublishDue)
        {
            if (_overlayChannels.Length == 0)
                SyncOverlayChannelRuntimes();

            // 重建可能刚好插在这一次调用中间，取一份快照按它跑完：这一帧要么全用旧的一份、要么全用新的一份。
            OverlayChannel[] channels = _overlayChannels;

            if (!HasConfiguredOverlayChannels)
            {
                foreach (OverlayChannel channel in channels)
                {
                    lock (channel.OverlayLock)
                    {
                        DisposeOverlayFrame(channel);
                    }
                }
                return;
            }

            if (!IsRecording && !previewPublishDue)
                return;

            ComposeOverlayChannels(frame);
        }

        /// <summary>
        /// 不看录像/预览状态，直接把每一路叠加画面叠进这一帧。
        /// 预录帧进环形缓存时用它：副画面要跟着这一帧的采集时刻走，
        /// 不能等到回灌时再贴 —— 那时候只能拿到"回灌那一刻"的叠加帧，
        /// 5 秒预录里副画面就只剩几帧，看起来一卡一卡的。
        /// </summary>
        internal void ComposeOverlayChannels(Mat frame)
        {
            if (_overlayChannels.Length == 0)
                SyncOverlayChannelRuntimes();

            if (!HasConfiguredOverlayChannels)
                return;

            foreach (OverlayChannel channel in _overlayChannels)
                ComposeOverlayChannel(channel, frame);
        }

        private void ComposeOverlayChannel(OverlayChannel channel, Mat frame)
        {
            if (!channel.Config.IsConfigured)
                return;

            lock (channel.OverlayLock)
            {
                RefreshOverlayFrame(channel);

                Mat? overlay = channel.OverlayFrame;
                if (overlay == null || overlay.IsDisposed || overlay.Empty())
                    return;

                try
                {
                    // 识别框就是裁剪范围：画中画只显示框内那块，识别也只解这块。
                    // 与主摄画框用的是同一个换算：几何 -> 画面上的矩形。
                    System.Windows.Rect guide = CameraBarcodeGuideLayout.ToDisplayRect(
                        ResolveOverlayGuideGeometry(channel.Config, overlay.Width, overlay.Height),
                        new System.Windows.Rect(0, 0, overlay.Width, overlay.Height));
                    Rect cropRect = new Rect(
                            (int)Math.Round(guide.X),
                            (int)Math.Round(guide.Y),
                            Math.Max(1, (int)Math.Round(guide.Width)),
                            Math.Max(1, (int)Math.Round(guide.Height)))
                        .Intersect(new Rect(0, 0, overlay.Width, overlay.Height));
                    if (cropRect.Width <= 0 || cropRect.Height <= 0)
                        return;

                    // 先算好"这一帧把叠加画面画在哪"，再把这**同一个矩形**交给合成：
                    // 画进去的位置和界面拖动框据此换算的位置必须完全一致，
                    // 两边各算一份就会在贴角规则上走岔（新增的第三、第四路贴上面两角时最明显）。
                    CameraOverlayRect? composedRect = CameraOverlayLayout.Resolve(
                        frame.Width,
                        frame.Height,
                        cropRect.Width,
                        cropRect.Height,
                        channel.Config.OverlayWidthRatio,
                        channel.Config.OverlayMargin,
                        channel.Config.OverlayLeftRatio,
                        channel.Config.OverlayTopRatio,
                        allowUpscale: false,
                        anchor: OverlayAnchorFor(channel));

                    if (composedRect is { } targetRect
                        && CameraOverlayComposer.TryCompose(frame, overlay, cropRect, targetRect))
                    {
                        OverlayGeometrySnapshot geometry = channel.Geometry;
                        bool placementChanged = geometry.ComposedRect != composedRect
                            || geometry.ComposedFrameWidth != frame.Width
                            || geometry.ComposedFrameHeight != frame.Height;
                        channel.Geometry = geometry.WithComposition(targetRect, frame.Width, frame.Height);
                        // 落位变了要立刻叫界面重摆拖动框。叠加画面是画进帧里的，界面那个框
                        // 平时不跟着每帧走，只在这里通知才不会停在上一帧的位置、和画面错开。
                        if (placementChanged)
                            NotifyOverlayPlacementChanged();
                    }

                    channel.ComposeFailureLogged = false;
                }
                catch (Exception ex)
                {
                    // 合成失败是每帧都进这里的：只落第一条，而且带类型和堆栈 ——
                    // 以前只记 ex.Message，"到底哪份资源被释放了"只能靠猜（画中画被蒙版缓存
                    // 自己挤掉那次就是这么被拖了很久）。恢复成功后再失败会重新记一条。
                    if (!channel.ComposeFailureLogged)
                    {
                        channel.ComposeFailureLogged = true;
                        RuntimeLog.Error("OverlayChannel", $"第 {channel.Number} 路叠加画面合成失败", ex);
                    }
                }
            }
        }

        /// <summary>
        /// 取走最新的一路帧并保留：主帧处理比它快/慢时都不会闪成黑块，
        /// 没有新帧就继续用上一帧，直到这一路停止才释放。
        /// </summary>
        private void RefreshOverlayFrame(OverlayChannel channel)
        {
            Mat? latest = channel.LatestFrame.Take();
            if (latest == null)
                return;

            if (latest.Empty() || latest.IsDisposed)
            {
                latest.Dispose();
                return;
            }

            // 停止通道时先 Clear() 再等采集回调收尾，这中间还会来一两帧。收下就麻烦：
            // 这一路已经不在跑了，这帧会被当成"当前画面"一直贴在录像里，不再有人来换掉它。
            if (!channel.IsRunning)
            {
                latest.Dispose();
                return;
            }

            Mat? previous = channel.OverlayFrame;
            channel.OverlayFrame = latest;
            channel.Geometry = channel.Geometry.WithSourceSize(latest.Width, latest.Height);
            previous?.Dispose();

            if (!channel.HasFrame)
                SetOverlayFrameFlag(channel, true);
        }

        /// <summary>
        /// 一路叠加画面的识别框：与主摄同一套定义（宽高占比 + 居中偏移），在预览的画中画上直接拖。
        /// 它只决定识别哪一块，不裁剪画面内容。
        /// </summary>
        private CameraBarcodeGuideGeometry ResolveOverlayGuideGeometry(OverlayChannel channel) =>
            channel.Geometry is { SourceWidth: > 0, SourceHeight: > 0 } geometry
                ? ResolveOverlayGuideGeometry(channel.Config, geometry.SourceWidth, geometry.SourceHeight)
                : new CameraBarcodeGuideGeometry(
                    channel.Config.BarcodeGuideWidthRatio,
                    channel.Config.BarcodeGuideHeightRatio,
                    channel.Config.BarcodeGuideOffsetX,
                    channel.Config.BarcodeGuideOffsetY);

        /// <summary>
        /// 取景框几何：用户没调过（还是默认比例 + 居中）时按"短边居中方形"算 ——
        /// 也就是默认 1:1 裁剪，这块既是要显示的画面、也是识别范围；
        /// 调过以后按存下来的比例走，四个角可以自由改大小。
        /// </summary>
        internal static CameraBarcodeGuideGeometry ResolveOverlayGuideGeometry(
            CameraChannelConfig channel,
            int frameWidth,
            int frameHeight)
        {
            bool untouched =
                Math.Abs(channel.BarcodeGuideWidthRatio - AppConfig.DefaultOverlayGuideRatio) < 0.001
                && Math.Abs(channel.BarcodeGuideHeightRatio - AppConfig.DefaultOverlayGuideRatio) < 0.001
                && Math.Abs(channel.BarcodeGuideOffsetX) < 0.001
                && Math.Abs(channel.BarcodeGuideOffsetY) < 0.001;

            if (untouched && frameWidth > 0 && frameHeight > 0)
            {
                double side = Math.Min(frameWidth, frameHeight) * AppConfig.DefaultOverlayGuideRatio;
                return new CameraBarcodeGuideGeometry(side / frameWidth, side / frameHeight, 0, 0);
            }

            return new CameraBarcodeGuideGeometry(
                channel.BarcodeGuideWidthRatio,
                channel.BarcodeGuideHeightRatio,
                channel.BarcodeGuideOffsetX,
                channel.BarcodeGuideOffsetY);
        }

        /// <summary>按通道号取取景框几何；通道不存在时退回整幅。</summary>
        internal CameraBarcodeGuideGeometry GetOverlayGuideGeometry(int channelNumber) =>
            FindOverlayChannel(channelNumber) is { } channel
                ? ResolveOverlayGuideGeometry(channel)
                : new CameraBarcodeGuideGeometry(1.0, 1.0, 0, 0);

        /// <summary>识别来源选了某一路叠加画面、且那一路已经出帧时，识别框画在画中画上。</summary>
        internal bool IsOverlayGuideVisible => ShouldUseOverlayChannelForBarcode;

        /// <summary>当前识别框几何（与主摄同一套语义）。</summary>
        internal CameraBarcodeGuideGeometry CurrentOverlayGuideGeometry =>
            // 正在编辑取景时用被编辑那一路的几何：编辑屏就是用来调它的，与识别来源选没选它无关。
            FindOverlayChannel(_editingOverlayChannelNumber) is { } editing
                ? ResolveOverlayGuideGeometry(editing)
                : ActiveBarcodeOverlayChannel is { } active
                    ? ResolveOverlayGuideGeometry(active)
                    : new CameraBarcodeGuideGeometry(1.0, 1.0, 0, 0);

        /// <summary>是否正在编辑某一路叠加画面的取景。</summary>
        public bool IsEditingOverlayPreview { get; private set; }

        /// <summary>正在编辑取景的通道号；没在编辑时为 0。</summary>
        private int _editingOverlayChannelNumber;

        /// <summary>供界面按钮显隐使用。</summary>
        public bool IsOverlayPreviewEditing => IsEditingOverlayPreview;

        /// <summary>正在编辑取景的那一路画面（编辑态下由主预览区显示）。</summary>
        public System.Windows.Media.Imaging.BitmapSource? OverlayPreviewFrame =>
            _editingOverlayChannelNumber > 0
                ? FindOverlayChannel(_editingOverlayChannelNumber)?.PreviewFrame
                : null;

        /// <summary>
        /// 主预览区当前该显示的帧：平常是主画面，进入叠加画面取景编辑后是那一路的整幅画面。
        /// 界面只绑这一个属性，不必在代码里抢 Image.Source（抢了会被帧刷新冲掉）。
        /// </summary>
        public System.Windows.Media.Imaging.BitmapSource? PreviewImageSource =>
            IsEditingOverlayPreview ? OverlayPreviewFrame : VideoFrame;

        /// <summary>点画中画进入取景编辑；那一路没在跑时不进（进去也没画面）。</summary>
        internal void EnterOverlayPreviewEdit(int channelNumber)
        {
            if (FindOverlayChannel(channelNumber) is not { } channel || !channel.Config.IsConfigured)
                return;

            RuntimeLog.Info("OverlayChannel", $"进入第 {channelNumber} 路取景编辑");
            _editingOverlayChannelNumber = channelNumber;
            IsEditingOverlayPreview = true;
            OnPropertyChanged(nameof(OverlayPreviewFrame));
            OnPropertyChanged(nameof(IsEditingOverlayPreview));
            OnPropertyChanged(nameof(IsOverlayPreviewEditing));
            OnPropertyChanged(nameof(PreviewImageSource));
            OnPropertyChanged(nameof(CameraBarcodeStatusText));
            OnPropertyChanged(nameof(CameraFrameSize));
            // 小锁/框的可编辑性都跟"在不在取景编辑屏"有关：识别来源是副画面时，
            // 主界面上不显示小锁，但进了这一屏就要显示，所以这里必须重新通知一次。
            OnPropertyChanged(nameof(IsBarcodeGuideVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
            // 编辑态由这一路画面接管预览，别让主画面的帧把它冲掉。
            SuppressVideoPreviewUpdates = true;
        }

        internal void ExitOverlayPreviewEdit()
        {
            if (!IsEditingOverlayPreview)
                return;

            RuntimeLog.Info("OverlayChannel", "退出叠加画面取景编辑");
            int previous = _editingOverlayChannelNumber;
            IsEditingOverlayPreview = false;
            _editingOverlayChannelNumber = 0;
            if (FindOverlayChannel(previous) is { } channel)
                channel.PreviewFrame = null;

            OnPropertyChanged(nameof(OverlayPreviewFrame));
            OnPropertyChanged(nameof(IsEditingOverlayPreview));
            OnPropertyChanged(nameof(IsOverlayPreviewEditing));
            OnPropertyChanged(nameof(PreviewImageSource));
            OnPropertyChanged(nameof(CameraBarcodeStatusText));
            OnPropertyChanged(nameof(CameraFrameSize));
            OnPropertyChanged(nameof(IsBarcodeGuideVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
            SuppressVideoPreviewUpdates = false;
        }

        /// <summary>
        /// 编辑态下把这一路的帧转成预览位图，节流到 30fps：既跟得上采集帧率、拖动取景时不卡，
        /// 又不会每一帧都做一次整帧转换。
        /// </summary>
        private void PublishOverlayPreviewFrameIfDue(OverlayChannel channel, Mat frame)
        {
            if (!IsEditingOverlayPreview || _editingOverlayChannelNumber != channel.Number)
                return;
            if (frame == null || frame.Empty())
                return;

            DateTime now = DateTime.Now;
            if (now - channel.LastPreviewPublishedAt < TimeSpan.FromMilliseconds(33))
                return;
            channel.LastPreviewPublishedAt = now;

            int width = frame.Width;
            int height = frame.Height;
            byte[] pixels = new byte[width * height * 3];
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width,
                height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgr24,
                null,
                pixels,
                width * 3);
            bitmap.Freeze();

            // 做这张位图期间界面可能已经退出取景编辑：这一帧就不要再挂上去，
            // 否则退出后还留着上一屏的画面，下次进来会先闪一帧旧的。
            if (!IsEditingOverlayPreview || _editingOverlayChannelNumber != channel.Number)
                return;

            channel.PreviewFrame = bitmap;
            OnPropertyChanged(nameof(OverlayPreviewFrame));
            OnPropertyChanged(nameof(PreviewImageSource));
            OnPropertyChanged(nameof(CameraFrameSize));
        }

        /// <summary>
        /// 在预览里拖动某一路识别框时写回配置：拖动过程只改内存，松手才落盘。
        /// 与主摄识别框走同一套换算，只是参考矩形换成那一路的画中画。
        /// </summary>
        internal void ApplyOverlayBarcodeGuideGeometry(
            int channelNumber,
            CameraBarcodeGuideGeometry geometry,
            bool persist)
        {
            if (FindOverlayChannel(channelNumber) is not { } channel)
                return;
            if (!TryGetChannelIndex(channelNumber, out int index))
                return;

            double widthRatio = AppConfig.NormalizeOverlayGuideRatio(geometry.WidthRatio);
            double heightRatio = AppConfig.NormalizeOverlayGuideRatio(geometry.HeightRatio);
            double offsetX = AppConfig.NormalizeGuideOffset(geometry.OffsetX);
            double offsetY = AppConfig.NormalizeGuideOffset(geometry.OffsetY);
            channel.Config.BarcodeGuideWidthRatio = widthRatio;
            channel.Config.BarcodeGuideHeightRatio = heightRatio;
            channel.Config.BarcodeGuideOffsetX = offsetX;
            channel.Config.BarcodeGuideOffsetY = offsetY;
            if (!persist)
                return;

            if (!WorkstationConfigStore.TryUpdate(
                    saved => ApplyOverlayGuideSample(saved, index, widthRatio, heightRatio, offsetX, offsetY),
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("OverlayChannel", $"识别框保存失败：{error}");
                return;
            }

            if (TryGetChannelIndex(savedConfig, index, out CameraChannelConfig savedChannel))
            {
                channel.Config.BarcodeGuideWidthRatio = savedChannel.BarcodeGuideWidthRatio;
                channel.Config.BarcodeGuideHeightRatio = savedChannel.BarcodeGuideHeightRatio;
                channel.Config.BarcodeGuideOffsetX = savedChannel.BarcodeGuideOffsetX;
                channel.Config.BarcodeGuideOffsetY = savedChannel.BarcodeGuideOffsetY;
            }
        }

        private static void ApplyOverlayGuideSample(
            AppConfig saved,
            int index,
            double widthRatio,
            double heightRatio,
            double offsetX,
            double offsetY)
        {
            if (!TryGetChannelIndex(saved, index, out CameraChannelConfig channel))
                return;

            channel.BarcodeGuideWidthRatio = widthRatio;
            channel.BarcodeGuideHeightRatio = heightRatio;
            channel.BarcodeGuideOffsetX = offsetX;
            channel.BarcodeGuideOffsetY = offsetY;
        }

        private static bool TryGetChannelIndex(AppConfig config, int index, out CameraChannelConfig channel)
        {
            if (config.CameraChannels != null && index >= 0 && index < config.CameraChannels.Count)
            {
                channel = config.CameraChannels[index];
                return true;
            }

            channel = null!;
            return false;
        }

        private bool TryGetChannelIndex(int channelNumber, out int index)
        {
            index = channelNumber - 1;
            return Config is { } config && index >= 0 && index < config.CameraChannels.Count;
        }

        /// <summary>
        /// 这里跑在视频处理线程上，属性通知必须回 UI 线程，
        /// 否则绑定到它的界面元素会在非 UI 线程更新而抛异常。
        /// </summary>
        private void SetOverlayFrameFlag(OverlayChannel channel, bool value)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplyOverlayFrameFlag(channel, value);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => ApplyOverlayFrameFlag(channel, value)));
        }

        private void ApplyOverlayFrameFlag(OverlayChannel channel, bool value)
        {
            if (channel.HasFrame == value)
                return;

            channel.HasFrame = value;
            OnPropertyChanged(nameof(HasOverlayFrame));
            OnPropertyChanged(nameof(IsBarcodeGuideVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
        }

        private void DisposeOverlayFrame(OverlayChannel channel)
        {
            Mat? current = channel.OverlayFrame;
            channel.OverlayFrame = null;
            if (current != null && !current.IsDisposed)
                current.Dispose();
        }

        /// <summary>
        /// 按当前配置与某一路最新尺寸算出它在主帧里的落位。
        /// 主界面用它摆放拖动框，合成用它摆放画面，两边保证同源。
        /// </summary>
        internal bool TryResolveOverlayRect(
            int channelNumber,
            int frameWidth,
            int frameHeight,
            out CameraOverlayRect rect)
        {
            rect = default;
            if (FindOverlayChannel(channelNumber) is not { } channel)
                return false;

            // 一次读一份快照：尺寸和落位必须来自同一次合成，不能各读一半、拼出"新尺寸配旧落位"。
            OverlayGeometrySnapshot geometry = channel.Geometry;

            // 优先用"上一帧实际合成时画在哪"等比换算：预览帧可能被降采样过，
            // 自己另算一份会因为取整/尺寸来源不同而与画面错开几个像素甚至几十像素。
            if (geometry.ComposedRect is { } composed
                && geometry.ComposedFrameWidth > 0
                && geometry.ComposedFrameHeight > 0
                && frameWidth > 0
                && frameHeight > 0)
            {
                double scaleX = (double)frameWidth / geometry.ComposedFrameWidth;
                double scaleY = (double)frameHeight / geometry.ComposedFrameHeight;
                rect = new CameraOverlayRect(
                    (int)Math.Round(composed.X * scaleX),
                    (int)Math.Round(composed.Y * scaleY),
                    (int)Math.Round(composed.Width * scaleX),
                    (int)Math.Round(composed.Height * scaleY));
                return true;
            }

            int sourceWidth = geometry.SourceWidth;
            int sourceHeight = geometry.SourceHeight;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            // 画中画显示的是识别框内那块，落位必须按裁剪后的尺寸与比例算，
            // 否则拖动框会跟画面错位（这正是之前"框和画面对不上"的来源）。
            System.Windows.Rect guide = CameraBarcodeGuideLayout.ToDisplayRect(
                ResolveOverlayGuideGeometry(channel.Config, sourceWidth, sourceHeight),
                new System.Windows.Rect(0, 0, sourceWidth, sourceHeight));
            int croppedWidth = Math.Max(1, (int)Math.Round(guide.Width));
            int croppedHeight = Math.Max(1, (int)Math.Round(guide.Height));

            CameraOverlayRect? resolved = CameraOverlayLayout.Resolve(
                frameWidth,
                frameHeight,
                croppedWidth,
                croppedHeight,
                channel.Config.OverlayWidthRatio,
                channel.Config.OverlayMargin,
                channel.Config.OverlayLeftRatio,
                channel.Config.OverlayTopRatio,
                allowUpscale: false,
                anchor: OverlayAnchorFor(channel));

            if (resolved is not { } value)
                return false;

            rect = value;
            return true;
        }

        /// <summary>
        /// 拖动中调用：只改内存配置，下一帧合成立刻按新位置画，预览实时跟随；松手时再落盘。
        /// </summary>
        internal void SetOverlayPosition(
            int channelNumber,
            double frameX,
            double frameY,
            int frameWidth,
            int frameHeight)
        {
            if (FindOverlayChannel(channelNumber) is not { } channel || frameWidth <= 0 || frameHeight <= 0)
                return;
            if (!TryResolveOverlayRect(channelNumber, frameWidth, frameHeight, out CameraOverlayRect rect))
                return;

            // 存左上角的归一化比例，换分辨率/换摄像头都不会跑偏；夹紧保证整块小窗留在画面内。
            double left = Math.Clamp(frameX / frameWidth, 0.0, 1.0);
            double top = Math.Clamp(frameY / frameHeight, 0.0, 1.0);
            left = Math.Min(left, Math.Max(0.0, 1.0 - ((double)rect.Width / frameWidth)));
            top = Math.Min(top, Math.Max(0.0, 1.0 - ((double)rect.Height / frameHeight)));

            channel.Config.OverlayLeftRatio = left;
            channel.Config.OverlayTopRatio = top;
        }

        /// <summary>拖动画中画把手改大小：只改内存配置，下一帧合成立刻跟着变，松手才落盘。</summary>
        internal void SetOverlayWidth(int channelNumber, double widthRatio)
        {
            if (FindOverlayChannel(channelNumber) is not { } channel)
                return;

            channel.Config.OverlayWidthRatio = CameraOverlayLayout.NormalizeWidthRatio(widthRatio);
        }

        /// <summary>把拖动改出来的画中画大小落盘。</summary>
        internal void SaveOverlayWidth(int channelNumber)
        {
            if (FindOverlayChannel(channelNumber) is not { } channel)
                return;
            if (!TryGetChannelIndex(channelNumber, out int index))
                return;

            double widthRatio = channel.Config.OverlayWidthRatio;
            if (!WorkstationConfigStore.TryUpdate(
                    saved =>
                    {
                        if (TryGetChannelIndex(saved, index, out CameraChannelConfig target))
                            target.OverlayWidthRatio = widthRatio;
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("OverlayChannel", $"画中画大小保存失败：{error}");
                return;
            }

            if (TryGetChannelIndex(savedConfig, index, out CameraChannelConfig savedChannel))
                channel.Config.OverlayWidthRatio = savedChannel.OverlayWidthRatio;
        }

        /// <summary>把拖动结果落盘，下次启动还在同一位置。</summary>
        internal void SaveOverlayPosition(int channelNumber)
        {
            if (FindOverlayChannel(channelNumber) is not { } channel)
                return;
            if (!TryGetChannelIndex(channelNumber, out int index))
                return;
            if (channel.Config.OverlayLeftRatio < 0 || channel.Config.OverlayTopRatio < 0)
                return;

            double left = channel.Config.OverlayLeftRatio;
            double top = channel.Config.OverlayTopRatio;
            if (!WorkstationConfigStore.TryUpdate(
                    saved =>
                    {
                        if (TryGetChannelIndex(saved, index, out CameraChannelConfig target))
                        {
                            target.OverlayLeftRatio = left;
                            target.OverlayTopRatio = top;
                        }
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("OverlayChannel", $"画中画位置保存失败：{error}");
                return;
            }

            if (TryGetChannelIndex(savedConfig, index, out CameraChannelConfig savedChannel))
            {
                channel.Config.OverlayLeftRatio = savedChannel.OverlayLeftRatio;
                channel.Config.OverlayTopRatio = savedChannel.OverlayTopRatio;
            }
        }

        /// <summary>识别来源当前选中的叠加画面；没选、没接或找不到时返回 null。</summary>
        private OverlayChannel? ActiveBarcodeOverlayChannel
        {
            get
            {
                int channelNumber = Config?.CameraBarcodeRecognitionChannel ?? 0;
                if (channelNumber <= 0)
                    return null;

                OverlayChannel? channel = FindOverlayChannel(channelNumber);
                return channel is { Config.IsConfigured: true } ? channel : null;
            }
        }

        /// <summary>
        /// 「摄像头自动识别面单」是否改用叠加画面：配置选了那一路、那一路开着、而且真的出过帧。
        ///
        /// 最后一条很重要：选了叠加画面但它连不上/还没出帧时必须回退主画面，
        /// 否则"换了识别摄像头但没连上"会直接变成完全识别不了。
        /// </summary>
        internal bool ShouldUseOverlayChannelForBarcode =>
            ActiveBarcodeOverlayChannel is { HasFrame: true };

        /// <summary>识别来源当前选中的通道号；没选或那一路没接设备时为 0。</summary>
        internal int BarcodeOverlayChannelNumber => ActiveBarcodeOverlayChannel?.Number ?? 0;

        /// <summary>这一帧来自哪一路叠加画面（0 = 主摄）：识别只接受当前识别来源那一路。</summary>
        private int BarcodeFrameSourceChannelNumber { get; set; }

        /// <summary>把某一路的帧交给面单识别；不是当前识别来源的那一路直接丢掉。</summary>
        private void TrySubmitOverlayBarcodeFrame(OverlayChannel channel, Mat frame)
        {
            if (frame == null || frame.Empty())
                return;
            if (Config?.CameraBarcodeRecognitionChannel != channel.Number)
                return;

            TrySubmitCameraBarcodeFrame(frame, sourceChannelNumber: channel.Number);
        }
    }
}
