namespace ExpressPackingMonitoring.Config;

public partial class AppConfig
{
    // 接收预览版更新：测试机用，打开后连 prerelease 版本一起收；正式机器保持关闭
    public bool AllowPrereleaseUpdates { get; set; } = false;
    // 上一次成功启动的版本，用于判断预览通道用户是否刚完成一次升级
    public string LastRunAppVersion { get; set; } = "";
    // 已展示过样本贡献提示的版本：预览升级或关闭倾斜面单识别共用，每版本最多提示一次
    public string PreviewSamplePromptVersion { get; set; } = "";
}
