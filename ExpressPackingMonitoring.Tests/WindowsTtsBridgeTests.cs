using ExpressPackingMonitoring.Audio;
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

        bool ok = bridge!.TrySynthesize("测试语音", isWarning: false, out byte[] wavData);
        Assert.True(ok, "Windows 系统语音合成应可用");
        Assert.True(wavData.Length > 44);
    }
}
