using ExpressPackingMonitoring.Services.Gpu;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class GpuPreviewResizeTests
{
    [Theory]
    [InlineData(1920, 1080, 1488, 837)]
    [InlineData(1920, 1080, 640, 360)]
    [InlineData(641, 481, 337, 253)]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(1920, 1080, 912, 513)]
    [InlineData(1920, 1080, 1904, 1071)]
    public void ResizeMatchesCpuIncludingPaddedRows(int width, int height, int targetWidth, int targetHeight)
    {
        using var converter = GpuFrameConverter.TryCreate(width, height, targetWidth, targetHeight,
            isNv12: false, isBgr24: true);
        Assert.NotNull(converter);
        using var storage = new Mat(height + 2, width + 7, MatType.CV_8UC3);
        using var source = new Mat(storage, new Rect(3, 1, width, height));
        byte[] bytes = new byte[width * height * 3];
        new Random(42).NextBytes(bytes);
        using var packed = Mat.FromPixelData(height, width, MatType.CV_8UC3, bytes);
        packed.CopyTo(source);
        using var outputStorage = new Mat(targetHeight + 2, targetWidth + 5, MatType.CV_8UC3);
        using var actual = new Mat(outputStorage, new Rect(2, 1, targetWidth, targetHeight));
        using var expected = new Mat();
        Cv2.Resize(source, expected, new Size(targetWidth, targetHeight),
            interpolation: GpuPreviewResizer.ResolveInterpolation(width, height, targetWidth, targetHeight));
        Assert.True(converter.TryRender(source.Data, (int)source.Step(), false));
        Assert.True(converter.TryReadBackInto(actual));
        Assert.True(Cv2.Norm(actual, expected, NormTypes.INF) <= 1,
            "GPU 缩放与 CPU 输出的逐通道误差应不超过 1/255");
    }
}
