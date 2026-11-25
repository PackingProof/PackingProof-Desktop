using ExpressPackingMonitoring.Audio;
using System.Reflection;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WindowsTtsBridgeTests
{
    [Fact]
    public void Bridge_LoadsHelperAndSynthesizesOnModernWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return;

        using WindowsTtsBridge? bridge = WindowsTtsBridge.TryCreate();
        Assert.NotNull(bridge);

        bool ok = bridge!.TrySynthesize("测试语音", isWarning: false, speed: 1.0f, out byte[] wavData);
        Assert.True(ok, "Windows 系统语音合成应可用");
        Assert.True(wavData.Length > 44);
    }

    [Fact]
    public void Bridge_SynthesizesWhenCreatedOnUiThreadAndUsedBySpeechThread()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return;

        WindowsTtsBridge? bridge = null;
        Exception? creationError = null;
        var creation = new Thread(() =>
        {
            try
            {
                bridge = WindowsTtsBridge.TryCreate();
            }
            catch (Exception ex)
            {
                creationError = ex;
            }
        })
        {
            IsBackground = true
        };
        creation.SetApartmentState(ApartmentState.STA);
        creation.Start();
        Assert.True(creation.Join(TimeSpan.FromSeconds(30)));
        if (creationError != null)
            throw creationError;
        Assert.NotNull(bridge);

        bool ok = false;
        byte[] wavData = [];
        Exception? synthesisError = null;
        var synthesis = new Thread(() =>
        {
            try
            {
                ok = bridge!.TrySynthesize(
                    "这是一段语音播报测试",
                    isWarning: false,
                    speed: 1.0f,
                    out wavData);
            }
            catch (Exception ex)
            {
                synthesisError = ex;
            }
        })
        {
            IsBackground = true
        };
        synthesis.SetApartmentState(ApartmentState.MTA);
        synthesis.Start();
        Assert.True(synthesis.Join(TimeSpan.FromSeconds(30)));
        if (synthesisError != null)
            throw synthesisError;

        bridge.Dispose();
        Assert.True(ok, "Windows 系统语音应能跨创建线程使用");
        Assert.True(wavData.Length > 44);
    }

    [Fact]
    public void TwoSpeechServices_UseTheSameWindowsTtsProjection()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return;

        WindowsTtsBridge? first = WindowsTtsBridge.TryCreate();
        Assert.NotNull(first);
        Assert.True(
            first!.TrySynthesize(
                "主界面系统语音。",
                isWarning: false,
                speed: 1.0f,
                out byte[] firstWav));
        Assert.True(firstWav.Length > 44);

        WindowsTtsBridge? second = WindowsTtsBridge.TryCreate();
        Assert.Same(first, second);
        Assert.True(
            second!.TrySynthesize(
                "设置页语音预览。",
                isWarning: false,
                speed: 1.0f,
                out byte[] secondWav),
            second.LastError);
        Assert.True(secondWav.Length > 44);
    }

    [Fact]
    public async Task ExistingSharedBridge_DoesNotBlockOnTheSynthesisLock()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            return;

        WindowsTtsBridge? bridge = WindowsTtsBridge.TryCreate();
        Assert.NotNull(bridge);
        FieldInfo? sharedSync = typeof(WindowsTtsBridge).GetField(
            "SharedSync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sharedSync);

        object sync = sharedSync!.GetValue(null)!;
        Monitor.Enter(sync);
        try
        {
            Task<WindowsTtsBridge?> fastPath = Task.Run(WindowsTtsBridge.TryCreate);
            WindowsTtsBridge? result = await fastPath.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            Assert.Same(bridge, result);
        }
        finally
        {
            Monitor.Exit(sync);
        }
    }
}
