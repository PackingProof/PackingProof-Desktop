namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// 开始录像后要不要按单号做重复单号提醒、退款核验和订单信息提示。
/// 扫码识别和手动“开始录制”都会走到同一处处理；手动开始时空扫码框会回退成
/// MAN_ 占位号，那不是真实单号，只适合当录像名，不能拿去查订单。
/// </summary>
internal static class RecordingStartOrderPolicy
{
    internal const string ManualOrderPrefix = "MAN_";

    internal static bool ShouldHandle(string? orderNumber) =>
        !string.IsNullOrWhiteSpace(orderNumber)
        && !orderNumber.Trim().StartsWith(ManualOrderPrefix, StringComparison.OrdinalIgnoreCase);
}
