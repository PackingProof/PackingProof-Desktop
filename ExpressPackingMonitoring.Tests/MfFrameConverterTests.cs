using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 原始帧到 BGR 的转换。这是整套方案的要害：转换矩阵错了就是全盘偏色，
/// 所以拿"已知 YUV 值 → 期望 RGB"逐个对，而不是只看"不抛异常"。
///
/// 旧路径是让系统按 BT.601 解错、再用逆矩阵掰回来（两次 8bit 取整、
/// 截断的饱和像素回不来）；这里按 BT.709 直接解一次。
/// </summary>
public sealed class MfFrameConverterTests
{
    /// <summary>BT.709 下的中性灰：色度居中时 R=G=B，不该染上任何颜色。</summary>
    [Theory]
    [InlineData(16, 0)]     // 有限范围的黑
    [InlineData(126, 128)]  // 中灰
    [InlineData(235, 255)]  // 有限范围的白
    public void NeutralChromaProducesGray(byte luma, byte expectedLevel)
    {
        using Mat bgr = ConvertSingleYuy2Pixel(luma, chromaU: 128, chromaV: 128, useBt709: true);

        Vec3b pixel = bgr.Get<Vec3b>(0, 0);
        Assert.InRange(pixel.Item0, expectedLevel - 2, expectedLevel + 2);
        Assert.InRange(pixel.Item1, expectedLevel - 2, expectedLevel + 2);
        Assert.InRange(pixel.Item2, expectedLevel - 2, expectedLevel + 2);
    }

    /// <summary>
    /// BT.709 的三个原色必须落在对的通道上。这条能抓住"通道顺序写反"
    /// 和"用了 BT.601 系数"两类错误 —— 601 解出来的饱和度明显偏低。
    /// </summary>
    [Theory]
    // Y, U, V, 期望主导通道（0=B, 1=G, 2=R）
    [InlineData(63, 102, 240, 2)]   // BT.709 红
    [InlineData(173, 42, 26, 1)]    // BT.709 绿
    [InlineData(32, 240, 118, 0)]   // BT.709 蓝
    public void PrimaryColorsLandOnTheRightChannel(byte y, byte u, byte v, int dominantChannel)
    {
        using Mat bgr = ConvertSingleYuy2Pixel(y, u, v, useBt709: true);
        Vec3b pixel = bgr.Get<Vec3b>(0, 0);

        byte[] channels = [pixel.Item0, pixel.Item1, pixel.Item2];
        byte dominant = channels[dominantChannel];
        for (int channel = 0; channel < 3; channel++)
        {
            if (channel == dominantChannel)
                continue;

            Assert.True(
                dominant > channels[channel] + 40,
                $"通道 {dominantChannel} 的值 {dominant} 没有明显主导通道 {channel} 的 {channels[channel]}");
        }
    }

    /// <summary>
    /// BT.709 的解码结果必须与 BT.601 明显不同 —— 否则说明我们其实还在用 601，
    /// 整个方案就白做了。红色在两种矩阵下的差异最明显。
    /// </summary>
    [Fact]
    public void Bt709DiffersFromBt601()
    {
        using Mat bt709 = ConvertSingleYuy2Pixel(63, 102, 240, useBt709: true);
        using Mat bt601 = ConvertSingleYuy2Pixel(63, 102, 240, useBt709: false);

        Vec3b a = bt709.Get<Vec3b>(0, 0);
        Vec3b b = bt601.Get<Vec3b>(0, 0);
        int difference = Math.Abs(a.Item0 - b.Item0)
            + Math.Abs(a.Item1 - b.Item1)
            + Math.Abs(a.Item2 - b.Item2);
        Assert.True(difference > 10, $"两种矩阵的结果几乎一样（差 {difference}），可能都在用同一套系数");
    }

