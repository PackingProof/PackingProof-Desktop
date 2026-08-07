using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WinRtPlacementServiceTests : IDisposable
{
    private readonly string _baseDir;

    public WinRtPlacementServiceTests()
    {
        _baseDir = Path.Combine(
            Path.GetTempPath(),
            "winrt-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    [Fact]
    public void Win7Mode_MovesWinRtFilesIntoDisabledFolder()
    {
        foreach (string file in WinRtFileNames())
            File.WriteAllText(Path.Combine(_baseDir, file), "x");

        WinRtPlacementService.Apply(_baseDir, modernWindows: false);

        string disabledDir = Path.Combine(_baseDir, "winrt-disabled");
        foreach (string file in WinRtFileNames())
        {
            Assert.False(File.Exists(Path.Combine(_baseDir, file)));
            Assert.True(File.Exists(Path.Combine(disabledDir, file)));
        }
    }

    [Fact]
    public void Win7Mode_IsIdempotent()
    {
        File.WriteAllText(Path.Combine(_baseDir, "WinRT.Runtime.dll"), "x");

        WinRtPlacementService.Apply(_baseDir, modernWindows: false);
        WinRtPlacementService.Apply(_baseDir, modernWindows: false);

        string disabledDir = Path.Combine(_baseDir, "winrt-disabled");
        Assert.False(File.Exists(Path.Combine(_baseDir, "WinRT.Runtime.dll")));
        Assert.True(File.Exists(Path.Combine(disabledDir, "WinRT.Runtime.dll")));
    }

    [Fact]
    public void ModernMode_RestoresDisabledFilesAndCleansFolder()
    {
        string disabledDir = Path.Combine(_baseDir, "winrt-disabled");
        Directory.CreateDirectory(disabledDir);
        foreach (string file in WinRtFileNames())
            File.WriteAllText(Path.Combine(disabledDir, file), "x");

        WinRtPlacementService.Apply(_baseDir, modernWindows: true);

        foreach (string file in WinRtFileNames())
            Assert.True(File.Exists(Path.Combine(_baseDir, file)));
        Assert.False(Directory.Exists(disabledDir));
    }

    [Fact]
    public void ModernMode_LeavesFilesUntouchedWhenAlreadyInPlace()
    {
        foreach (string file in WinRtFileNames())
            File.WriteAllText(Path.Combine(_baseDir, file), "x");

        WinRtPlacementService.Apply(_baseDir, modernWindows: true);

        foreach (string file in WinRtFileNames())
            Assert.True(File.Exists(Path.Combine(_baseDir, file)));
        Assert.False(Directory.Exists(Path.Combine(_baseDir, "winrt-disabled")));
    }

    private static string[] WinRtFileNames() =>
        new[]
        {
            "ExpressPackingMonitoring.WinTts.dll",
            "Microsoft.Windows.SDK.NET.dll",
            "WinRT.Runtime.dll"
        };

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }
}
