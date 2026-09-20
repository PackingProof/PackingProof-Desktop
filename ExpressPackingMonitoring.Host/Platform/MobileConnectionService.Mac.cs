using System.Text;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 二维码在 Mac 主机上的实现。Windows 端把 ZXing 像素渲染成 WPF 位图再编码 PNG，
/// 这里直接输出 SVG data URI：不依赖任何图像库，浏览器显示和手机扫码都正常。
/// </summary>
internal static class MobileConnectionService
{
    public static string CreateQrDataUri(string url, int size = 260)
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
        string svg = BuildSvg(pixelData.Width, pixelData.Height, pixelData.Pixels);
        return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
    }

    /// <summary>每个模块输出一个方块，矢量结果放大也不会糊。</summary>
    private static string BuildSvg(int width, int height, byte[] pixels)
    {
        var builder = new StringBuilder();
        builder.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" shape-rendering=\"crispEdges\">");
        builder.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>");
        builder.Append("<path fill=\"#000000\" d=\"");
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                if (!IsDark(pixels, width, x, y))
                {
                    x++;
                    continue;
                }

                int runStart = x;
                while (x < width && IsDark(pixels, width, x, y)) x++;
                builder.Append($"M{runStart} {y}h{x - runStart}v1H{runStart}z");
            }
        }

        builder.Append("\"/></svg>");
        return builder.ToString();
    }

    private static bool IsDark(byte[] pixels, int width, int x, int y) =>
        pixels[(((y * width) + x) * 4)] < 128;
}
