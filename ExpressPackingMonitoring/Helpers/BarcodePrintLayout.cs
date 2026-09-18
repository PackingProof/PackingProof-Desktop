namespace ExpressPackingMonitoring.Helpers;

/// <summary>
/// 指令条码打印页的排版参数。打印件要贴到墙上给摄像头扫，所以模块宽度按固定物理尺寸给，
/// 不受屏幕缩放影响：4 DIP ≈ 1.06 毫米，比界面上的显示尺寸更耐脏、更耐反光。
/// </summary>
public static class BarcodePrintLayout
{
    /// <summary>模块宽度（DIP）。96 DIP = 1 英寸</summary>
    public const double ModuleWidth = 4;

    /// <summary>条码高度（DIP），约 17 毫米</summary>
    public const double BarcodeHeight = 64;

    /// <summary>每行标题占用的高度</summary>
    public const double LabelHeight = 26;

    /// <summary>标题与说明块的高度</summary>
    public const double HeaderHeight = 74;

    /// <summary>行间距</summary>
    public const double RowGap = 26;

    /// <summary>页边距</summary>
    public const double Margin = 48;

    /// <summary>A4 竖版宽度（DIP）</summary>
    public const double A4Width = 793.7;

    /// <summary>A4 竖版高度（DIP）</summary>
    public const double A4Height = 1122.5;

    /// <summary>单个条码的绘制宽度（DIP），含两侧静区</summary>
    public static double GetBarcodeWidth(string payload)
    {
        int modules = BarcodeHelper.QuietZoneModules * 2;
        foreach ((bool _, int width) in BarcodeHelper.BuildModules(payload))
            modules += width;
        return modules * ModuleWidth;
    }

    /// <summary>一整行（标题 + 条码 + 间距）占用的高度</summary>
    public static double GetRowHeight() => LabelHeight + BarcodeHeight + RowGap;

    /// <summary>排完这些条码需要的总高度</summary>
    public static double GetRequiredHeight(int itemCount) =>
        Margin * 2 + HeaderHeight + Math.Max(0, itemCount) * GetRowHeight();

    /// <summary>排完这些条码需要的总宽度（右侧留出指令文字的余地）</summary>
    public static double GetRequiredWidth(IEnumerable<string> payloads)
    {
        double widest = 0;
        foreach (string payload in payloads)
            widest = Math.Max(widest, GetBarcodeWidth(payload));
        return Margin * 2 + widest + 120;
    }

    /// <summary>这些条码能否排进一页</summary>
    public static bool FitsOnOnePage(int itemCount, double pageHeight = A4Height) =>
        GetRequiredHeight(itemCount) <= pageHeight;

    /// <summary>每一行相对页顶的 Y 坐标</summary>
    public static IReadOnlyList<double> GetRowTops(int itemCount)
    {
        var tops = new List<double>(Math.Max(0, itemCount));
        double y = Margin + HeaderHeight;
        for (int index = 0; index < itemCount; index++)
        {
            tops.Add(y);
            y += GetRowHeight();
        }
        return tops;
    }
}
