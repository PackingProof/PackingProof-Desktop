using ExpressPackingMonitoring.Helpers;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BarcodeDisplayTests
{
    [Fact]
    public void Code128_UsesRecommendedQuietZoneOnBothSides()
    {
        Assert.Equal(10, BarcodeHelper.QuietZoneModules);

        int modules = BarcodeHelper.CalculateTotalModules([104, 35, 36, 33, 50, 52, 0]);

        Assert.Equal(111, modules);
    }

    [Theory]
    [InlineData(1.0, 3)]
    [InlineData(1.25, 3)]
    [InlineData(1.5, 4)]
    [InlineData(2.0, 6)]
    public void Code128_ModuleEdgesAlignToPhysicalPixels(
        double dpiScale,
        int expectedPhysicalModuleWidth)
    {
        var metrics = BarcodeHelper.CalculateRasterMetrics(110, 52, 3, dpiScale);

        Assert.Equal(110 * expectedPhysicalModuleWidth, metrics.PixelWidth);
        Assert.Equal(expectedPhysicalModuleWidth, metrics.ModuleWidthDip * dpiScale, 6);
        Assert.Equal(metrics.PixelHeight, metrics.HeightDip * dpiScale, 6);
    }

    [Fact]
    public void MainWindow_CommandBarcodesUseNativePixelSize()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement[] barcodeImages = document
            .Descendants(presentation + "Image")
            .Where(element =>
                element.Attribute("Source")?.Value is "{Binding Barcode1Image}" or "{Binding Barcode2Image}")
            .ToArray();

        Assert.Equal(2, barcodeImages.Length);
        Assert.All(barcodeImages, image =>
        {
            Assert.Equal("None", image.Attribute("Stretch")?.Value);
            Assert.Equal("Center", image.Attribute("HorizontalAlignment")?.Value);
            Assert.Equal("Center", image.Attribute("VerticalAlignment")?.Value);
            Assert.Equal("True", image.Attribute("SnapsToDevicePixels")?.Value);
        });

        string scannerSource = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Scanner.cs"));
        Assert.Contains("BarcodeHelper.Generate(cmd1, 52, 3)", scannerSource);
        Assert.Contains("BarcodeHelper.Generate(cmd2, 52, 3)", scannerSource);
    }

    [Fact]
    public void CommandBarcodeLabelsUseConciseLocalizedActions()
    {
        XDocument chinese = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "Resources",
            "Strings.zh-Hans.resx"));
        XDocument english = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "Resources",
            "Strings.resx"));

        Assert.Equal("扫码清空输入", GetResource(chinese, "Main.BarcodeClear"));
        Assert.Equal("扫码切换退货", GetResource(chinese, "Main.BarcodeReturn"));
        Assert.Equal("扫码切换发货", GetResource(chinese, "Main.BarcodeShipping"));
        Assert.Equal("扫码停止录像", GetResource(chinese, "Main.BarcodeStop"));
        Assert.Equal("扫码开始录像", GetResource(chinese, "Main.BarcodeStart"));
        Assert.Equal("Scan: Clear input", GetResource(english, "Main.BarcodeClear"));
        Assert.Equal("Scan: Return mode", GetResource(english, "Main.BarcodeReturn"));
        Assert.Equal("Scan: Shipping mode", GetResource(english, "Main.BarcodeShipping"));
        Assert.Equal("Scan: Stop recording", GetResource(english, "Main.BarcodeStop"));
        Assert.Equal("Scan: Start recording", GetResource(english, "Main.BarcodeStart"));

        string mainWindow = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml"));
        Assert.Equal(2, mainWindow.Split("FontSize=\"13\" FontWeight=\"SemiBold\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, mainWindow.Split("Height=\"78\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("CornerRadius=\"10\" Padding=\"12\"", mainWindow);
    }

    private static string GetResource(XDocument document, string name) =>
        document.Root!
            .Elements("data")
            .Single(element => element.Attribute("name")?.Value == name)
            .Element("value")!
            .Value;

    private static string FindRepositoryFile(params string[] parts)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                string solution = Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln");
                if (File.Exists(solution))
                    return Path.Combine([directory.FullName, .. parts]);
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("找不到解决方案根目录");
    }
}
