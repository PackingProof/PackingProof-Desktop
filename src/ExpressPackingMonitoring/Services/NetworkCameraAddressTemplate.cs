using System.Globalization;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

public enum NetworkCameraBrand
{
    Dahua,
    Hikvision,
    Uniview,
    Other,
}

public enum NetworkCameraDeviceKind
{
    Camera,
    Recorder,
}

public enum NetworkCameraStreamKind
{
    Main,
    Sub,
}

/// <summary>一台网络摄像头的地址组成；只有三个预置品牌能拼出地址，"其他"仍走完整 URL。</summary>
internal sealed record NetworkCameraAddress(
    NetworkCameraBrand Brand,
    NetworkCameraDeviceKind Device,
    string Host,
    int Port,
    string UserName,
    string Password,
    NetworkCameraStreamKind Stream,
    int Channel)
{
    internal string ToUrl()
    {
        string userInfo = "";
        if (UserName.Length > 0)
            userInfo = Password.Length > 0 ? $"{UserName}:{Password}@" : $"{UserName}@";

        string path = Brand switch
        {
            NetworkCameraBrand.Dahua =>
                $"/cam/realmonitor?channel={Format(Channel)}&subtype={Format(StreamIndex)}",
            NetworkCameraBrand.Hikvision =>
                $"/Streaming/Channels/{Format(Channel)}{(Stream == NetworkCameraStreamKind.Main ? "01" : "02")}",
            NetworkCameraBrand.Uniview when Device == NetworkCameraDeviceKind.Recorder =>
                $"/unicast/c{Format(Channel)}/s{Format(StreamIndex)}/live",
            NetworkCameraBrand.Uniview =>
                $"/media/video{(Stream == NetworkCameraStreamKind.Main ? "1" : "2")}",
            _ => "",
        };

        return $"rtsp://{userInfo}{Host}:{Format(Port)}{path}";
    }

    private int StreamIndex => Stream == NetworkCameraStreamKind.Main ? 0 : 1;

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 品牌预置的 RTSP 地址模板。只负责"字段 → 完整地址"和"完整地址 → 字段"，
/// 不碰配置、界面和拉流；设置页与首用向导共用，避免两边各拼一套。
///
/// 预置只是常见型号的捷径：兼容设备、老固件、非标准端口都可能不匹配，
/// 所以"其他"永远保留完整地址手动填写。
/// </summary>
internal static class NetworkCameraAddressTemplate
{
    internal const int DefaultPort = 554;
    internal const int DefaultChannel = 1;
    internal const int MaxChannel = 64;

    private const string UnsafeCredentialChars = "@:/?#%";

    private static readonly Regex HikvisionPath = new(
        @"^/Streaming/Channels/(?<channel>\d{1,3})(?<stream>0[12])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UniviewRecorderPath = new(
        @"^/unicast/c(?<channel>\d{1,3})/s(?<stream>[01])/live$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UniviewCameraPath = new(
        @"^/media/video(?<stream>[0-3])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool TryCompose(
        NetworkCameraBrand brand,
        NetworkCameraDeviceKind device,
        string? host,
        string? portText,
        string? userName,
        string? password,
        NetworkCameraStreamKind stream,
        string? channelText,
        out string url,
        out string error)
    {
        url = "";
        error = "";

        if (brand == NetworkCameraBrand.Other)
        {
            error = "请选择品牌";
            return false;
        }

        if (!TryNormalizeHost(host, out string normalizedHost, out error))
            return false;
        if (!TryParsePort(portText, out int port, out error))
            return false;
        if (!TryNormalizeCredentials(userName, password, out string normalizedUser, out string normalizedPassword, out error))
            return false;

        int channel = DefaultChannel;
        if (UsesChannel(brand, device) && !TryParseChannel(channelText, out channel, out error))
            return false;

        url = new NetworkCameraAddress(
            brand,
            device,
            normalizedHost,
            port,
            normalizedUser,
            normalizedPassword,
            stream,
            channel).ToUrl();
        return true;
    }

    /// <summary>哪些预置模板需要通道号：大华/海康的路径带通道，宇视只有录像机路径带通道。</summary>
    internal static bool UsesChannel(NetworkCameraBrand brand, NetworkCameraDeviceKind device) =>
        brand is NetworkCameraBrand.Dahua or NetworkCameraBrand.Hikvision
        || (brand == NetworkCameraBrand.Uniview && device == NetworkCameraDeviceKind.Recorder);

    /// <summary>把已有地址还原成字段；不是三个预置品牌的标准路径时返回 false，界面落到"其他"。</summary>
    internal static bool TryParse(string? value, out NetworkCameraAddress? address)
    {
        address = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        int port = uri.Port > 0 ? uri.Port : DefaultPort;
        (string userName, string password) = SplitUserInfo(uri.UserInfo);
        if (!IsSafeCredential(userName) || !IsSafeCredential(password))
            return false;

        string path = uri.AbsolutePath;
        NetworkCameraAddress? parsed = null;

        if (string.Equals(path, "/cam/realmonitor", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, string> query = ParseQuery(uri.Query);
            if (!TryReadQueryInt(query, "channel", DefaultChannel, 1, MaxChannel, out int channel)
                || !TryReadQueryInt(query, "subtype", 0, 0, 1, out int subtype))
            {
                return false;
            }

            parsed = new NetworkCameraAddress(
                NetworkCameraBrand.Dahua,
                channel > 1 ? NetworkCameraDeviceKind.Recorder : NetworkCameraDeviceKind.Camera,
                uri.Host,
                port,
                userName,
                password,
                subtype == 0 ? NetworkCameraStreamKind.Main : NetworkCameraStreamKind.Sub,
                channel);
        }
        else if (HikvisionPath.Match(path) is { Success: true } hikvision)
        {
            int channel = int.Parse(hikvision.Groups["channel"].Value, CultureInfo.InvariantCulture);
            if (channel is < 1 or > MaxChannel)
                return false;

            parsed = new NetworkCameraAddress(
                NetworkCameraBrand.Hikvision,
                NetworkCameraDeviceKind.Camera,
                uri.Host,
                port,
                userName,
                password,
                hikvision.Groups["stream"].Value == "01"
                    ? NetworkCameraStreamKind.Main
                    : NetworkCameraStreamKind.Sub,
                channel);
        }
        else if (UniviewRecorderPath.Match(path) is { Success: true } recorder)
        {
            int channel = int.Parse(recorder.Groups["channel"].Value, CultureInfo.InvariantCulture);
            if (channel is < 1 or > MaxChannel)
                return false;

            parsed = new NetworkCameraAddress(
                NetworkCameraBrand.Uniview,
                NetworkCameraDeviceKind.Recorder,
                uri.Host,
                port,
                userName,
                password,
                recorder.Groups["stream"].Value == "0"
                    ? NetworkCameraStreamKind.Main
                    : NetworkCameraStreamKind.Sub,
                channel);
        }
        else if (UniviewCameraPath.Match(path) is { Success: true } camera)
        {
            int stream = int.Parse(camera.Groups["stream"].Value, CultureInfo.InvariantCulture);
            parsed = new NetworkCameraAddress(
                NetworkCameraBrand.Uniview,
                NetworkCameraDeviceKind.Camera,
                uri.Host,
                port,
                userName,
                password,
                stream <= 1 ? NetworkCameraStreamKind.Main : NetworkCameraStreamKind.Sub,
                DefaultChannel);
        }

        if (parsed == null)
            return false;

        address = parsed;
        return true;
    }

    internal static bool TryNormalizeHost(
        string? value,
        out string host,
        out string error)
    {
        host = (value ?? "").Trim();
        error = "";
        if (host.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            host = host["rtsp://".Length..].Trim();
        host = host.TrimEnd('/');

        if (host.Length == 0)
        {
            error = "请填写主机/IP 地址";
            return false;
        }

        foreach (char ch in host)
        {
            if (char.IsWhiteSpace(ch) || ch is '/' or '?' or '#' or '@' or ':' or '\\')
            {
                error = "主机/IP 只填地址本身，不要带协议、路径或端口";
                return false;
            }
        }

        return true;
    }

    internal static bool TryParsePort(string? value, out int port, out string error)
    {
        error = "";
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            port = DefaultPort;
            return true;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port < 1
            || port > 65535)
        {
            error = "端口必须是 1-65535 之间的数字";
            return false;
        }

        return true;
    }

    internal static bool TryParseChannel(string? value, out int channel, out string error)
    {
        error = "";
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            channel = DefaultChannel;
            return true;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out channel)
            || channel < 1
            || channel > MaxChannel)
        {
            error = "通道必须是 1-64 之间的数字";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeCredentials(
        string? userName,
        string? password,
        out string normalizedUser,
        out string normalizedPassword,
        out string error)
    {
        normalizedUser = (userName ?? "").Trim();
        normalizedPassword = password ?? "";
        error = "";

        if (normalizedUser.Length == 0)
        {
            if (normalizedPassword.Length > 0)
            {
                error = "请填写账号";
                return false;
            }

            return true;
        }

        if (normalizedUser.Any(IsUnsafeCredentialChar))
        {
            error = "账号不能包含 @ : / ? # % 或空格";
            return false;
        }

        if (normalizedPassword.Any(IsUnsafeCredentialChar))
        {
            error = "密码不能包含 @ : / ? # % 或空格，请先在摄像头里改成简单密码";
            return false;
        }

        return true;
    }

    private static bool IsSafeCredential(string value) =>
        value.All(ch => !IsUnsafeCredentialChar(ch));

    private static bool IsUnsafeCredentialChar(char ch) =>
        char.IsWhiteSpace(ch) || UnsafeCredentialChars.Contains(ch, StringComparison.Ordinal);

    private static (string UserName, string Password) SplitUserInfo(string userInfo)
    {
        if (string.IsNullOrEmpty(userInfo))
            return ("", "");

        int separator = userInfo.IndexOf(':');
        return separator < 0
            ? (userInfo, "")
            : (userInfo[..separator], userInfo[(separator + 1)..]);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;
            values[pair[..separator]] = pair[(separator + 1)..];
        }

        return values;
    }

    /// <summary>读查询参数：缺省给默认值，出现了但解析不了或越界则判为不匹配（宁可落到"其他"也不猜）。</summary>
    private static bool TryReadQueryInt(
        IReadOnlyDictionary<string, string> query,
        string key,
        int fallback,
        int minimum,
        int maximum,
        out int value)
    {
        if (!query.TryGetValue(key, out string? text))
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= minimum
            && value <= maximum;
    }
}
