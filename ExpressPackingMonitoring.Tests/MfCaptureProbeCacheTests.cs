using ExpressPackingMonitoring.Services.MediaFoundation;
using System;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// MF 探测缓存：探测要"开设备→等首帧→关设备"，每次启动都做一次会让设备在 1 秒内被开两次，
/// 现场表现就是休眠唤醒后正式源 1.5 秒拿不到帧、被看门狗判成掉线（日志 22:52 / 22:58）。
/// 缓存要保证：同设备同配置只探测一次、配置或设备变了要重探、失败后能主动失效、过期重探。
/// </summary>
public sealed class MfCaptureProbeCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static MfCaptureProbe.Result Usable(string format = "NV12 2560x1440@30") =>
        new(true, format, 2560, 1440, 30, true, "");

    private static MfCaptureProbe.Result Unusable() => MfCaptureProbe.Result.Unusable("没有帧");

    [Fact]
    public void SameDeviceAndConfiguration_ProbesOnlyOnce()
    {
        MfCaptureProbeCache.Clear();
        int calls = 0;
        MfCaptureProbe.Result Probe() =>
            MfCaptureProbeCache.Probe(
                "link-1", 2560, 1440, 30, null,
                (_, _, _, _, _, _) => { calls++; return Usable(); },
                Now,
                Ttl);

        MfCaptureProbe.Result first = Probe();
        MfCaptureProbe.Result second = Probe();

        Assert.True(first.Usable);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DifferentConfiguration_ProbesAgain()
    {
        MfCaptureProbeCache.Clear();
        int calls = 0;
        MfCaptureProbe.Result Probe(int width) =>
            MfCaptureProbeCache.Probe(
                "link-2", width, 1440, 30, null,
                (_, _, _, _, _, _) => { calls++; return Usable(); },
                Now,
                Ttl);

        Probe(2560);
        Probe(1920);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void ExpiredEntry_ProbesAgain()
    {
        MfCaptureProbeCache.Clear();
        int calls = 0;
        MfCaptureProbe.Result Probe(DateTime now) =>
            MfCaptureProbeCache.Probe(
                "link-3", 2560, 1440, 30, null,
                (_, _, _, _, _, _) => { calls++; return Usable(); },
                now,
                Ttl);

        Probe(Now);
        Probe(Now.AddMinutes(11));

        Assert.Equal(2, calls);
    }

    /// <summary>启动失败后必须能清掉这台设备的缓存，下次重新探测（不长期沿用错误结论）。</summary>
    [Fact]
    public void InvalidatedEntry_ProbesAgain()
    {
        MfCaptureProbeCache.Clear();
        int calls = 0;
        MfCaptureProbe.Result Probe() =>
            MfCaptureProbeCache.Probe(
                "link-4", 2560, 1440, 30, null,
                (_, _, _, _, _, _) => { calls++; return Usable(); },
                Now,
                Ttl);

        Probe();
        MfCaptureProbeCache.Invalidate("link-4");
        Probe();

        Assert.Equal(2, calls);
    }

    /// <summary>清除某台设备不该影响别的设备。</summary>
    [Fact]
    public void Invalidate_OnlyAffectsTheGivenDevice()
    {
        MfCaptureProbeCache.Clear();
        int calls = 0;
        MfCaptureProbe.Result Probe(string link) =>
            MfCaptureProbeCache.Probe(
                link, 2560, 1440, 30, null,
                (_, _, _, _, _, _) => { calls++; return Usable(); },
                Now,
                Ttl);

        Probe("link-a");
        Probe("link-b");
        MfCaptureProbeCache.Invalidate("link-a");
        Probe("link-a");
        Probe("link-b");

        Assert.Equal(3, calls);
    }

    /// <summary>"协商成功但不出帧"（虚拟摄像头）的结论也要被缓存并复用，不会退回黑屏。</summary>
    [Fact]
    public void UnusableResult_IsCachedToo()
    {
        MfCaptureProbeCache.Clear();
        int calls = 0;
        MfCaptureProbe.Result Probe() =>
            MfCaptureProbeCache.Probe(
                "virtual-cam", 1920, 1080, 30, null,
                (_, _, _, _, _, _) => { calls++; return Unusable(); },
                Now,
                Ttl);

        Assert.False(Probe().Usable);
        Assert.False(Probe().Usable);
        Assert.Equal(1, calls);
    }
}
