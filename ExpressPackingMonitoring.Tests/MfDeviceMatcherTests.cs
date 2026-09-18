using ExpressPackingMonitoring.Services.MediaFoundation;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// DirectShow moniker 与 Media Foundation 符号链接的映射。
///
/// 现有配置存的是 MonikerString（用户换设备、重启都靠它认设备），不能改；
/// 所以新后端要打开"用户选中的那一台"，必须靠这层映射。配不上就回退旧后端。
/// </summary>
public sealed class MfDeviceMatcherTests
{
    /// <summary>两套 API 只差一个前缀，剥掉之后应当完全一致。</summary>
    [Fact]
    public void ExtractsSameKeyFromMonikerAndSymbolicLink()
    {
        const string tail = @"\\?\usb#vid_046d&pid_0825#abc123#{65e8773d-8f56-11d0-a3b9-00a0c9223196}";
        string fromMoniker = MfDeviceMatcher.ExtractDeviceInstanceKey("@device:pnp:" + tail);
        string fromSymbolicLink = MfDeviceMatcher.ExtractDeviceInstanceKey(tail);

        Assert.Equal(fromMoniker, fromSymbolicLink);
        Assert.NotEmpty(fromMoniker);
    }

    /// <summary>大小写不同的同一台设备要配上：两套 API 的大小写写法不一致。</summary>
    [Fact]
    public void MatchesIgnoringCase()
    {
        const string tail = @"\\?\USB#VID_046D&PID_0825#ABC123#{65E8773D-8F56-11D0-A3B9-00A0C9223196}";
        var devices = new[] { new MfCaptureDevice("摄像头", tail.ToLowerInvariant()) };

        MfCaptureDevice? matched = MfDeviceMatcher.FindByMoniker("@device:pnp:" + tail, devices);

        Assert.NotNull(matched);
    }

    /// <summary>
    /// DirectShow 与 Media Foundation 对同一物理摄像头会挂不同的接口类 GUID，
    /// 但实例路径主体相同；接口 GUID 不能参与匹配，否则新后端永远不会启动。
    /// </summary>
    [Fact]
    public void MatchesWhenInterfaceClassGuidDiffers()
    {
        const string moniker =
            @"@device:pnp:\\?\usb#vid_046d&pid_0825#abc123#{65e8773d-8f56-11d0-a3b9-00a0c9223196}";
        const string symbolicLink =
            @"\\?\usb#vid_046d&pid_0825#abc123#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
        var devices = new[] { new MfCaptureDevice("摄像头", symbolicLink) };

        MfCaptureDevice? matched = MfDeviceMatcher.FindByMoniker(moniker, devices);

        Assert.Same(devices[0], matched);
    }

    /// <summary>
    /// 同型号的两台设备必须按实例路径区分开，不能按名字 ——
    /// 名字完全一样（"Iriun Webcam" 与 "Iriun Webcam #2" 只是显示名加后缀）。
    /// </summary>
    [Fact]
    public void DistinguishesIdenticalNamesByInstancePath()
    {
        var devices = new[]
        {
            new MfCaptureDevice("Iriun Webcam", @"\\?\usb#vid_1234&pid_0001#aaa#{guid}"),
            new MfCaptureDevice("Iriun Webcam", @"\\?\usb#vid_1234&pid_0001#bbb#{guid}"),
        };

        MfCaptureDevice? matched = MfDeviceMatcher.FindByMoniker(
            @"@device:pnp:\\?\usb#vid_1234&pid_0001#bbb#{guid}",
            devices);

        Assert.NotNull(matched);
        Assert.Equal(@"\\?\usb#vid_1234&pid_0001#bbb#{guid}", matched!.SymbolicLink);
    }

    /// <summary>
    /// 软件设备（OBS 虚拟摄像头这类）只存在于 DirectShow，MF 根本枚举不到，
    /// 必须判定为配不上并回退 —— 这不是缺陷，是事实。
    ///
    /// 实测本机配置的就是 OBS 虚拟摄像头：
    /// @device:sw:{860bb310-...}\{a3fce0f5-3493-419f-958a-aba1250ec20b}
    /// </summary>
    [Fact]
    public void TreatsSoftwareOnlyDevicesAsUnmatched()
    {
        const string obsVirtualCamera =
            @"@device:sw:{860bb310-5d01-11d0-bd3b-00a0c911ce86}\{a3fce0f5-3493-419f-958a-aba1250ec20b}";
        var devices = new[] { new MfCaptureDevice("真实摄像头", @"\\?\usb#vid_1234#aaa#{guid}") };

        Assert.True(MfDeviceMatcher.IsSoftwareOnlyDevice(obsVirtualCamera));
        Assert.Null(MfDeviceMatcher.FindByMoniker(obsVirtualCamera, devices));
        Assert.False(MfDeviceMatcher.IsSoftwareOnlyDevice(@"@device:pnp:\\?\usb#vid_1234#aaa#{guid}"));
    }

