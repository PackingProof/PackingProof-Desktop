using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 摄像头帧转 Mat 必须无损：GDI+ 的 DrawImage 默认带半像素偏移，会把每一帧重新插值一遍，
/// 预览就会"每帧糊一点、颜色偏一点"，镜像套镜像时特别明显。
/// 这里用相邻像素差异最大的棋盘验证转换后逐像素不变。
/// </summary>
public sealed class CameraFrameConverterTests
{
    [Fact]
    public void ConvertsThirtyTwoBppCheckerboardWithoutBleedingNeighbours()
    {
        using var source = new Bitmap(4, 2, PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.SetPixel(
                    x,
                    y,
                    (x + y) % 2 == 0
                        ? Color.FromArgb(255, 255, 0, 0)
                        : Color.FromArgb(255, 0, 0, 255));
            }
        }

        using Mat mat = CameraFrameConverter.ConvertToBgrMat(source);

        Assert.Equal(4, mat.Width);
        Assert.Equal(2, mat.Height);
        Assert.Equal(MatType.CV_8UC3, mat.Type());

        var pixel = mat.GetGenericIndexer<Vec3b>();
        // OpenCvSharp 的索引器是 [行, 列] = [y, x]。红（BGR: 0,0,255）与蓝（255,0,0）交错：
        // 一旦发生重采样，值就会被邻居污染。
        Assert.Equal(new Vec3b(0, 0, 255), pixel[0, 0]);
        Assert.Equal(new Vec3b(255, 0, 0), pixel[0, 1]);
        Assert.Equal(new Vec3b(0, 0, 255), pixel[0, 2]);
        Assert.Equal(new Vec3b(255, 0, 0), pixel[1, 0]);
        Assert.Equal(new Vec3b(0, 0, 255), pixel[1, 1]);
    }

    [Fact]
    public void KeepsTwentyFourBppPixelValuesExactly()
    {
        using var source = new Bitmap(2, 1, PixelFormat.Format24bppRgb);
        source.SetPixel(0, 0, Color.FromArgb(10, 20, 30));
        source.SetPixel(1, 0, Color.FromArgb(200, 100, 50));

        using Mat mat = CameraFrameConverter.ConvertToBgrMat(source);

        var pixel = mat.GetGenericIndexer<Vec3b>();
        Assert.Equal(new Vec3b(30, 20, 10), pixel[0, 0]);
        Assert.Equal(new Vec3b(50, 100, 200), pixel[0, 1]);
    }

    /// <summary>像素格式与尺寸都要照搬，转出来的 Mat 不能缩放。</summary>
    [Fact]
    public void PreservesFrameSizeForOddWidths()
    {
        using var source = new Bitmap(3, 5, PixelFormat.Format32bppArgb);

        using Mat mat = CameraFrameConverter.ConvertToBgrMat(source);

        Assert.Equal(3, mat.Width);
        Assert.Equal(5, mat.Height);
    }
}
