using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.UI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 指令条码必须"生成出来就能被自己读回来"。主界面显示的指令条码既给摄像头看也给手持扫描枪看，
/// 编码端一旦出错，现场就会出现 CLEAR 读成 CLEAN、CLAER 这类误读。
/// 这里按指令清单逐条重画并真实解码，同时把编码表和标准表逐条比对，任何抄错都过不了。
/// </summary>
[Collection("WPF render tests")]
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

    /// <summary>
    /// 向导里的扫码枪测试条码也是给真扫码枪对着屏幕扫的，必须和指令条码过同一道门禁：
    /// 真正渲染成位图后回读得到，且左右各留标准 10 个模块的静区。
    /// </summary>
    [Fact]
    public void WizardTestBarcode_RendersScannableCodeWithStandardQuietZone()
    {
        RunOnStaThread(() =>
        {
            string payload = FirstUseSetupWizardWindow.BuildTestBarcodeValue(
                new DateTime(2026, 10, 7, 5, 21, 17));
            Assert.Equal("TEST0521", payload);

            BitmapSource image = BarcodeHelper.Generate(
                payload,
                FirstUseSetupWizardWindow.TestBarcodeHeight,
                FirstUseSetupWizardWindow.TestBarcodeModuleWidth,
                // 测试宿主里 Application.MainWindow 可能挂在别的 STA 线程上，跨线程读 DPI 会抛异常；
                // 这里按 100% 缩放渲染，也就是屏幕上最常见的场景。
                dpiScale: 1);

            int moduleWidth = FirstUseSetupWizardWindow.TestBarcodeModuleWidth;
            int stride = image.PixelWidth * 4;
            var pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);

            // 静区：最外侧 10 个模块必须是白底
            int quietZonePixels = BarcodeHelper.QuietZoneModules * moduleWidth;
            Assert.True(image.PixelWidth > quietZonePixels * 2, "条码宽度容不下两侧静区");
            foreach (int y in new[] { 0, image.PixelHeight - 1 })
            {
                for (int x = 0; x < quietZonePixels; x++)
                {
                    Assert.True(
                        IsWhite(pixels, y * stride + x * 4),
                        $"左侧静区第 {x} 列不是白底");
                    Assert.True(
                        IsWhite(pixels, y * stride + (image.PixelWidth - 1 - x) * 4),
                        $"右侧静区第 {x} 列不是白底");
                }
            }

            // 最细的条不能比一个模块还窄，否则现场扫不出来
            Assert.True(
                MeasureMinBarWidth(pixels, stride, image.PixelWidth, image.PixelHeight / 2) >= moduleWidth,
                "条码最细的条比一个模块还窄");

            // 显示尺寸要放得下向导卡片，别把条码挤出可视区
            Assert.True(image.Width <= 620, $"测试条码太宽：{image.Width} DIP");

            int grayStride = image.PixelWidth;
            var gray = new byte[grayStride * image.PixelHeight];
            for (int y = 0; y < image.PixelHeight; y++)
                for (int x = 0; x < image.PixelWidth; x++)
                    gray[y * grayStride + x] = pixels[y * stride + x * 4 + 2];

            Result[]? results = DecodeCode128(gray, image.PixelWidth, image.PixelHeight);
            Assert.NotNull(results);
            Assert.Contains(results, result => string.Equals(result.Text, payload, StringComparison.Ordinal));

            AssertOnScreenRasterStaysCrisp(image, moduleWidth, payload);
        });
    }

    /// <summary>
    /// 位图标准还不够：条码贴到取景卡片里居中时，落点可能是半像素（卡片内宽减条码宽是奇数），
    /// 默认的线性缩放会把 1 个模块的条糊成 2/5/8 像素——位图回读照样过，真扫码枪却扫不出来。
    /// 这里按向导的做法（Stretch=None + NearestNeighbor + 布局取整）把条码真的画一遍，量屏幕像素。
    /// </summary>
    private static void AssertOnScreenRasterStaysCrisp(BitmapSource barcode, int moduleWidth, string payload)
    {
        // 向导里的摆法：卡片里居中（这里故意让差值是奇数，居中后落点正好在半个像素上），
        // 外层布局取整把落点压回整像素，Image 用 NearestNeighbor 关掉插值。
        var centred = new Grid
        {
            Width = barcode.Width + 1,
            Height = barcode.Height + 8,
            Background = Brushes.White,
            UseLayoutRounding = true
        };
        centred.Children.Add(CreateBarcodeImage(barcode));
        AssertRasterStaysCrisp(centred, barcode, moduleWidth, payload);
    }

    private static System.Windows.Controls.Image CreateBarcodeImage(BitmapSource barcode)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = barcode,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return image;
    }

    private static void AssertRasterStaysCrisp(
        Panel host,
        BitmapSource barcode,
        int moduleWidth,
        string payload)
    {
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(new Point(0, 0), new Size(host.Width, host.Height)));
        host.UpdateLayout();

        var image = (System.Windows.Controls.Image)host.Children[0];
        double offset = image.TransformToAncestor(host).Transform(new Point(0, 0)).X;

        var rtb = new RenderTargetBitmap(
            (int)Math.Round(host.Width), (int)Math.Round(host.Height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);

        int stride = rtb.PixelWidth * 4;
        var pixels = new byte[stride * rtb.PixelHeight];
        rtb.CopyPixels(pixels, stride, 0);

        int cropLeft = (int)Math.Round(offset);
        int cropStride = barcode.PixelWidth * 4;
        var onScreen = new byte[cropStride * barcode.PixelHeight];
        for (int y = 0; y < barcode.PixelHeight; y++)
            Array.Copy(pixels, (y + 4) * stride + cropLeft * 4, onScreen, y * cropStride, cropStride);

        int minBar = MeasureMinBarWidth(onScreen, cropStride, barcode.PixelWidth, barcode.PixelHeight / 2);
        Assert.True(
            minBar == moduleWidth,
            $"屏幕像素里最细的条是 {minBar}px，应为 {moduleWidth}px" +
            $"（offset={offset} bitmap={barcode.PixelWidth}x{barcode.PixelHeight} host={host.Width}x{host.Height}" +
            $" bars={DescribeBars(onScreen, cropStride, barcode.PixelWidth, barcode.PixelHeight / 2)}）");

        // 屏幕像素再解一次码：糊掉的条码就算尺寸对，也读不回来
        var gray = new byte[barcode.PixelWidth * barcode.PixelHeight];
        for (int y = 0; y < barcode.PixelHeight; y++)
            for (int x = 0; x < barcode.PixelWidth; x++)
                gray[y * barcode.PixelWidth + x] = onScreen[y * cropStride + x * 4 + 2];

        Assert.Contains(
            DecodeCode128(gray, barcode.PixelWidth, barcode.PixelHeight) ?? [],
            result => string.Equals(result.Text, payload, StringComparison.Ordinal));
    }

    private static bool IsWhite(byte[] pixels, int index) => pixels[index + 2] > 200;

    private static string DescribeBars(byte[] bgra, int stride, int width, int y)
    {
        var runs = new List<(bool Black, int Len)>();
        bool? current = null;
        int len = 0;
        for (int x = 0; x < width; x++)
        {
            bool black = bgra[y * stride + x * 4 + 2] < 128;
            if (current == null) { current = black; len = 1; }
            else if (current == black) len++;
            else { runs.Add((current.Value, len)); current = black; len = 1; }
        }
        runs.Add((current!.Value, len));

        string bars = string.Join(",", runs.Where(run => run.Black)
            .GroupBy(run => run.Len).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}px x{group.Count()}"));
        return $"lead={runs[0].Len} {bars}";
    }

    /// <summary>量一行里最细的黑条由几个像素组成</summary>
    private static int MeasureMinBarWidth(byte[] pixels, int stride, int width, int y)
    {
        int minBar = int.MaxValue;
        int run = 0;
        for (int x = 0; x < width; x++)
        {
            bool isBlack = pixels[y * stride + x * 4 + 2] < 128;
            if (isBlack)
            {
                run++;
            }
            else if (run > 0)
            {
                minBar = Math.Min(minBar, run);
                run = 0;
            }
        }

        if (run > 0)
            minBar = Math.Min(minBar, run);

        Assert.True(minBar != int.MaxValue, "渲染结果里没有找到黑条");
        return minBar;
    }

    /// <summary>WPF 渲染必须在 STA 线程，且要能解析到 App 的资源（BarcodeHelper 取画刷）</summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                    _ = new Application();
                LoadAppResources();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException($"测试条码渲染失败：{failure}");
        }
    }

    /// <summary>与 App.xaml 相同的合并顺序，否则条码画刷取不到</summary>
    private static void LoadAppResources()
    {
        string[] files =
        [
            "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
            "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
            "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
        ];

        var merged = new ResourceDictionary();
        foreach (string file in files)
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        if (Application.Current != null)
            Application.Current.Resources = merged;
    }
}
