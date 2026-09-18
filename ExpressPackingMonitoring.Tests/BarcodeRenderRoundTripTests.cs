using ExpressPackingMonitoring.Helpers;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 指令条码必须"生成出来就能被自己读回来"。主界面显示的指令条码既给摄像头看也给手持扫描枪看，
/// 编码端一旦出错，现场就会出现 CLEAR 读成 CLEAN、CLAER 这类误读。
/// 这里按指令清单逐条重画并真实解码，同时把编码表和标准表逐条比对，任何抄错都过不了。
/// </summary>
public sealed class BarcodeRenderRoundTripTests
{
    private const int ModulePixels = 3;
    private const int BarcodeHeight = 52;

    /// <summary>标准 Code 128 图案表（下标即编码值，Stop 单列）</summary>
    private static readonly string[] CanonicalPatterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232"
    ];

    /// <summary>每一条指令都必须能被自家解码器读回来；新增指令会自动纳入这条守卫</summary>
    [Fact]
    public void EveryCommandBarcode_DecodesBackToItsPayload()
    {
        Assert.NotEmpty(BarcodeCommandCatalog.Commands);

        var failed = new List<string>();
        foreach ((string _, string payload) in BarcodeCommandCatalog.Commands)
        {
            (byte[] gray, int width) = RenderModules(payload);
            Result[]? results = DecodeCode128(gray, width, BarcodeHeight);
            if (results == null || !results.Any(result => string.Equals(result.Text, payload, StringComparison.Ordinal)))
                failed.Add(payload);
        }

        Assert.True(failed.Count == 0, "这些指令条码自家解码器读不回来: " + string.Join(",", failed));
    }

    /// <summary>
    /// 编码表必须与标准 Code 128 表逐条一致。现场把 CLEAR 读成 CLEAN、CLAER，
    /// 根因就是这张表里六条图案的第四段宽度被抄成了 2。
    /// </summary>
    [Fact]
    public void PatternTable_MatchesCanonicalCode128Table()
    {
        IReadOnlyList<int[]> patterns = BarcodeHelper.EnumeratePatterns();

        Assert.Equal(CanonicalPatterns.Length, patterns.Count);
        for (int code = 0; code < CanonicalPatterns.Length; code++)
        {
            string actual = string.Concat(patterns[code]);
            Assert.True(
                string.Equals(actual, CanonicalPatterns[code], StringComparison.Ordinal),
                $"编码值 {code} 的图案是 {actual}，标准表是 {CanonicalPatterns[code]}");
        }
    }

    /// <summary>Code 128 每个编码字符固定 11 个模块，Stop 固定 13 个模块</summary>
    [Fact]
    public void PatternTable_UsesElevenModulesPerCharacter()
    {
        IReadOnlyList<int[]> patterns = BarcodeHelper.EnumeratePatterns();

        for (int code = 0; code < patterns.Count; code++)
            Assert.True(patterns[code].Sum() == 11, $"编码值 {code} 的模块数不是 11");

        Assert.Equal(13, BarcodeHelper.EnumerateStopPattern().Sum());
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
