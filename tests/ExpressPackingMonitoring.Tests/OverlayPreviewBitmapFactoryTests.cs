using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class OverlayPreviewBitmapFactoryTests(ITestOutputHelper output)
{
    [Fact]
    public void FullHdFrameIsDownscaledToTheDisplayWidth()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(30, 60, 90));

        System.Windows.Media.Imaging.BitmapSource bitmap =
            OverlayPreviewBitmapFactory.Create(frame, displayWidth: 960);

        Assert.Equal(960, bitmap.PixelWidth);
        Assert.Equal(540, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 3];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 3, 0);
        Assert.Equal((byte)30, pixels[0]);
        Assert.Equal((byte)60, pixels[1]);
        Assert.Equal((byte)90, pixels[2]);
    }

    [Fact]
    public void UnknownDisplayWidthKeepsTheSourceSize()
    {
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);

        System.Windows.Media.Imaging.BitmapSource bitmap =
            OverlayPreviewBitmapFactory.Create(frame, displayWidth: 0);

        Assert.Equal(1280, bitmap.PixelWidth);
        Assert.Equal(720, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
    }

    [Fact]
    public void NonContinuousRoiAtBottomEdgeIsCopiedToContinuousBuffer()
    {
        using var storage = new Mat(40, 52, MatType.CV_8UC3, Scalar.Black);
        using var roi = new Mat(storage, new Rect(3, 16, 32, 24));
        roi.SetTo(new Scalar(10, 20, 30));

        System.Windows.Media.Imaging.BitmapSource bitmap =
            OverlayPreviewBitmapFactory.Create(roi, displayWidth: 0);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 3];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 3, 0);
        Assert.Equal(32, bitmap.PixelWidth);
        Assert.Equal(24, bitmap.PixelHeight);
        Assert.Equal((byte)10, pixels[0]);
        Assert.Equal((byte)20, pixels[1]);
        Assert.Equal((byte)30, pixels[2]);
    }

    [Fact]
    public void BitmapCopiesPixelsInsteadOfReferencingTheSourceBuffer()
    {
        const int width = 4;
        const int height = 2;
        const int stride = width * 3;
        int length = stride * height;
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
        for (int index = 0; index < length; index += 3)
        {
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, index, 30);
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, index + 1, 60);
            System.Runtime.InteropServices.Marshal.WriteByte(buffer, index + 2, 90);
        }
        try
        {
            using var frame = Mat.FromPixelData(
                height,
                width,
                MatType.CV_8UC3,
                buffer,
                stride);
            System.Windows.Media.Imaging.BitmapSource bitmap =
                OverlayPreviewBitmapFactory.Create(frame, displayWidth: 0);

            for (int index = 0; index < length; index++)
                System.Runtime.InteropServices.Marshal.WriteByte(buffer, index, 0xFF);

            var pixels = new byte[length];
            bitmap.CopyPixels(pixels, stride, 0);
            Assert.Equal((byte)30, pixels[0]);
            Assert.Equal((byte)60, pixels[1]);
            Assert.Equal((byte)90, pixels[2]);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void RejectsAFrameThatIsNotBgr24()
    {
        using var frame = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);

        Assert.Throws<ArgumentException>(() =>
            OverlayPreviewBitmapFactory.Create(frame, displayWidth: 160));
    }

    [Fact]
    public void FullHdPreviewDoesNotAllocateTheFrameAsAManagedArray()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(30, 60, 90));
        _ = OverlayPreviewBitmapFactory.Create(frame, displayWidth: 960);

        const int iterations = 5;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
            _ = OverlayPreviewBitmapFactory.Create(frame, displayWidth: 960);
        elapsed.Stop();
        long allocatedPerFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        output.WriteLine($"Full HD overlay preview managed allocation: {allocatedPerFrame:N0} bytes/frame");
        output.WriteLine($"Full HD overlay preview conversion: {elapsed.Elapsed.TotalMilliseconds / iterations:F2} ms/frame");

        Assert.True(
            allocatedPerFrame < 1024 * 1024,
            $"每帧托管分配仍有 {allocatedPerFrame:N0} 字节，疑似又走了整帧 byte[]");
    }
}
