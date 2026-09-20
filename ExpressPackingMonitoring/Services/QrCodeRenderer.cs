using System.Text;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 二维码渲染入口。默认输出 SVG data URI：不依赖任何图像库，非 Windows 宿主（例如
/// macOS 保存主机）可以直接用；Windows 端在启动时注册 WPF 实现，保持原有 PNG 输出。
/// </summary>
internal static class QrCodeRenderer
{
    private static Func<string, int, string>? _renderer;

    /// <summary>注册平台实现；不注册时使用内置的 SVG 输出。</summary>
    internal static void UseRenderer(Func<string, int, string> renderer) => _renderer = renderer;

    public static string CreateDataUri(string url, int size = 260)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("二维码网址不能为空", nameof(url));

        int normalizedSize = Math.Clamp(size, 160, 1024);
        return _renderer?.Invoke(url.Trim(), normalizedSize)
            ?? CreateSvgDataUri(url.Trim(), normalizedSize);
    }

    /// <summary>内置的 SVG 输出；单独开放给测试直接验证，不受注册的平台实现影响。</summary>
    internal static string CreateSvgDataUri(string url, int size)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 2,
                PureBarcode = true
            }
        };

        var pixelData = writer.Write(url);
        string svg = BuildSvg(pixelData.Width, pixelData.Height, pixelData.Pixels);
        return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
    }

    /// <summary>每个模块输出一个连通的方块，矢量结果放大也不会糊。</summary>
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
