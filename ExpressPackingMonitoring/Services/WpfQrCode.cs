using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 二维码的 WPF 渲染：桌面窗口用的位图二维码，以及注册网页二维码的
/// PNG 输出（与非 Windows 宿主的 SVG 输出分开，Windows 表现保持不变）。
/// </summary>
internal static class WpfQrCode
{
    /// <summary>启动时注册，让网页二维码与手机连接窗口使用同一套 WPF 渲染。</summary>
    internal static void UseWpfRenderer()
        => QrCodeRenderer.UseRenderer(CreatePngDataUri);

    /// <summary>
    /// 程序集一被加载就注册：Windows 上不管是谁在跑（主程序、测试宿主），
    /// 网页二维码都保持原有的 PNG 输出，只有非 Windows 宿主才用内置 SVG。
    /// </summary>
    [ModuleInitializer]
    internal static void RegisterOnLoad() => UseWpfRenderer();

    public static string CreatePngDataUri(string url, int size = 260)
        => $"data:image/png;base64,{Convert.ToBase64String(CreatePng(url, size))}";

    public static BitmapSource CreateBitmap(string url, int size = 260)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("二维码网址不能为空", nameof(url));

        int normalizedSize = Math.Clamp(size, 160, 1024);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = normalizedSize,
                Height = normalizedSize,
                Margin = 2,
                PureBarcode = true
            }
        };

        var pixelData = writer.Write(url);
        var source = BitmapSource.Create(
            pixelData.Width,
            pixelData.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixelData.Pixels,
            pixelData.Width * 4);
        source.Freeze();
        return source;
    }

    public static byte[] CreatePng(string url, int size = 260)
    {
        BitmapSource bitmap = CreateBitmap(url, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
