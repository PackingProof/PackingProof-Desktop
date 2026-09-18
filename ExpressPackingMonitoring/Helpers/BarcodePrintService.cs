using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExpressPackingMonitoring.Helpers;

/// <summary>
/// 指令条码的打印页。条码直接按模块宽度画，不经过界面上的位图，保证打印出来的
/// 物理尺寸稳定、边缘落在整点上，贴到墙上给摄像头或扫描枪读。
/// </summary>
public static class BarcodePrintService
{
    public sealed record BarcodePrintItem(string Label, string Payload);

    private static readonly Typeface TitleTypeface = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Typeface PayloadTypeface = new(
        new FontFamily("Consolas, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>
    /// 生成一页打印内容：标题 + 使用说明 + 若干条码。
    /// 页面尺寸用打印机的可打印区域，避免内容被纸张边距裁掉。
    /// </summary>
    public static DrawingVisual BuildPage(
        IReadOnlyList<BarcodePrintItem> items,
        double pageWidth,
        double pageHeight,
        string title,
        string hint)
    {
        double width = Math.Max(
            BarcodePrintLayout.GetRequiredWidth(items.Select(item => item.Payload)),
            pageWidth);
        double height = Math.Max(BarcodePrintLayout.GetRequiredHeight(items.Count), pageHeight);

        var visual = new DrawingVisual();
        using DrawingContext dc = visual.RenderOpen();
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        double left = BarcodePrintLayout.Margin;
        double y = BarcodePrintLayout.Margin;

        dc.DrawText(
            CreateText(title, TitleTypeface, 26, Brushes.Black),
            new Point(left, y));
        y += 34;
        dc.DrawText(
            CreateText(hint, LabelTypeface, 14, Brushes.DimGray),
            new Point(left, y));

        IReadOnlyList<double> rowTops = BarcodePrintLayout.GetRowTops(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            BarcodePrintItem item = items[index];
            double rowTop = rowTops[index];

            dc.DrawText(
                CreateText(item.Label, LabelTypeface, 18, Brushes.Black),
                new Point(left, rowTop));

            double barcodeTop = rowTop + BarcodePrintLayout.LabelHeight;
            DrawBarcode(dc, item.Payload, left, barcodeTop);

            dc.DrawText(
                CreateText(item.Payload, PayloadTypeface, 13, Brushes.DimGray),
                new Point(
                    left + BarcodePrintLayout.GetBarcodeWidth(item.Payload) + 16,
                    barcodeTop + BarcodePrintLayout.BarcodeHeight / 2 - 10));

            double separatorY = rowTop + BarcodePrintLayout.LabelHeight
                + BarcodePrintLayout.BarcodeHeight
                + BarcodePrintLayout.RowGap / 2;
            dc.DrawLine(
                new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 1),
                new Point(left, separatorY),
                new Point(width - BarcodePrintLayout.Margin, separatorY));
        }

        return visual;
    }

    /// <summary>把单条条码渲染成图片，方便用户贴进自己的文档或自行打印</summary>
    public static BitmapSource RenderSingle(
        BarcodePrintItem item,
        string title,
        string hint,
        double dpi = 300)
    {
        var items = new[] { item };
        double width = BarcodePrintLayout.GetRequiredWidth(items.Select(entry => entry.Payload));
        double height = BarcodePrintLayout.GetRequiredHeight(items.Length);

        DrawingVisual page = BuildPage(items, width, height, title, hint);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi / 96.0),
            (int)Math.Ceiling(height * dpi / 96.0),
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(page);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>另存图片的文件名：指令载荷 + 中文标签，去掉文件名不允许的字符</summary>
    public static string BuildImageFileName(BarcodePrintItem item)
    {
        string label = (item.Label ?? "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Trim();
        var builder = new System.Text.StringBuilder();
        foreach (char value in label)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), value) >= 0 ? '-' : value);
        }

        string safeLabel = builder.ToString().Trim('-', ' ', '.');
        string prefix = string.IsNullOrWhiteSpace(item.Payload) ? "barcode" : item.Payload;
        return string.IsNullOrEmpty(safeLabel) ? prefix + ".png" : $"{prefix}-{safeLabel}.png";
    }

    /// <summary>
    /// 把整套指令条码写入指定文件夹：目录不存在就建，文件已存在一律覆盖，
    /// 保证用户打开目录时看到的永远是当前语言、当前清单的图片。
    /// </summary>
    public static int WriteAll(
        string folder,
        IReadOnlyList<BarcodePrintItem> items,
        string title,
        string hint)
    {
        if (string.IsNullOrWhiteSpace(folder) || items.Count == 0)
            return 0;

        Directory.CreateDirectory(folder);
        int written = 0;
        foreach (BarcodePrintItem item in items)
        {
            BitmapSource image = RenderSingle(item, title, hint);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using Stream stream = File.Create(Path.Combine(folder, BuildImageFileName(item)));
            encoder.Save(stream);
            written++;
        }

        return written;
    }

    /// <summary>按条码模块逐条画黑白条纹，宽度与静区都由排版参数决定</summary>
    private static void DrawBarcode(DrawingContext dc, string payload, double left, double top)
    {
        double x = left + BarcodeHelper.QuietZoneModules * BarcodePrintLayout.ModuleWidth;
        foreach ((bool isBar, int moduleWidth) in BarcodeHelper.BuildModules(payload))
        {
            double barWidth = moduleWidth * BarcodePrintLayout.ModuleWidth;
            if (isBar)
                dc.DrawRectangle(Brushes.Black, null, new Rect(x, top, barWidth, BarcodePrintLayout.BarcodeHeight));
            x += barWidth;
        }
    }

    private static FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(
            text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            96);
}
