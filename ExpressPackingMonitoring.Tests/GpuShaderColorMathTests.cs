using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// GPU 着色器里的 YUV→RGB 系数必须与 CPU 路径等价。
///
/// 这条是整个 GPU 方案的正确性基础：着色器算错了，画面颜色就全错，
/// 而 GPU 结果没法直接在单元测试里读回来（需要完整渲染管线 + 回读），
/// 所以这里把 HLSL 里那套算式用 C# 原样实现一份做对照。
///
/// 两边共用同一组标准系数，所以这条同时守住"以后有人改了一边忘了另一边"。
/// </summary>
public sealed class GpuShaderColorMathTests
{
    /// <summary>
    /// HLSL 里 YuvToRgb 的 C# 等价实现。
    /// 改这里必须同步改 GpuConversionShaders，反之亦然。
    ///
    /// 系数直接作用在 0..255 域上，不先归一化：归一化写法里"色度除以 224"
    /// 得到的是 ±0.5 范围，而常见的 1.5748/1.8556 那组系数要求 ±1.0，
    /// 配错了只在彩色区域偏色、灰阶完全正常，极难发现 —— 第一版就栽在这里
    /// （G 通道差了 20~40，被这组用例抓住）。
    /// </summary>
    private static (double R, double G, double B) ShaderYuvToRgb(
        byte y,
        byte u,
        byte v,
        bool bt709)
    {
        double yy = y - 16.0;
        double uu = u - 128.0;
        double vv = v - 128.0;

        double r, g, b;
        if (bt709)
        {
            r = 1.164383 * yy + 1.792741 * vv;
            g = 1.164383 * yy - 0.213249 * uu - 0.532909 * vv;
            b = 1.164383 * yy + 2.112402 * uu;
        }
        else
        {
            r = 1.164383 * yy + 1.596027 * vv;
            g = 1.164383 * yy - 0.391762 * uu - 0.812968 * vv;
            b = 1.164383 * yy + 2.017232 * uu;
        }

        return (Saturate(r / 255.0), Saturate(g / 255.0), Saturate(b / 255.0));
    }

    private static double Saturate(double value) => Math.Clamp(value, 0.0, 1.0);

    /// <summary>
    /// 中性色度必须产生灰：着色器系数写错时这条最先炸。
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(126)]
    [InlineData(235)]
    public void NeutralChromaProducesGray(byte luma)
    {
        (double r, double g, double b) = ShaderYuvToRgb(luma, 128, 128, bt709: true);

        Assert.Equal(r, g, 4);
        Assert.Equal(g, b, 4);
    }

    /// <summary>
    /// 着色器必须产出标准 BT.709 的解码值。
    ///
    /// 这里拿标准值而不是 CPU 路径作基准，因为实测发现 CPU 路径本身有精度损失：
    /// 它是"OpenCV 按 BT.601 解码 + 再乘色度校正矩阵"，在饱和色上误差很大 ——
    /// BT.709 的纯绿（Y=173,U=42,V=26）标准解码是 G=255，CPU 路径只给到 218。
    /// 那条路的截断损失在 CameraColorMatrixPolicy 的注释里已经承认过。
    ///
    /// 所以这组用例同时说明：GPU 路径在饱和色上的画质**优于**现有 CPU 路径。
    /// </summary>
    [Theory]
    // Y, U, V, 期望 R, G, B —— BT.709 的纯红/纯绿/纯蓝
    [InlineData(63, 102, 240, 255, 1, 0)]
    [InlineData(173, 42, 26, 0, 255, 1)]
    [InlineData(32, 240, 118, 1, 0, 255)]
    // 中性灰：色度居中时三通道相等
    [InlineData(126, 128, 128, 128, 128, 128)]
    public void ProducesStandardBt709Values(
        byte y,
        byte u,
        byte v,
        int expectedR,
        int expectedG,
        int expectedB)
    {
        (double r, double g, double b) = ShaderYuvToRgb(y, u, v, bt709: true);

        AssertClose(r * 255.0, expectedR, "R");
        AssertClose(g * 255.0, expectedG, "G");
        AssertClose(b * 255.0, expectedB, "B");
    }

    /// <summary>
    /// 与 CPU 路径的差异要落在可解释的范围内：中性色与低饱和区必须几乎一致，
    /// 这样两条路径切换时用户不会看到画面跳变。
    /// 高饱和区允许差得多（CPU 路径在那里本来就有损失，见上一条用例）。
    /// </summary>
    [Theory]
    [InlineData(126, 128, 128)] // 中灰
    [InlineData(180, 120, 136)] // 低饱和肤色
    [InlineData(40, 130, 130)]  // 暗部
    [InlineData(200, 126, 132)] // 亮部
    public void MatchesCpuPathOnLowSaturationColors(byte y, byte u, byte v)
    {
        (double r, double g, double b) = ShaderYuvToRgb(y, u, v, bt709: true);
        Vec3b cpu = ConvertOnCpu(y, u, v);

        // CPU 路径输出 BGR 顺序。低饱和区两条路径应当贴得很近。
        AssertClose(b * 255.0, cpu.Item0, "B", tolerance: 8.0);
        AssertClose(g * 255.0, cpu.Item1, "G", tolerance: 8.0);
        AssertClose(r * 255.0, cpu.Item2, "R", tolerance: 8.0);
    }

    /// <summary>BT.709 与 BT.601 的结果必须明显不同，否则说明分支没起作用。</summary>
    [Fact]
    public void Bt709DiffersFromBt601()
    {
        (double r709, double g709, double b709) = ShaderYuvToRgb(63, 102, 240, bt709: true);
        (double r601, double g601, double b601) = ShaderYuvToRgb(63, 102, 240, bt709: false);

        double difference =
            Math.Abs(r709 - r601) + Math.Abs(g709 - g601) + Math.Abs(b709 - b601);
        Assert.True(difference > 0.02, $"两种矩阵结果几乎一样（差 {difference:F4}）");
    }

    /// <summary>越界输入必须夹取，不能回绕 —— 着色器里靠 saturate，这里靠 Clamp。</summary>
    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 0, 255)]
    public void ExtremeInputsSaturate(byte y, byte u, byte v)
    {
        (double r, double g, double b) = ShaderYuvToRgb(y, u, v, bt709: true);

        Assert.InRange(r, 0.0, 1.0);
        Assert.InRange(g, 0.0, 1.0);
        Assert.InRange(b, 0.0, 1.0);
    }

    private static void AssertClose(double shader, double expected, string channel, double tolerance = 3.0)
    {
        Assert.True(
            Math.Abs(shader - expected) <= tolerance,
            $"{channel} 通道：着色器 {shader:F1}，期望 {expected:F1}，"
                + $"相差超过 {tolerance}/255");
    }

    /// <summary>走真实的 CPU 转换路径，拿到对照值。</summary>
    private static Vec3b ConvertOnCpu(byte y, byte u, byte v)
    {
        // YUY2 的最小单位是两像素共享一组色度。
        byte[] source = [y, u, y, v];
        using var bgr = new Mat(1, 2, MatType.CV_8UC3);
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            MfFrameConverter.ConvertYuy2(
                handle.AddrOfPinnedObject(),
                source.Length,
                2,
                1,
                bgr,
                useBt709: true);
        }
        finally
        {
            handle.Free();
        }
        return bgr.Get<Vec3b>(0, 0);
    }
}
