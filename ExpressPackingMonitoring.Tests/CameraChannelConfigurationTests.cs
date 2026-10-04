using System.Text;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 叠加画面（画中画）通道的配置与接线约束：
/// 每一路独立配置、比例与留白被夹紧、改采集参数要求重启采集；
/// 合成必须接在处理循环里（预览与录像共用的那一帧上），且旧的"同一路内嵌小窗"不得再出现。
///
/// 频道化的核心不变量：**加一路只是多一个 CameraChannelConfig，不该再有第二套字段或第二条链路**。
/// </summary>
public sealed class CameraChannelConfigurationTests
{
    private static readonly string[] OverlayChannelSources =
    [
        "ViewModels/MainViewModel.OverlayChannels.cs",
        "ViewModels/CameraOverlayLayout.cs",
        "ViewModels/CameraOverlayComposer.cs"
    ];

    [Fact]
    public void OverlayChannelsAreOffByDefault()
    {
        var config = new AppConfig();

        // 默认必须关：老用户升级后画面不能凭空多出一路。空通道只为设置页有卡片可显示。
        Assert.Equal(AppConfig.DefaultOverlayChannelCount, config.OverlayChannelCount);
        Assert.Equal(AppConfig.DefaultOverlayChannelCount, config.CameraChannels.Count);
        Assert.All(config.CameraChannels, channel => Assert.False(channel.IsConfigured));

        CameraChannelConfig channel = config.CameraChannels[0];
        Assert.Equal(AppConfig.OverlayChannelSourceNone, channel.SourceKind);
        Assert.Equal(AppConfig.DefaultOverlayWidthRatio, channel.OverlayWidthRatio);
        Assert.Equal(AppConfig.DefaultOverlayMargin, channel.OverlayMargin);
        Assert.Equal(AppConfig.UnsetOverlayPosition, channel.OverlayLeftRatio);
        Assert.Equal(AppConfig.UnsetOverlayPosition, channel.OverlayTopRatio);
    }

