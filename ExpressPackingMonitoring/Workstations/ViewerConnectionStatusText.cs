namespace ExpressPackingMonitoring;

/// <summary>
/// 查看端连接状态的文案唯一来源。
///
/// Windows 查看窗口与 macOS 菜单栏显示同一套口径：状态词只在核心维护一份，
/// 两端都不许自己编词（Mac 端曾经自造过"等待主机确认接入"这种 PC 上看不到的说法）。
/// </summary>
internal static class ViewerConnectionStatusText
{
    /// <summary>查看端：正在搜索同一网络中的主机。</summary>
    public const string SearchingViewer = "正在搜索同一网络中的主机";

    /// <summary>保存主机：正在查找同一局域网中可用的保存主机（用于绑定主机）。</summary>
    public const string SearchingBindingHost = "正在查找同一局域网中可用的保存主机";

    public const string NotFound = "没有找到主机，请检查两台电脑是否连接同一网络";
    public const string FoundNoRecordingReceiver = "找到了主机，但没有可接收录像的保存主机";
    public const string FoundOutdatedRecordingReceiver = "找到了保存主机，但版本过旧，请更新保存主机电脑";
    public const string FoundSingle = "找到 1 台主机，确认后即可连接";
    public const string SearchCanceled = "搜索已取消";
    public const string SearchingOtherHosts = "正在查找其他可用主机";
    public const string RequestingHostApproval = "正在请求保存主机允许连接";
    public const string AccessGranted = "已允许访问";
    public const string AccessNotGranted = "未取得网页访问权限";
    public const string Online = "在线";
    public const string HostOfflineOrChanged = "主机离线或身份已变化";
    public const string NotBound = "尚未绑定主机";
    public const string TemporaryOffline = "暂时离线，稍后会自动重试";

    public static string FoundMany(int count) => $"找到 {count} 台主机，请选择要连接的主机";

    public static string SearchFailed(string reason) => $"搜索主机失败：{reason}";

    public static string WaitingForHostApproval(string nodeName) =>
        $"已找到“{nodeName}”，等待保存主机允许连接";

    public static string Connecting(string nodeName) => $"正在连接“{nodeName}”";

    /// <summary>已连接的显示：菜单一行里需要"已连接 主机名"，没有名字时退回"在线"。</summary>
    public static string Connected(string? nodeName) =>
        string.IsNullOrWhiteSpace(nodeName) ? Online : $"已连接 {nodeName}";

    /// <summary>
    /// 状态词表：菜单这类外部界面按 key 取词，避免"同一句话在壳里再写一遍"，
    /// 词改了以后两端一起变。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> AllWords { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["searching"] = SearchingViewer,
            ["searchingBindingHost"] = SearchingBindingHost,
            ["notFound"] = NotFound,
            ["noRecordingReceiver"] = FoundNoRecordingReceiver,
            ["outdatedReceiver"] = FoundOutdatedRecordingReceiver,
            ["foundSingle"] = FoundSingle,
            ["searchCanceled"] = SearchCanceled,
            ["searchingOtherHosts"] = SearchingOtherHosts,
            ["requestingHostApproval"] = RequestingHostApproval,
            ["accessGranted"] = AccessGranted,
            ["accessNotGranted"] = AccessNotGranted,
            ["online"] = Online,
            ["hostOfflineOrChanged"] = HostOfflineOrChanged,
            ["notBound"] = NotBound,
            ["temporaryOffline"] = TemporaryOffline
        };
}