    /// <summary>配不上时返回 null，让调用方回退旧后端，而不是随便挑一台。</summary>
    [Fact]
    public void ReturnsNullWhenNoDeviceMatches()
    {
        var devices = new[] { new MfCaptureDevice("别的设备", @"\\?\usb#vid_9999&pid_9999#zzz#{guid}") };

        Assert.Null(MfDeviceMatcher.FindByMoniker(
            @"@device:pnp:\\?\usb#vid_1234&pid_0001#aaa#{guid}",
            devices));
    }

    /// <summary>空输入不能抛，也不能配上任何设备。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void HandlesMissingMoniker(string? moniker)
    {
        var devices = new[] { new MfCaptureDevice("摄像头", @"\\?\usb#vid_1234#aaa#{guid}") };

        Assert.Null(MfDeviceMatcher.FindByMoniker(moniker, devices));
        Assert.Empty(MfDeviceMatcher.ExtractDeviceInstanceKey(moniker));
    }

    /// <summary>
    /// 真机验证：系统里每一台 MF 设备的符号链接，都能被"加上 moniker 前缀"后重新配回自己。
    /// 这条证明映射规则对现场设备真实成立，而不只是对我构造的字符串成立。
    /// </summary>
    [Fact]
    public void MatchesRealDevicesRoundTrip()
    {
        using MfPlatform? platform = MfPlatform.TryStart();
        if (platform == null)
            return;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            return;

        foreach (MfCaptureDevice device in devices)
        {
            MfCaptureDevice? matched = MfDeviceMatcher.FindByMoniker(
                "@device:pnp:" + device.SymbolicLink,
                devices);

            Assert.NotNull(matched);
            Assert.Equal(device.SymbolicLink, matched!.SymbolicLink);
        }
    }
}

/// <summary>采集后端的选择策略。底线是绝不能因为后端问题录不了像。</summary>
public sealed class CameraBackendPolicyTests
{
    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("乱写的值", "auto")]
    [InlineData("mediafoundation", "mediafoundation")]
    [InlineData("mf", "mediafoundation")]
    [InlineData("directshow", "directshow")]
    [InlineData("aforge", "directshow")]
    public void NormalizesConfiguredMode(string? configured, string expected)
    {
        Assert.Equal(expected, CameraBackendPolicy.Normalize(configured));
    }

    /// <summary>探测到真实首帧时才用新后端。</summary>
    [Fact]
    public void UsesMediaFoundationOnlyWhenProbeSucceeded()
    {
        Assert.Equal(
            CameraBackendKind.MediaFoundation,
            CameraBackendPolicy.Decide("auto", probeUsable: true));
        Assert.Equal(
            CameraBackendKind.DirectShow,
            CameraBackendPolicy.Decide("auto", probeUsable: false));
    }

    /// <summary>
    /// 即使用户强制指定新后端，探测失败也要回退：
    /// 录像取证软件宁可用旧后端，也不能因为配置项让用户录不了像。
    /// </summary>
    [Fact]
    public void FallsBackEvenWhenMediaFoundationForced()
    {
        Assert.Equal(
            CameraBackendKind.DirectShow,
            CameraBackendPolicy.Decide("mediafoundation", probeUsable: false));
    }

    /// <summary>用户强制旧后端时连探测都不做，省掉两次开设备。</summary>
    [Fact]
    public void SkipsProbeWhenDirectShowForced()
    {
        Assert.True(CameraBackendPolicy.IsMediaFoundationDisabled("directshow"));
        Assert.False(CameraBackendPolicy.IsMediaFoundationDisabled("auto"));
        Assert.Equal(
            CameraBackendKind.DirectShow,
            CameraBackendPolicy.Decide("directshow", probeUsable: true));
    }
}
