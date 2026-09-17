using System.Diagnostics;
using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 采集路径的开销守护，跑在 1080p 上。
///
/// 这里不宣称"新转换比旧转换快"——两者最后都落到同一套 OpenCV SIMD 原语上。
/// 新路径省的是**旧路径额外背的那些搬运**：系统 CSC 产出的 6MB 中间 RGB24
/// 要跨 DirectShow 边界交给 AForge、再由 Bitmap.Clone 复制一次才到 Mat。
/// 那两步测不到（发生在我们代码之前），所以这里只守住转换本身别超预算。
///
/// 一个失败的尝试留在这里当记录：手写定点 BT.709 解码实测 3.9ms/帧，
/// 比"OpenCV 解码 + 矩阵校正"的 2.1ms/帧更慢，逐像素托管循环打不过 SIMD。
/// </summary>
public sealed class MfConversionPerformanceTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>
    /// 单帧转换要留足余量：60fps 每帧只有 16.7ms 总预算，
    /// 而转换之后还有录像编码、条码识别、预览缩放要做。
    ///
    /// 阈值按 Debug 构建定：Debug 下 OpenCV 的托管封送开销明显更高，
    /// 实测能到 Release 的两倍多。发布版跑的是 Release，所以这里留够余量，
    /// 目的是抓住"数量级退化"，不是卡精确数值。
    /// </summary>
    [Fact]
    public void StaysWithinFrameBudget()
    {
        byte[] yuy2 = CreateYuy2Frame();
        using var bgr = new Mat(Height, Width, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(yuy2, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            for (int index = 0; index < 5; index++)
                MfFrameConverter.ConvertYuy2(source, Width * 2, Width, Height, bgr, useBt709: true);

            const int iterations = 30;
            var stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++)
                MfFrameConverter.ConvertYuy2(source, Width * 2, Width, Height, bgr, useBt709: true);
            double perFrameMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            Assert.True(
                perFrameMs < 12.0,
                $"1080p YUY2→BGR 转换 {perFrameMs:F3} ms/帧，已接近 60fps 的整帧预算（16.7ms）");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 标清源（不需要 BT.709 校正）必须明显更省：那条分支只解码、不做矩阵变换。
    /// 这条同时证明校正确实是可选的，没有被无条件执行。
    /// </summary>
    [Fact]
    public void SkippingBt709CorrectionIsCheaper()
    {
        byte[] yuy2 = CreateYuy2Frame();
        using var bgr = new Mat(Height, Width, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(yuy2, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            for (int index = 0; index < 5; index++)
            {
                MfFrameConverter.ConvertYuy2(source, Width * 2, Width, Height, bgr, useBt709: true);
                MfFrameConverter.ConvertYuy2(source, Width * 2, Width, Height, bgr, useBt709: false);
            }

            const int iterations = 30;
            var stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++)
                MfFrameConverter.ConvertYuy2(source, Width * 2, Width, Height, bgr, useBt709: false);
            double decodeOnlyMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            stopwatch.Restart();
            for (int index = 0; index < iterations; index++)
                MfFrameConverter.ConvertYuy2(source, Width * 2, Width, Height, bgr, useBt709: true);
            double withCorrectionMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            Assert.True(
                decodeOnlyMs < withCorrectionMs,
                $"只解码 {decodeOnlyMs:F3} ms/帧，解码加校正 {withCorrectionMs:F3} ms/帧，"
                    + "校正似乎没有真正被跳过");
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] CreateYuy2Frame()
    {
        byte[] frame = new byte[Width * 2 * Height];
        // 填一个有色度变化的图案，避免常量数据被优化掉、也更接近真实画面。
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 2;
            for (int x = 0; x < Width * 2; x += 4)
            {
                frame[row + x] = (byte)(16 + (x + y) % 220);
                frame[row + x + 1] = (byte)(64 + y % 128);
                frame[row + x + 2] = (byte)(16 + (x + y + 2) % 220);
                frame[row + x + 3] = (byte)(64 + x % 128);
            }
        }
        return frame;
    }
}
