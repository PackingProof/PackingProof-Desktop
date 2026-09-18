using ExpressPackingMonitoring.Helpers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 打印页必须"印出来就能扫"。这里验证排版放得下、尺寸够大，
/// 并把打印页真正渲染成位图后逐条回读条码，防止排版改动把条码挤小或裁掉。
/// </summary>
public sealed class BarcodePrintLayoutTests
{
    /// <summary>直接取指令清单：以后新增指令会自动纳入打印页与解码守卫</summary>
    private static IReadOnlyList<BarcodePrintService.BarcodePrintItem> CommandBarcodes =>
        BarcodeCommandCatalog.ResolveAll();

    [Fact]
    public void CommandCatalog_CoversDesktopAndPhoneCommands()
    {
        string[] payloads = BarcodeCommandCatalog.Commands.Select(command => command.Payload).ToArray();

        Assert.Equal(
            ["CLEAR", "BACK", "SHIP", "START", "STOP", "FLASH"],
            payloads);
    }

    [Fact]
    public void ImageFileName_UsesPayloadAndSanitizedLabel()
    {
        string fileName = BarcodePrintService.BuildImageFileName(
            new BarcodePrintService.BarcodePrintItem("扫码\n切换发货", "SHIP"));
        Assert.Equal("SHIP-扫码切换发货.png", fileName);

        string blankLabel = BarcodePrintService.BuildImageFileName(
            new BarcodePrintService.BarcodePrintItem("", "FLASH"));
        Assert.Equal("FLASH.png", blankLabel);
    }

    [Fact]
    public void WholeCommandSet_FitsOnOneA4Page()
    {
        Assert.True(BarcodePrintLayout.FitsOnOnePage(CommandBarcodes.Count));
        Assert.True(BarcodePrintLayout.GetRequiredHeight(CommandBarcodes.Count) <= BarcodePrintLayout.A4Height);
    }

    [Fact]
    public void PrintedModuleWidth_IsLargeEnoughForWallScanning()
    {
        // 4 DIP ≈ 1.06 毫米，低于 3 DIP（0.79 毫米）贴在墙上就开始难扫
        Assert.True(BarcodePrintLayout.ModuleWidth >= 3);
    }

    [Fact]
    public void EveryCommandBarcode_StaysInsidePrintableWidth()
    {
        double printableWidth = BarcodePrintLayout.A4Width - BarcodePrintLayout.Margin * 2;

        foreach (BarcodePrintService.BarcodePrintItem item in CommandBarcodes)
            Assert.True(
                BarcodePrintLayout.GetBarcodeWidth(item.Payload) <= printableWidth,
                $"{item.Payload} 打印宽度超出纸张");
    }

    [Fact]
    public void RowTops_FollowRowHeight()
    {
        IReadOnlyList<double> tops = BarcodePrintLayout.GetRowTops(CommandBarcodes.Count);

        Assert.Equal(CommandBarcodes.Count, tops.Count);
        Assert.Equal(BarcodePrintLayout.Margin + BarcodePrintLayout.HeaderHeight, tops[0], 3);
        for (int index = 1; index < tops.Count; index++)
            Assert.Equal(BarcodePrintLayout.GetRowHeight(), tops[index] - tops[index - 1], 3);
    }

