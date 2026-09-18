using ExpressPackingMonitoring.Helpers;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 指令条码必须"生成出来就能被自己读回来"。主界面显示的清除/切发货/切退货/开录/停录条码
/// 既给摄像头看也给手持扫描枪看，编码端一旦出错，现场就会出现 CLEAR 读成 CLEAN、CLAER 这类误读。
/// 这里直接拿渲染用的模块序列重画一遍再解码，验证的是真正会显示出去的那份编码。
/// </summary>
public sealed class BarcodeRenderRoundTripTests
{
    private const int ModulePixels = 3;
    private const int BarcodeHeight = 52;

    [Theory]
    [InlineData("CLEAR")]
    [InlineData("SHIP")]
    [InlineData("BACK")]
    [InlineData("START")]
    [InlineData("STOP")]
    public void CommandBarcode_DecodesBackToItsPayload(string payload)
    {
        (byte[] gray, int width) = RenderModules(payload);

        Result[]? results = DecodeCode128(gray, width, BarcodeHeight);
        Assert.NotNull(results);
        Assert.Contains(results!, result => string.Equals(result.Text, payload, StringComparison.Ordinal));
    }

    /// <summary>
    /// 整张编码表逐字符回读。Code 128 每个字符必须正好 11 个模块，
    /// 任何一条图案抄错都会让含该字符的指令码在现场读不出来。
    /// </summary>
    [Fact]
    public void AllPrintableCharacters_RoundTripThroughTheAppDecoder()
    {
        var failed = new List<string>();
        for (char value = ' '; value <= '~'; value++)
        {
            string payload = value.ToString();
            (byte[] gray, int width) = RenderModules(payload);
            Result[]? results = DecodeCode128(gray, width, BarcodeHeight);
            if (results == null || !results.Any(result => result.Text == payload))
                failed.Add($"{value}({(int)value})");
        }

        Assert.True(failed.Count == 0, "这些字符的条码自家解码器读不回来: " + string.Join(",", failed));
    }

    private static (byte[] Gray, int Width) RenderModules(string payload)
    {
        List<(bool IsBar, int Width)> modules = BarcodeHelper.BuildModules(payload);
        int quietZoneModules = BarcodeHelper.QuietZoneModules;
        int totalModules = quietZoneModules * 2 + modules.Sum(module => module.Width);
        int width = totalModules * ModulePixels;
        byte[] gray = Enumerable.Repeat((byte)255, width * BarcodeHeight).ToArray();

        int x = quietZoneModules * ModulePixels;
        foreach ((bool isBar, int moduleWidth) in modules)
        {
            int barWidth = moduleWidth * ModulePixels;
            if (isBar)
            {
                for (int y = 0; y < BarcodeHeight; y++)
                {
                    int rowStart = y * width + x;
                    for (int i = 0; i < barWidth; i++)
                        gray[rowStart + i] = 0;
                }
            }
            x += barWidth;
        }

        return (gray, width);
    }

    /// <summary>用项目自己的解码配置（ZXing + TryHarder + Code128）读回来</summary>
    private static Result[]? DecodeCode128(byte[] gray, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.CODE_128 }
            }
        };
        return reader.DecodeMultiple(
            new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8));
    }
}
