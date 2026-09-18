using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.Helpers;

/// <summary>
/// 指令条码清单。桌面端与手机端共用同一批指令词（手机端另有 FLASH：切换手机手电筒），
/// 打印与另存图片都按这份清单生成，避免两处各写一套。
/// </summary>
public static class BarcodeCommandCatalog
{
    /// <summary>标签资源键与指令载荷；顺序按现场使用习惯排列</summary>
    public static readonly IReadOnlyList<(string LabelKey, string Payload)> Commands =
    [
        ("Main.BarcodeClear", "CLEAR"),
        ("Main.BarcodeReturn", "BACK"),
        ("Main.BarcodeShipping", "SHIP"),
        ("Main.BarcodeStart", "START"),
        ("Main.BarcodeStop", "STOP"),
        ("Main.BarcodeFlash", "FLASH")
    ];

    /// <summary>按当前界面语言取好名字的整套指令条码</summary>
    public static IReadOnlyList<BarcodePrintService.BarcodePrintItem> ResolveAll()
    {
        var items = new List<BarcodePrintService.BarcodePrintItem>(Commands.Count);
        foreach ((string labelKey, string payload) in Commands)
        {
            string label = AppLanguage.Get(labelKey).Replace("\\n", "\n");
            items.Add(new BarcodePrintService.BarcodePrintItem(label, payload));
        }
        return items;
    }
}