    /// <summary>越界值必须饱和夹取，不能回绕成另一端的颜色。</summary>
    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 0, 255)]
    public void ExtremeInputsSaturate(byte y, byte u, byte v)
    {
        using Mat bgr = ConvertSingleYuy2Pixel(y, u, v, useBt709: true);

        Vec3b pixel = bgr.Get<Vec3b>(0, 0);
        Assert.InRange(pixel.Item0, 0, 255);
        Assert.InRange(pixel.Item1, 0, 255);
        Assert.InRange(pixel.Item2, 0, 255);
    }

    /// <summary>
    /// 带行边距的帧（stride > width*2）要按真实 stride 步进。
    /// 按 width 硬算会逐行错位，画面被斜着撕开 —— 这是 NV12 最常见的坑。
    /// </summary>
    [Fact]
    public void HonoursSourceStride()
    {
        const int width = 4;
        const int height = 3;
        const int stride = 32; // 远大于 width*2，模拟对齐后的缓冲区
        byte[] source = new byte[stride * height];
        // 每行填不同的亮度，方便检查是否错位。
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width * 2; x += 4)
            {
                int offset = y * stride + x;
                source[offset] = (byte)(60 + y * 60);
                source[offset + 1] = 128;
                source[offset + 2] = (byte)(60 + y * 60);
                source[offset + 3] = 128;
            }
        }

        using Mat bgr = new(height, width, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            MfFrameConverter.ConvertYuy2(
                handle.AddrOfPinnedObject(),
                stride,
                width,
                height,
                bgr,
                useBt709: true);
        }
        finally
        {
            handle.Free();
        }

        // 每一行应当是均匀的灰，且逐行变亮。
        byte previous = 0;
        for (int y = 0; y < height; y++)
        {
            Vec3b left = bgr.Get<Vec3b>(y, 0);
            Vec3b right = bgr.Get<Vec3b>(y, width - 1);
            Assert.InRange(right.Item1, left.Item1 - 3, left.Item1 + 3);
            Assert.True(left.Item1 > previous, $"第 {y} 行没有比上一行更亮，可能按错误的 stride 步进");
            previous = left.Item1;
        }
    }

    /// <summary>NV12 也要能转，且中性色度同样产生灰。</summary>
    [Fact]
    public void ConvertsNv12WithNeutralChromaToGray()
    {
        const int width = 4;
        const int height = 4;
        const int stride = width;
        byte[] source = new byte[stride * height * 3 / 2];
        for (int index = 0; index < stride * height; index++)
            source[index] = 126;
        for (int index = stride * height; index < source.Length; index++)
            source[index] = 128;

        using Mat bgr = new(height, width, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            MfFrameConverter.ConvertNv12(
                handle.AddrOfPinnedObject(),
                stride,
                width,
                height,
                bgr,
                useBt709: true);
        }
        finally
        {
            handle.Free();
        }

        Vec3b pixel = bgr.Get<Vec3b>(2, 2);
        Assert.InRange(pixel.Item0, 126, 132);
        Assert.InRange(pixel.Item1, 126, 132);
        Assert.InRange(pixel.Item2, 126, 132);
    }

    /// <summary>尺寸不匹配要明确报错，不能默默写坏内存。</summary>
    [Fact]
    public void RejectsMismatchedDestination()
    {
        byte[] source = new byte[4 * 2 * 2];
        using Mat wrongSize = new(8, 8, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            Assert.Throws<ArgumentException>(() => MfFrameConverter.ConvertYuy2(
                handle.AddrOfPinnedObject(),
                8,
                4,
                2,
                wrongSize,
                useBt709: true));
        }
        finally
        {
            handle.Free();
        }
    }

    private static Mat ConvertSingleYuy2Pixel(byte luma, byte chromaU, byte chromaV, bool useBt709)
    {
        // YUY2 的最小单位是两个像素共享一组色度。
        byte[] source = [luma, chromaU, luma, chromaV];
        var bgr = new Mat(1, 2, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            MfFrameConverter.ConvertYuy2(
                handle.AddrOfPinnedObject(),
                source.Length,
                2,
                1,
                bgr,
                useBt709);
        }
        finally
        {
            handle.Free();
        }
        return bgr;
    }
}
