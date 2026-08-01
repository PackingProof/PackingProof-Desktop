using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraFrameOrientationTests
{
    [Fact]
    public void Apply_WhenDisabled_PreservesFrame()
    {
        using var frame = CreateTestFrame();

        CameraFrameOrientation.Apply(frame, rotate180: false);

        Assert.Equal(1, frame.At<byte>(0, 0));
        Assert.Equal(4, frame.At<byte>(1, 1));
    }

    [Fact]
    public void Apply_WhenEnabled_RotatesFrameWithoutChangingSize()
    {
        using var frame = CreateTestFrame();

        CameraFrameOrientation.Apply(frame, rotate180: true);

        Assert.Equal(2, frame.Rows);
        Assert.Equal(2, frame.Cols);
        Assert.Equal(4, frame.At<byte>(0, 0));
        Assert.Equal(3, frame.At<byte>(0, 1));
        Assert.Equal(2, frame.At<byte>(1, 0));
        Assert.Equal(1, frame.At<byte>(1, 1));
    }

    [Fact]
    public void Settings_ShowsRotationActionDirectlyInMainLabel()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml"));

        Assert.Contains("Text=\"画面旋转 180°\"", xaml, StringComparison.Ordinal);
        Assert.Contains("摄像头倒装时开启，预览、识别和录像会同步旋转", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"摄像头画面方向\"", xaml, StringComparison.Ordinal);
    }

    private static Mat CreateTestFrame()
    {
        var frame = new Mat(2, 2, MatType.CV_8UC1);
        frame.Set(0, 0, (byte)1);
        frame.Set(0, 1, (byte)2);
        frame.Set(1, 0, (byte)3);
        frame.Set(1, 1, (byte)4);
        return frame;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine([current.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