    [Fact]
    public void WriteAll_CreatesEveryImageAndOverwritesExistingOnes()
    {
        RunOnStaThread(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), $"packingproof-barcodes-{Guid.NewGuid():N}");
            try
            {
                int written = BarcodePrintService.WriteAll(folder, CommandBarcodes, "指令条码", "打印说明");
                Assert.Equal(CommandBarcodes.Count, written);
                foreach (BarcodePrintService.BarcodePrintItem item in CommandBarcodes)
                    Assert.True(File.Exists(Path.Combine(folder, BarcodePrintService.BuildImageFileName(item))));

                // 再生成一次必须覆盖旧文件：先弄脏一张，重生成后应恢复原大小
                string dirty = Path.Combine(folder, BarcodePrintService.BuildImageFileName(CommandBarcodes[0]));
                long originalLength = new FileInfo(dirty).Length;
                File.WriteAllText(dirty, "dirty");

                int rewritten = BarcodePrintService.WriteAll(folder, CommandBarcodes, "指令条码", "打印说明");

                Assert.Equal(CommandBarcodes.Count, rewritten);
                Assert.Equal(originalLength, new FileInfo(dirty).Length);
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
        });
    }

    [Fact]
    public void SingleBarcodeImage_IsHighResolutionAndScannable()
    {
        RunOnStaThread(() =>
        {
            var item = new BarcodePrintService.BarcodePrintItem("扫码切换发货", "SHIP");
            BitmapSource image = BarcodePrintService.RenderSingle(item, "指令条码", "打印说明");

            Assert.Equal(300, image.DpiX, 3);
            Assert.True(image.PixelWidth > 1000, $"另存图片太窄: {image.PixelWidth}");

            int stride = image.PixelWidth * 4;
            var pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);

            double scale = 300 / 96.0;
            string? decoded = DecodeBarcode(
                pixels,
                stride,
                image.PixelWidth,
                left: (int)(BarcodePrintLayout.Margin * scale),
                top: (int)((BarcodePrintLayout.Margin
                    + BarcodePrintLayout.HeaderHeight
                    + BarcodePrintLayout.LabelHeight) * scale),
                width: (int)(BarcodePrintLayout.GetBarcodeWidth(item.Payload) * scale),
                height: (int)(BarcodePrintLayout.BarcodeHeight * scale));

            Assert.Equal(item.Payload, decoded);
        });
    }

    [Fact]
    public void RenderedPrintPage_IsScannableForEveryCommand()
    {
        RunOnStaThread(() =>
        {
            const double dpi = 96;
            DrawingVisual page = BarcodePrintService.BuildPage(
                CommandBarcodes,
                BarcodePrintLayout.A4Width,
                BarcodePrintLayout.A4Height,
                "指令条码",
                "打印时请选择 100% 比例，不要缩放");

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(BarcodePrintLayout.A4Width),
                (int)Math.Ceiling(BarcodePrintLayout.A4Height),
                dpi,
                dpi,
                PixelFormats.Pbgra32);
            bitmap.Render(page);

            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            IReadOnlyList<double> rowTops = BarcodePrintLayout.GetRowTops(CommandBarcodes.Count);
            for (int index = 0; index < CommandBarcodes.Count; index++)
            {
                BarcodePrintService.BarcodePrintItem item = CommandBarcodes[index];
                string? decoded = DecodeBarcode(
                    pixels,
                    stride,
                    bitmap.PixelWidth,
                    left: (int)BarcodePrintLayout.Margin,
                    top: (int)(rowTops[index] + BarcodePrintLayout.LabelHeight),
                    width: (int)Math.Ceiling(BarcodePrintLayout.GetBarcodeWidth(item.Payload)),
                    height: (int)BarcodePrintLayout.BarcodeHeight);

                Assert.Equal(item.Payload, decoded);
            }
        });
    }

    private static string? DecodeBarcode(
        byte[] pixels,
        int stride,
        int sourceWidth,
        int left,
        int top,
        int width,
        int height)
    {
        var gray = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceRow = (top + y) * stride;
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = sourceRow + (left + x) * 4;
                int targetIndex = y * width + x;
                gray[targetIndex] = sourceIndex + 2 < pixels.Length
                    ? pixels[sourceIndex + 2]
                    : (byte)255;
            }
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.CODE_128 }
            }
        };
        Result[]? results = reader.DecodeMultiple(
            new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8));

        return results?.FirstOrDefault()?.Text;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            throw new Xunit.Sdk.XunitException($"打印页渲染失败：{detail}");
        }
    }
}
