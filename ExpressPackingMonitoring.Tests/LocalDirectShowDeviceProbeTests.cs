using AForge.Video.DirectShow;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>列出本机 DirectShow 采集设备的序号与 moniker，实测时用来精确指定摄像头。</summary>
public sealed class LocalDirectShowDeviceProbeTests
{
    private readonly ITestOutputHelper _output;

    public LocalDirectShowDeviceProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReportsLocalDirectShowDevices()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") != "1")
        {
            _output.WriteLine("skipped: set PACKINGPROOF_CAPTURE_PROBE=1 to run local capture probes");
            return;
        }

        var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
        _output.WriteLine($"DirectShow devices: {devices.Count}");
        for (int index = 0; index < devices.Count; index++)
            _output.WriteLine($"  [{index}] {devices[index].Name} :: {devices[index].MonikerString}");
    }
}