    [Theory]
    [InlineData(0.0, AppConfig.DefaultOverlayWidthRatio)]
    [InlineData(-1.0, AppConfig.DefaultOverlayWidthRatio)]
    [InlineData(double.NaN, AppConfig.DefaultOverlayWidthRatio)]
    [InlineData(0.01, AppConfig.MinimumOverlayWidthRatio)]
    [InlineData(0.9, AppConfig.MaximumOverlayWidthRatio)]
    [InlineData(0.3, 0.3)]
    public void NormalizeAfterLoad_ClampsOverlayWidthRatio(double raw, double expected)
    {
        var config = new AppConfig();
        config.CameraChannels[0].OverlayWidthRatio = raw;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.CameraChannels[0].OverlayWidthRatio);
    }

    [Theory]
    [InlineData(-5, AppConfig.DefaultOverlayMargin)]
    [InlineData(0, 0)]
    [InlineData(5000, AppConfig.MaximumOverlayMargin)]
    [InlineData(24, 24)]
    public void NormalizeAfterLoad_ClampsOverlayMargin(int raw, int expected)
    {
        var config = new AppConfig();
        config.CameraChannels[0].OverlayMargin = raw;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.CameraChannels[0].OverlayMargin);
    }

    /// <summary>位置只在拖动过之后才是 0~1 的比例，非法值一律回到"自动右下角"。</summary>
    [Theory]
    [InlineData(-5.0, AppConfig.UnsetOverlayPosition)]
    [InlineData(double.NaN, AppConfig.UnsetOverlayPosition)]
    [InlineData(-1.0, AppConfig.UnsetOverlayPosition)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.35, 0.35)]
    public void NormalizeAfterLoad_ClampsOverlayPosition(double raw, double expected)
    {
        var config = new AppConfig();
        config.CameraChannels[0].OverlayLeftRatio = raw;
        config.CameraChannels[0].OverlayTopRatio = raw;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.CameraChannels[0].OverlayLeftRatio);
        Assert.Equal(expected, config.CameraChannels[0].OverlayTopRatio);
    }

    /// <summary>叠加画面的来源与主摄同口径：去空白、判来源、传输方式归一。</summary>
    [Fact]
    public void NormalizeAfterLoad_NormalizesOverlaySource()
    {
        var config = new AppConfig();
        config.CameraChannels[0].SourceKind = "什么都不是";
        config.CameraChannels[0].NetworkCameraUrl = "  rtsp://192.168.1.9/stream  ";
        config.CameraChannels[0].NetworkCameraRtspTransport = "UDP";

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("network", config.CameraChannels[0].SourceKind);
        Assert.Equal("rtsp://192.168.1.9/stream", config.CameraChannels[0].NetworkCameraUrl);
        Assert.Equal("udp", config.CameraChannels[0].NetworkCameraRtspTransport);
    }

    /// <summary>来源写坏时必须回到"无"，绝不因为一个坏值让这一路悄悄开始采集。</summary>
    [Fact]
    public void NormalizeAfterLoad_BrokenSourceFallsBackToNone()
    {
        var config = new AppConfig();
        config.CameraChannels[0].SourceKind = "乱写";

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.CameraChannels[0].IsConfigured);
    }

    [Theory]
    [InlineData(null, "720p")]
    [InlineData("", "720p")]
    [InlineData("480p", "480p")]
    [InlineData("720p", "720p")]
    [InlineData("1080P", "1080p")]
    [InlineData("2160p", "720p")]
    public void NormalizeOverlayResolutionPreset_FallsBackTo720p(string? raw, string expected) =>
        Assert.Equal(expected, AppConfig.NormalizeOverlayResolutionPreset(raw));

    [Fact]
    public void ResolveOverlayFrameSize_MapsPresets()
    {
        Assert.Equal((640, 480), AppConfig.ResolveOverlayFrameSize("480p"));
        Assert.Equal((1280, 720), AppConfig.ResolveOverlayFrameSize("720p"));
        Assert.Equal((1920, 1080), AppConfig.ResolveOverlayFrameSize("1080p"));
        Assert.Equal((1280, 720), AppConfig.ResolveOverlayFrameSize("不属于任何预设"));
    }

    /// <summary>叠加画面帧率是独立设置项，必须被夹到合法区间，0 回落到默认值。</summary>
    [Fact]
    public void NormalizeAfterLoad_ClampsOverlayFrameRate()
    {
        var config = new AppConfig();
        config.CameraChannels[0].FrameFps = 0;
        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(AppConfig.DefaultOverlayFrameFps, config.CameraChannels[0].FrameFps);

        config = new AppConfig();
        config.CameraChannels[0].FrameFps = 999;
        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(AppConfig.MaximumOverlayFrameFps, config.CameraChannels[0].FrameFps);
    }

    /// <summary>旋转只认 0/90/180/270，手改坏的角度按不旋转处理。</summary>
    [Theory]
    [InlineData(90, 90)]
    [InlineData(270, 270)]
    [InlineData(45, 0)]
    [InlineData(-1, 0)]
    public void NormalizeAfterLoad_ClampsOverlayRotation(int raw, int expected)
    {
        var config = new AppConfig();
        config.CameraChannels[0].RotationDegrees = raw;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.CameraChannels[0].RotationDegrees);
    }

    /// <summary>
    /// 路数跟着"副画面数量"走：设置页按它显示几张卡（都是"无"就是不接）。
    /// 少了补齐、多了截断，所以界面不需要"添加/删除"这种入口。
    /// </summary>
    [Fact]
    public void NormalizeAfterLoad_FollowsConfiguredChannelCount()
    {
        var tooMany = new AppConfig
        {
            OverlayChannelCount = 3,
            CameraChannels = Enumerable.Range(0, AppConfig.MaximumOverlayChannelCount + 2)
                .Select(_ => new CameraChannelConfig { SourceKind = "usb", MonikerString = "m" })
                .ToList()
        };
        AppConfig.NormalizeAfterLoad(tooMany);
        Assert.Equal(3, tooMany.CameraChannels.Count);
        Assert.Equal(3, tooMany.OverlayChannelCount);

        var onlyOne = new AppConfig
        {
            CameraChannels = [new CameraChannelConfig { SourceKind = "usb", MonikerString = "m1" }]
        };
        AppConfig.NormalizeAfterLoad(onlyOne);
        Assert.Equal(AppConfig.DefaultOverlayChannelCount, onlyOne.CameraChannels.Count);
        Assert.Equal("m1", onlyOne.CameraChannels[0].MonikerString);
        Assert.False(onlyOne.CameraChannels[1].IsConfigured);

        var empty = new AppConfig { CameraChannels = [] };
        AppConfig.NormalizeAfterLoad(empty);
        Assert.Equal(AppConfig.DefaultOverlayChannelCount, empty.CameraChannels.Count);

        // 配置了四路：补齐到四张卡，设置页与运行时都按四路走。
        var four = new AppConfig { OverlayChannelCount = 4, CameraChannels = [] };
        AppConfig.NormalizeAfterLoad(four);
        Assert.Equal(4, four.CameraChannels.Count);
        Assert.Equal(4, four.OverlayChannelCount);
    }

    /// <summary>副画面数量的取值区间：越界一律夹回可配置范围，写坏的值不会带出一堆空通道。</summary>
    [Fact]
    public void NormalizeAfterLoad_ClampsOverlayChannelCount()
    {
        foreach ((int raw, int expected) in new[]
        {
            (AppConfig.MinimumOverlayChannelCount - 1, AppConfig.MinimumOverlayChannelCount),
            (0, AppConfig.MinimumOverlayChannelCount),
            (int.MinValue, AppConfig.MinimumOverlayChannelCount),
            (AppConfig.MaximumOverlayChannelCount + 1, AppConfig.MaximumOverlayChannelCount),
            (99, AppConfig.MaximumOverlayChannelCount),
        })
        {
            var config = new AppConfig { OverlayChannelCount = raw };
            AppConfig.NormalizeAfterLoad(config);
            Assert.Equal(expected, config.OverlayChannelCount);
            Assert.Equal(expected, config.CameraChannels.Count);
        }
    }

    /// <summary>改小副画面数量：多出来的那几路连同它的设置一起收掉，识别来源不能停在已经不存在的那一路上。</summary>
    [Fact]
    public void NormalizeAfterLoad_ShrinkingDropsExtraChannels()
    {
        var config = new AppConfig
        {
            OverlayChannelCount = 4,
            CameraChannels =
            [
                new CameraChannelConfig { SourceKind = "usb", MonikerString = "相机A" },
                new CameraChannelConfig { SourceKind = "usb", MonikerString = "相机B" },
                new CameraChannelConfig { SourceKind = "usb", MonikerString = "相机C" },
                new CameraChannelConfig { SourceKind = "usb", MonikerString = "相机D" },
            ]
        };
        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(4, config.CameraChannels.Count);

        // 识别来源放在第四路：把路数改成两路后，第四路不存在了，必须回主摄。
        config.CameraBarcodeRecognitionChannel = 4;
        config.OverlayChannelCount = 2;
        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(2, config.CameraChannels.Count);
        Assert.Equal("相机A", config.CameraChannels[0].MonikerString);
        Assert.Equal("相机B", config.CameraChannels[1].MonikerString);
        Assert.Equal(0, config.CameraBarcodeRecognitionChannel);
    }

    /// <summary>两路各自独立：设备、旋转、档位、画中画比例互不干扰。</summary>
    [Fact]
    public void OverlayChannels_AreIndependent()
    {
        var config = new AppConfig
        {
            CameraChannels =
            [
                new CameraChannelConfig
                {
                    SourceKind = "usb",
                    MonikerString = "相机A",
                    RotationDegrees = 90,
                    FrameFps = 15,
                    OverlayWidthRatio = 0.2
                },
                new CameraChannelConfig
                {
                    SourceKind = "usb",
                    MonikerString = "相机B",
                    RotationDegrees = 270,
                    FrameFps = 30,
                    OverlayWidthRatio = 0.4
                }
            ]
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(2, config.CameraChannels.Count);
        Assert.Equal("相机A", config.CameraChannels[0].MonikerString);
        Assert.Equal("相机B", config.CameraChannels[1].MonikerString);
        Assert.Equal(90, config.CameraChannels[0].RotationDegrees);
        Assert.Equal(270, config.CameraChannels[1].RotationDegrees);
        Assert.Equal(15, config.CameraChannels[0].FrameFps);
        Assert.Equal(30, config.CameraChannels[1].FrameFps);
        Assert.Equal(0.2, config.CameraChannels[0].OverlayWidthRatio);
        Assert.Equal(0.4, config.CameraChannels[1].OverlayWidthRatio);
    }

    /// <summary>
    /// 换叠加画面规格要重开**这一路**，否则设置改了不生效；主摄的流跟副画面无关，不该跟着重启。
    /// </summary>
    [Fact]
    public void OverlayChannelsRequireRestart_ReactsToCaptureFormat()
    {
        var current = new AppConfig();
        var next = new AppConfig();
        next.CameraChannels[0].ResolutionPreset = "1080p";
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, next));
        Assert.False(AppConfig.RequiresCameraRestart(current, next));

        next = new AppConfig();
        next.CameraChannels[0].FrameFps = 15;
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, next));
        Assert.False(AppConfig.RequiresCameraRestart(current, next));
    }

    /// <summary>
    /// 改了副画面那一路的来源/设备/地址/旋转/路数就必须重开这一路（不然设置里换设备不生效），
    /// 但这些都属于副画面自己的事，不该把主摄也重启一遍。
    /// </summary>
    [Fact]
    public void OverlayChannelsRequireRestart_ReactsToChannelChanges()
    {
        var current = new AppConfig();

        var usb = new AppConfig();
        usb.CameraChannels[0].SourceKind = "usb";
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, usb));

        var otherDevice = new AppConfig();
        otherDevice.CameraChannels[0].MonikerString = "别的一台";
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, otherDevice));

        var network = new AppConfig();
        network.CameraChannels[0].SourceKind = "network";
        network.CameraChannels[0].NetworkCameraUrl = "rtsp://x/y";
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, network));

        // 旋转变了也要重开（默认已是 90°，显式改成 180 才算变）
        var rotated = new AppConfig();
        rotated.CameraChannels[0].RotationDegrees = 180;
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, rotated));

        // 加了一路也是采集变化
        var addedChannel = new AppConfig();
        addedChannel.CameraChannels.Add(new CameraChannelConfig { SourceKind = "usb", MonikerString = "第二台" });
        Assert.True(AppConfig.OverlayChannelsRequireRestart(current, addedChannel));
        Assert.False(AppConfig.RequiresCameraRestart(current, addedChannel));

        // 与采集无关的字段不能引起重启。
        var wider = new AppConfig();
        wider.CameraChannels[0].OverlayWidthRatio = 0.4;
        Assert.False(AppConfig.OverlayChannelsRequireRestart(current, wider));
        Assert.False(AppConfig.RequiresCameraRestart(current, wider));

        var barcodeChannel = new AppConfig { CameraBarcodeRecognitionChannel = 1 };
        Assert.False(AppConfig.OverlayChannelsRequireRestart(current, barcodeChannel));
        Assert.False(AppConfig.RequiresCameraRestart(current, barcodeChannel));

        // 只有主摄自己的设置变了才重启主摄
        var mainCameraChanged = new AppConfig { Fps = 30 };
        Assert.True(AppConfig.RequiresCameraRestart(current, mainCameraChanged));
        Assert.False(AppConfig.OverlayChannelsRequireRestart(current, mainCameraChanged));
    }

    /// <summary>
    /// 识别来源通道号归一：0 = 主摄；超出当前路数或那一路上没接设备一律回主摄。
    /// 识别来源是"能不能扫到面单"的关键开关，写错不能变成两边都不识别。
    /// </summary>
    [Fact]
    public void NormalizeBarcodeRecognitionChannel_FallsBackToPrimary()
    {
        var channels = new List<CameraChannelConfig>
        {
            new() { SourceKind = "usb", MonikerString = "相机A" },
            new()
        };

        Assert.Equal(0, AppConfig.NormalizeBarcodeRecognitionChannel(-1, channels));
        Assert.Equal(0, AppConfig.NormalizeBarcodeRecognitionChannel(0, channels));
        Assert.Equal(1, AppConfig.NormalizeBarcodeRecognitionChannel(1, channels));
        // 第二路已经被设成"无"：不能还把它当识别来源，回到主摄
        Assert.Equal(0, AppConfig.NormalizeBarcodeRecognitionChannel(2, channels));
        // 超出路数 → 回主摄
        Assert.Equal(0, AppConfig.NormalizeBarcodeRecognitionChannel(9, channels));
    }

    /// <summary>默认必须是主画面识别，否则升级后所有老用户的面单识别来源会静默改变。</summary>
    [Fact]
    public void BarcodeRecognitionChannelDefaultsToPrimary()
    {
        var config = new AppConfig();

        Assert.Equal(0, config.CameraBarcodeRecognitionChannel);
    }

    /// <summary>
    /// 识别来源永远是"选一路"：多路都提交会让稳定性追踪器在几种画面之间反复归零，
    /// 结果谁都认不出来。所以提交入口必须按来源分流。
    /// </summary>
    [Fact]
    public void BarcodeRecognitionPicksExactlyOneSource()
    {
        string scanner = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Scanner.cs"));
        Assert.Contains("ShouldUseOverlayChannelForBarcode", scanner, StringComparison.Ordinal);
        Assert.Contains("sourceChannelNumber != expectedChannel", scanner, StringComparison.Ordinal);

        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));
        Assert.Contains("ShouldUseOverlayChannelForBarcode", channels, StringComparison.Ordinal);
        // 每一路的采集回调都要把这一路的通道号带进识别入口。
        Assert.Contains("TrySubmitOverlayBarcodeFrame(channel, frame)", channels, StringComparison.Ordinal);
    }

    /// <summary>叠加画面没出帧时必须回退主画面，否则"选了那一路但没连上"= 完全识别不了。</summary>
    [Fact]
    public void BarcodeRecognitionFallsBackWhenOverlayHasNoFrame()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        Assert.Contains("ActiveBarcodeOverlayChannel is { HasFrame: true }", channels, StringComparison.Ordinal);
    }

    /// <summary>
    /// 叠加画面识别框有自己的比例（不能套主画面那套构图），并且要跳过运动门控：
    /// 面单放好后那一幕是静止的，不跳过门控就永远解不出静止条码。
    /// </summary>
    [Fact]
    public void OverlayBarcodeRecognitionUsesCropGeometryAndSkipsMotionGate()
    {
        string scanner = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Scanner.cs"));

        Assert.Contains("GetOverlayGuideGeometry(barcodeChannel.Number)", scanner, StringComparison.Ordinal);
        Assert.Contains("forceDecode: sourceChannelNumber > 0", scanner, StringComparison.Ordinal);

        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));
        Assert.Contains("BarcodeGuideWidthRatio", channels, StringComparison.Ordinal);

        string service = ReadProjectFile(Path.Combine("Services", "CameraBarcodeRecognitionService.cs"));
        Assert.Contains("public bool TrySubmitFrame(Mat frame, bool forceDecode = false)", service, StringComparison.Ordinal);
    }

    /// <summary>
    /// 合成必须挂在处理循环里、预览发布与录像入队之前：预览和录像共用那一帧，
    /// 合成一次两边都有，不能各自去叠一遍。
    /// </summary>
    [Fact]
    public void OverlayIsComposedInsideTheSingleFramePipeline()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));

        int composeIndex = camera.IndexOf(
            "ComposeOverlayChannelsIfNeeded(",
            StringComparison.Ordinal);
        Assert.True(composeIndex >= 0, "处理循环里没有调用画中画合成");
        Assert.Contains(
            "processedFrame",
            camera[composeIndex..(composeIndex + 200)],
            StringComparison.Ordinal);
        Assert.Contains(
            "previewPublishDue",
            camera[composeIndex..(composeIndex + 200)],
            StringComparison.Ordinal);

        int previewIndex = camera.IndexOf(
            "PublishPreviewFrameIfDue(processedFrame, previewResizer, currentFrameCapturedTicks)",
            StringComparison.Ordinal);
        int recorderIndex = camera.IndexOf(
            "TryEnqueueFrameForRecording(processedFrame, currentFrameCapturedTicks)",
            StringComparison.Ordinal);

        Assert.True(previewIndex > composeIndex, "合成必须在预览发布之前");
        Assert.True(recorderIndex > composeIndex, "合成必须在录像入队之前");
    }

    /// <summary>
    /// 水印必须后于画中画绘制：水印承载时间戳与单号，是取证核心，必须永远压在最上层。
    /// 顺序反过来时，用户把画中画拖到右上角就会把水印盖掉。
    /// </summary>
    [Fact]
    public void WatermarkIsDrawnAfterTheOverlay()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));
        int composeIndex = camera.IndexOf(
            "ComposeOverlayChannelsIfNeeded(",
            StringComparison.Ordinal);
        int watermarkIndex = camera.IndexOf(
            "ApplyWatermarkToFrame(processedFrame",
            StringComparison.Ordinal);
        Assert.True(composeIndex >= 0, "处理循环里没有调用画中画合成");
        Assert.True(watermarkIndex >= 0, "处理循环里没有绘制水印");
        Assert.True(watermarkIndex > composeIndex, "水印必须在画中画之后绘制");

        string recording = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Recording.cs"));
        int preWatermarkIndex = recording.IndexOf(
            "ApplyWatermarkToFrame(preFrame",
            StringComparison.Ordinal);
        // 预录帧的副画面在进环形缓存时就贴好了（见 PreRecordFramesAlsoGetTheOverlay），
        // 回灌那一段只补水印 —— 顺序仍然是"先副画面、后水印"。
        Assert.True(preWatermarkIndex >= 0, "预录帧没有画水印");
    }

    /// <summary>
    /// 预录帧也要贴画中画，而且要贴"这一帧采集那一刻"的那一份：
    /// 等到回灌时再贴，只能拿到回灌那一刻的叠加帧 —— 5 秒预录里副画面就剩几帧，看起来一卡一卡。
    /// </summary>
    [Fact]
    public void PreRecordFramesAlsoGetTheOverlay()
    {
        string recording = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Recording.cs"));

        int ringAddIndex = recording.IndexOf("_preRecordRing.Add(", StringComparison.Ordinal);
        Assert.True(ringAddIndex > 0, "找不到预录帧进环形缓存的地方");
        Assert.Contains(
            "storedFrame => ComposeOverlayChannels(storedFrame, _overlayZoomFadePercent)",
            recording[ringAddIndex..(ringAddIndex + 400)],
            StringComparison.Ordinal);

        int flushIndex = recording.IndexOf("private void FlushPreRecordFrames", StringComparison.Ordinal);
        Assert.True(flushIndex > 0, "找不到回灌方法");
        string flushBody = recording[flushIndex..];
        Assert.DoesNotContain("ComposeOverlayChannels", flushBody, StringComparison.Ordinal);
        Assert.Contains("TryEnqueueFrameForRecording(preFrame", flushBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// 副画面重新启动（改设置、休眠唤醒）时，启动之前采到的预录帧里是没有副画面的。
    /// 留着就会在录像开头留下"副摄像头画面直接没了"的接缝，所以启动那一刻要重攒预录缓冲。
    /// </summary>
    [Fact]
    public void OverlayRestart_DropsPreRecordFramesWithoutOverlay()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        int start = channels.IndexOf("internal void StartOverlayChannels()", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到副画面启动入口");
        string startBody = channels[start..(start + 1200)];
        Assert.Contains("ClearPreRecordBuffer()", startBody, StringComparison.Ordinal);
        Assert.Contains("IsRunning", startBody, StringComparison.Ordinal);
    }

    /// <summary>画中画是"独立几路采集"，不能再出现旧的"同一路内嵌小窗"叠加层。</summary>
    [Fact]
    public void LegacyInlineOverlayIsGone()
    {
        string mainWindow = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));
        Assert.DoesNotContain("InlinePipHost", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("InlinePipImage", mainWindow, StringComparison.Ordinal);

        string mainWindowCode = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("InlinePip", mainWindowCode, StringComparison.Ordinal);

        // 主界面依旧不允许新增小窗入口按钮。
        Assert.DoesNotContain("FloatingPreviewButton", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("BtnFloatingPreview_Click", mainWindow, StringComparison.Ordinal);
    }

    /// <summary>每一路独立采集：不能占用主路的帧槽与会话闸门，也不该共用一份运行时状态。</summary>
    [Fact]
    public void OverlayChannelsOwnSeparateCaptureState()
    {
        string source = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        Assert.Contains("private OverlayChannel[] _overlayChannels = Array.Empty<OverlayChannel>();", source, StringComparison.Ordinal);
        Assert.Contains("internal readonly LatestFrameHandoffSlot<Mat> LatestFrame", source, StringComparison.Ordinal);
        Assert.Contains("OnUsbChannelFrame", source, StringComparison.Ordinal);
        Assert.Contains("OnNetworkChannelFrame", source, StringComparison.Ordinal);
        Assert.Contains("TryCompose", source, StringComparison.Ordinal);

        // 每一路不得写主路的帧槽，也不得复用主路的会话闸门。
        Assert.DoesNotContain("_latestCameraFrame", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_previewSessionGate", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 通道列表必须整体替换，不能就地增删。
    ///
    /// 它在 UI 线程（保存设置、休眠唤醒）重建，而处理循环线程和副摄采集线程每帧都要遍历它；
    /// 就地 Clear()/Add() 会被读成半成品，两边同时重建还会留下重复通道 —— 同一台设备被开两次，
    /// 表现就是"画中画没有画面"，得像重启那样重新应用一次设置才好。所以：
    /// 重建要拿 <c>_overlayChannelSyncLock</c>、先在局部拼好再整体赋值，列表上不许再有 Add/Clear。
    /// </summary>
    [Fact]
    public void OverlayChannelListIsSwappedWholeInsteadOfMutatedInPlace()
    {
        string source = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        Assert.Contains("lock (_overlayChannelSyncLock)", source, StringComparison.Ordinal);
        Assert.Contains("_overlayChannels = rebuilt;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlayChannels.Add(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlayChannels.Clear()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlayChannels.ToList()", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 合成写给界面的状态必须整体替换：换落位不能把帧尺寸丢掉，换尺寸也不能把落位丢掉。
    ///
    /// 原来这三样是三个字段各自更新，界面正好落在两次写之间时会拿"新尺寸配旧落位"算出一个
    /// 错误的换算比例，画中画拖动框跳一下。
    /// </summary>
    [Fact]
    public void OverlayGeometrySnapshotKeepsBothHalvesTogether()
    {
        var composed = new CameraOverlayRect(30, 40, 480, 480);

        MainViewModel.OverlayGeometrySnapshot empty = MainViewModel.OverlayGeometrySnapshot.Empty;
        Assert.Null(empty.ComposedRect);
        Assert.Equal(0, empty.SourceWidth);

        MainViewModel.OverlayGeometrySnapshot withSource = empty.WithSourceSize(1080, 1920);
        Assert.Null(withSource.ComposedRect);
        Assert.Equal(1080, withSource.SourceWidth);
        Assert.Equal(1920, withSource.SourceHeight);

        MainViewModel.OverlayGeometrySnapshot withComposition = withSource.WithComposition(composed, 1920, 1080);
        Assert.Equal(composed, withComposition.ComposedRect!.Value);
        Assert.Equal(1920, withComposition.ComposedFrameWidth);
        Assert.Equal(1080, withComposition.ComposedFrameHeight);
        // 换落位没把帧尺寸丢掉
        Assert.Equal(1080, withComposition.SourceWidth);
        Assert.Equal(1920, withComposition.SourceHeight);

        MainViewModel.OverlayGeometrySnapshot resized = withComposition.WithSourceSize(720, 1280);
        // 换尺寸没把落位丢掉
        Assert.Equal(composed, resized.ComposedRect!.Value);
        Assert.Equal(1920, resized.ComposedFrameWidth);
        Assert.Equal(720, resized.SourceWidth);
        Assert.Equal(1280, resized.SourceHeight);
    }

    /// <summary>
    /// 合成要把"这一路的整幅叠加帧 + 裁剪矩形"交给合成器，裁剪在合成器里做。
    ///
    /// 贴片缓存的 key 是（叠加帧对象 + 裁剪矩形 + 落位 + 主帧通道数）。调用方每帧先
    /// <c>new Mat(overlay, cropRect)</c> 再传下去的话，key 每帧都是新对象，缓存永远不命中，
    /// 等于每帧重做一遍缩放与描边 —— 副摄帧率一低，同一份叠加帧本来会被连着合成好几帧，
    /// 那笔开销就白花在采集/处理线程上。
    /// </summary>
    [Fact]
    public void OverlayComposePassesTheWholeFrameSoThePatchCacheCanHit()
    {
        string source = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        // 调用改成多行（多带一个画中画淡出系数），这里盯住"整幅叠加帧 + 裁剪矩形"这两个关键实参
        int composeIndex = source.IndexOf("CameraOverlayComposer.TryCompose(", StringComparison.Ordinal);
        Assert.True(composeIndex > 0, "找不到画中画合成调用");
        string composeCall = source[composeIndex..(composeIndex + 300)];
        Assert.Contains("frame,", composeCall, StringComparison.Ordinal);
        Assert.Contains("overlay,", composeCall, StringComparison.Ordinal);
        Assert.Contains("cropRect,", composeCall, StringComparison.Ordinal);
        Assert.Contains("targetRect,", composeCall, StringComparison.Ordinal);
        Assert.DoesNotContain("new Mat(overlay, cropRect)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 设置页用一套模板渲染所有叠加画面：加第三、第四路只是列表多一项，
    /// 不再复制一段卡片 XAML，也不让被冻结的 SettingsWindow.xaml.cs 继续增长。
    /// </summary>
    [Fact]
    public void SettingsWindow_RendersOverlayChannelsFromOneTemplate()
    {
        string settings = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));

        Assert.Contains("ItemsControl ItemsSource=\"{Binding OverlayCameraCards}\"", settings, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DeviceChoices}\"", settings, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedDevice, Mode=TwoWay}\"", settings, StringComparison.Ordinal);
        // 来源就是开关：选"无"时其余选项整块收起。
        Assert.Contains(
            "Visibility=\"{Binding IsConfigured, Converter={StaticResource BoolToVisibility}}\"",
            settings,
            StringComparison.Ordinal);
        // 路数在高级设置里选，界面不再需要"添加/删除"这种入口。
        Assert.DoesNotContain("添加副画面", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("AddOverlayChannelCommand", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveCommand", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryCameraCheckBox", settings, StringComparison.Ordinal);
        // 叠加画面规格与主摄共用同一套档位枚举：分辨率/帧率下拉由 CameraFormatCatalog 填
        Assert.Contains("ItemsSource=\"{Binding Resolutions}\"", settings, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding FpsOptions}\"", settings, StringComparison.Ordinal);
        // 卡片不再有单独的标题行，靠"副摄像头 N"这一行说明是哪一路。
        Assert.Contains("Text=\"{Binding Title}\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceSummary", settings, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"CameraChannelCards_Loaded\"", settings, StringComparison.Ordinal);
    }

    /// <summary>
    /// 高级设置里"副画面数量"的可选项必须和 AppConfig 的可配置区间一一对应：
    /// 改了常量却忘了改下拉，用户就选不到新增的那几路。
    /// </summary>
    [Fact]
    public void SettingsWindow_ChannelCountOptionsMatchConfiguredRange()
    {
        string settings = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));

        int anchorIndex = settings.IndexOf("AutomationProperties.Name=\"副画面数量\"", StringComparison.Ordinal);
        Assert.True(anchorIndex > 0, "高级设置里找不到“副画面数量”下拉");

        string combo = settings[anchorIndex..];
        combo = combo[..combo.IndexOf("</ComboBox>", StringComparison.Ordinal)];
        for (int count = AppConfig.MinimumOverlayChannelCount;
            count <= AppConfig.MaximumOverlayChannelCount;
            count++)
        {
            Assert.Contains($"Tag=\"{count}\"", combo, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            $"Tag=\"{AppConfig.MaximumOverlayChannelCount + 1}\"",
            combo,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 画中画是直接画进帧里的，界面那个拖动框必须跟着"实际合成落位"重摆：
    /// 少了这条通知，框会停在上一帧的位置，用户看到的就是"主画面上画中画的框偏了"。
    /// </summary>
    [Fact]
    public void OverlayBoxesFollowComposedPlacement()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));
        Assert.Contains("NotifyOverlayPlacementChanged", channels, StringComparison.Ordinal);
        Assert.Contains("OverlayPlacementVersion", channels, StringComparison.Ordinal);

        string window = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"))
            + ReadProjectFile(Path.Combine("UI", "MainWindow.OverlayBoxes.cs"));
        Assert.Contains(
            "nameof(MainViewModel.OverlayPlacementVersion)",
            window,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 拖动框必须按"画面在父容器里的实际位置"换算，不能直接拿画面矩形当父容器坐标：
    /// VideoImage 被 Uniform 居中摆放后，四周留出的黑边同样占父容器坐标；
    /// 少这一步换算，框就会整体偏出画面、压到黑边上（现场反馈"画中画框超出摄像头画面"）。
    /// </summary>
    [Fact]
    public void OverlayBoxMapsIntoTheImageCoordinateSpace()
    {
        string window = ReadProjectFile(Path.Combine("UI", "MainWindow.OverlayBoxes.cs"));

        Assert.Contains("VideoImage.TranslatePoint", window, StringComparison.Ordinal);
        Assert.Contains("overlayHost", window, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "videoRect.X + (rect.X * scale)",
            window,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 取景编辑态必须给出"这一屏在干什么、怎么退出"的提示，并且要有可靠的退出路径：
    /// "完成"按下即生效（实测鼠标捕获会在按下后被释放，等 MouseUp 的按钮点不动），
    /// 外加 Esc 兜底。
    /// </summary>
    [Fact]
    public void OverlayPreviewEditShowsItsOwnHintAndCanBeExited()
    {
        string viewModel = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert.Contains("拖动框调整副摄取景，点完成或按 Esc 退出", viewModel, StringComparison.Ordinal);

        string window = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.Contains("Key.Escape", window, StringComparison.Ordinal);

        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));
        Assert.Contains("ClickMode=\"Press\"", xaml, StringComparison.Ordinal);
        // 这个按钮盖在画面上，不能用透明底：浅色主题的深色文字落在深色画面上会看不见
        int done = xaml.IndexOf("x:Name=\"BtnOverlayPreviewDone\"", StringComparison.Ordinal);
        Assert.True(done > 0, "没找到取景编辑屏的完成按钮");
        string doneBlock = xaml[done..Math.Min(xaml.Length, done + 1200)];
        Assert.Contains("VideoOverlayButtonStyle", doneBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryButtonStyle", doneBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// 小锁只在"主界面上的主摄取景框"上出现：识别来源是叠加画面时框只是状态反馈，
    /// 进副摄取景编辑屏时拖动本来就不受小锁限制，两种情况下都不显示锁。
    /// </summary>
    [Fact]
    public void OverlayGuideHasNoLockWhenRecognitionComesFromOverlay()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));
        Assert.Contains("IsCameraBarcodeGuideLockVisible", channels, StringComparison.Ordinal);
        Assert.Contains(
            "!IsPreviewGuideEditing && !ShouldUseOverlayChannelForBarcode",
            channels,
            StringComparison.Ordinal);

        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));
        Assert.Contains("{Binding IsCameraBarcodeGuideLockVisible}", xaml, StringComparison.Ordinal);
    }

    /// <summary>画中画识别框默认居中、占画面八成半，用户不设置也能直接用。</summary>
    [Fact]
    public void OverlayBarcodeGuideDefaultsToCenteredBox()
    {
        CameraChannelConfig channel = new AppConfig().CameraChannels[0];

        Assert.Equal(AppConfig.DefaultOverlayGuideRatio, channel.BarcodeGuideWidthRatio);
        Assert.Equal(AppConfig.DefaultOverlayGuideRatio, channel.BarcodeGuideHeightRatio);
        Assert.Equal(0.0, channel.BarcodeGuideOffsetX);
        Assert.Equal(0.0, channel.BarcodeGuideOffsetY);
    }

    /// <summary>
    /// 取景框没调过时按"短边居中方形"算：默认就是 1:1 裁剪，这块既是要显示的画面、也是识别范围。
    /// 调过（比例不再是默认值）以后就按用户存的比例走，四个角可以自由改大小。
    /// </summary>
    [Fact]
    public void DefaultOverlayGuide_IsACenteredSquareCrop()
    {
        CameraBarcodeGuideGeometry guide = MainViewModel.ResolveOverlayGuideGeometry(
            new CameraChannelConfig(),
            1280,
            720);

        Assert.Equal(AppConfig.DefaultOverlayGuideRatio * 720 / 1280, guide.WidthRatio, 3);
        Assert.Equal(AppConfig.DefaultOverlayGuideRatio, guide.HeightRatio, 3);
        // 宽高按像素算一样长 = 1:1
        Assert.Equal(guide.WidthRatio * 1280, guide.HeightRatio * 720, 1);
        Assert.Equal(0.0, guide.OffsetX);
        Assert.Equal(0.0, guide.OffsetY);

        var touched = new CameraChannelConfig
        {
            BarcodeGuideWidthRatio = 0.5,
            BarcodeGuideHeightRatio = 0.4
        };
        CameraBarcodeGuideGeometry stored = MainViewModel.ResolveOverlayGuideGeometry(touched, 1280, 720);
        Assert.Equal(0.5, stored.WidthRatio, 3);
        Assert.Equal(0.4, stored.HeightRatio, 3);
    }

    /// <summary>
    /// 小锁的显隐同时取决于"在不在取景编辑屏"和"识别来源是不是副画面"，
    /// 进出取景编辑屏必须重新通知一次显隐，否则绑定会停在主界面那一套状态上。
    /// </summary>
    [Fact]
    public void EnteringOverlayPreviewEdit_RefreshesGuideLockVisibility()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        int enter = channels.IndexOf("internal void EnterOverlayPreviewEdit", StringComparison.Ordinal);
        int exit = channels.IndexOf("internal void ExitOverlayPreviewEdit", StringComparison.Ordinal);
        Assert.True(enter >= 0 && exit > enter, "没找到进入/退出取景编辑的实现");

        string enterBody = channels[enter..exit];
        Assert.Contains("nameof(IsCameraBarcodeGuideLockVisible)", enterBody, StringComparison.Ordinal);
        Assert.Contains("nameof(IsCameraBarcodeGuideEditable)", enterBody, StringComparison.Ordinal);

        int nextMember = channels.IndexOf("\n        internal ", exit, StringComparison.Ordinal);
        string exitBody = nextMember > exit ? channels[exit..nextMember] : channels[exit..];
        Assert.Contains("nameof(IsCameraBarcodeGuideLockVisible)", exitBody, StringComparison.Ordinal);

        // 应用设置（换识别来源）时也要刷新，否则主界面的小锁显隐会停在旧状态。
        string viewModel = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert.Contains("OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));", viewModel, StringComparison.Ordinal);
    }

    /// <summary>
    /// 主界面必须给每一路画中画都生成可拖动的框：画中画是画进帧里的，
    /// 没有它就没法用鼠标调位置；按通道生成，以后加第三、第四路不用改。
    /// </summary>
    [Fact]
    public void MainWindow_ExposesDraggableOverlayBoxes()
    {
        string mainWindow = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));
        Assert.Contains("x:Name=\"OverlayBoxLayer\"", mainWindow, StringComparison.Ordinal);

        string codeBehind = ReadProjectFile(Path.Combine("UI", "MainWindow.OverlayBoxes.cs"));
        // 每一路接了设备的画中画各有一个框，按通道号生成。
        Assert.Contains("VisibleOverlayChannelNumbers", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetOverlayPosition", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SaveOverlayPosition", codeBehind, StringComparison.Ordinal);
        // 拖动框与合成必须用同一套落位算法，不能各算一份。
        Assert.Contains("TryResolveOverlayRect", codeBehind, StringComparison.Ordinal);
        Assert.Contains("EnterOverlayPreviewEdit(channelNumber)", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>
    /// 频道化的门槛：主界面、设置页与运行时都只认"通道"，配置里不该再有第二套写死的字段名。
    /// </summary>
    [Fact]
    public void OverlayFeatureHasNoSecondCameraSpecificConfigFields()
    {
        string config = ReadProjectFile(Path.Combine("Config", "AppConfig.cs"));

        Assert.DoesNotContain("SecondaryCamera", config, StringComparison.Ordinal);
        Assert.Contains("public List<CameraChannelConfig> CameraChannels", config, StringComparison.Ordinal);

        // 运行时也只剩通道口径：加第三、第四路不该再动这些文件的结构。
        foreach (string path in OverlayChannelSources)
        {
            string source = ReadProjectFile(Path.Combine(path.Split('/')));
            Assert.DoesNotContain("SecondaryCamera", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_secondary", source, StringComparison.Ordinal);
        }
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    private static string ReadProjectFile(string[] relativePathParts) =>
        File.ReadAllText(
            Path.Combine([FindRepositoryRoot(), "ExpressPackingMonitoring", .. relativePathParts]),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
