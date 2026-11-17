using System.Globalization;

namespace ExpressPackingMonitoring.UI;

internal static class WebClipLaunchUrlBuilder
{
    internal static string Build(
        string accessUrl,
        long videoId,
        string? orderId,
        string? fileName,
        double durationSeconds,
        string? videoCodec)
    {
        if (string.IsNullOrWhiteSpace(accessUrl) || videoId <= 0)
            throw new ArgumentException("网页剪辑地址参数无效");

        string separator = accessUrl.Contains('?') ? "&" : "?";
        string duration = Math.Max(0, durationSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);
        return accessUrl
            + separator
            + "action=clip"
            + "&videoId=" + videoId.ToString(CultureInfo.InvariantCulture)
            + "&orderId=" + Uri.EscapeDataString(orderId ?? "")
            + "&fileName=" + Uri.EscapeDataString(fileName ?? "")
            + "&durationSec=" + Uri.EscapeDataString(duration)
            + "&videoCodec=" + Uri.EscapeDataString(videoCodec ?? "");
    }
}
