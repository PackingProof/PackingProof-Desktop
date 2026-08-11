namespace ExpressPackingMonitoring.Services;

/// <summary>
/// “订单联动”状态卡片的共享文案模型：主界面与录像文件备份主机窗口共用，
/// 短状态（已就绪/需更新/暂无设备/未配置）与详情文案的映射只维护一份。
/// </summary>
internal static class UserscriptStatusCardModel
{
    internal static (string ShortStatus, string DetailText) GetCardTexts(
        UserscriptTargetStatus status)
    {
        string shortStatus = status.StatusText switch
        {
            "订单联动已就绪" => "已就绪",
            "需要更新订单联动" => "需更新",
            "暂无订单接收设备" => "暂无设备",
            _ => "未配置"
        };
        return (shortStatus, status.StatusText);
    }
}
