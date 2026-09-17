using ExpressPackingMonitoring.Services.MediaFoundation;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 后端选择的端到端验证：从 moniker 出发，走完整的"匹配设备 → 探测首帧 → 决定后端"。
///
/// 这是接入 StartCamera 的那条链路，必须保证两件事：
/// 真实设备能走上新后端；任何一步不成立都干净回退，绝不因为后端问题录不了像。
/// </summary>
public sealed class CameraBackendSelectionTests
{
    /// <summary>
    /// 真实 USB 摄像头应当能走新后端。
    ///
    /// 这条同时守住了三件事：moniker 映射对现场设备成立、探测能拿到真实首帧、
    /// 决策会选中 Media Foundation。任何一环坏掉这里都会失败。
    /// </summary>
    [Fact]
    public void RealDeviceSelectsMediaFoundationBackend()
    {
        using MfPlatform? platform = MfPlatform.TryStart();
        if (platform == null)
            return;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            return;

        var failures = new List<string>();
        foreach (MfCaptureDevice device in devices)
        {
            // 模拟配置里存的 moniker：现场配置存的就是这个形状。
            string moniker = "@device:pnp:" + device.SymbolicLink;
            MfCaptureDevice? matched = MfDeviceMatcher.FindByMoniker(moniker, devices);
            if (matched == null)
            {
                failures.Add($"[{device.Name}] moniker 配不上");
                continue;
            }

            MfCaptureProbe.Result probe = MfCaptureProbe.Probe(
                matched.SymbolicLink,
                1920,
                1080,
                30,
                "auto");
            if (!probe.Usable)
            {
                failures.Add($"[{device.Name}] {probe.Failure}");
                continue;
            }

            Assert.Equal(
                CameraBackendKind.MediaFoundation,
                CameraBackendPolicy.Decide("auto", probe.Usable));
            return;
        }

        Assert.Fail("没有任何设备能走上新后端：" + string.Join("；", failures));
    }

    /// <summary>
    /// OBS 虚拟摄像头这类软件设备必须回退旧后端。
    ///
    /// 实测本机配置的正是它：MF 枚举不到 DirectShow 的软件设备，
    /// 硬走新后端的话摄像头直接打不开。
    /// </summary>
    [Fact]
    public void SoftwareOnlyDeviceFallsBackToDirectShow()
    {
        const string obsVirtualCamera =
            @"@device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";

        using MfPlatform? platform = MfPlatform.TryStart();
        IReadOnlyList<MfCaptureDevice> devices = platform == null
            ? []
            : MfCaptureDevice.Enumerate();

        Assert.Null(MfDeviceMatcher.FindByMoniker(obsVirtualCamera, devices));
        // 配不上就等同于探测失败，决策必须落到 DirectShow。
        Assert.Equal(
            CameraBackendKind.DirectShow,
            CameraBackendPolicy.Decide("auto", probeUsable: false));
    }

    /// <summary>MF 完全不可用的机器（裁剪镜像、N 版 Windows）上也要能正常回退。</summary>
    [Fact]
    public void FallsBackWhenMediaFoundationUnavailable()
    {
        // 设备列表为空时任何 moniker 都配不上，决策落到 DirectShow。
        Assert.Null(MfDeviceMatcher.FindByMoniker(@"@device:pnp:\\?\usb#vid_1234#aaa#{guid}", []));
        Assert.Equal(
            CameraBackendKind.DirectShow,
            CameraBackendPolicy.Decide("auto", probeUsable: false));
    }
}
