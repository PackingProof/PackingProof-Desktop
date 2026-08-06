namespace ExpressPackingMonitoring.Services;

internal static class NetworkCameraUrlPolicy
{
    private static readonly string[] AllowedSchemes = ["rtsp", "rtmp", "rtmps", "http", "https"];

    internal static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        string value = input?.Trim() ?? "";
        if (value.Length == 0)
        {
            error = "请输入网络摄像头地址";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            error = "地址格式不正确，请输入以 rtsp://、rtmp:// 或 http(s):// 开头的完整地址";
            return false;
        }

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = "暂不支持该协议，支持 rtsp://、rtmp://、http:// 和 https://";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "地址缺少主机名或 IP";
            return false;
        }

        normalized = value;
        return true;
    }

    internal static string SanitizeForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return "invalid-url";

            var builder = new UriBuilder(uri);
            if (!string.IsNullOrEmpty(builder.UserName) && !string.IsNullOrEmpty(builder.Password))
                builder.Password = "***";
            return builder.Uri.ToString();
        }
        catch
        {
            return "invalid-url";
        }
    }
}
