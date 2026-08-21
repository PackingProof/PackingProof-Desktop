using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

internal static class BackupCompatibilityPolicy
{
    internal const string BackupProtocol = "mobile-backup-v2";
    internal const int EnrollmentVersion = 2;
    internal const int AuthenticationVersion = 3;
    internal const string MinimumMobileVersion = "0.5.23";
    internal const int MinimumMobileBuildNumber = 11036;
    internal const string MinimumDesktopVersion = "0.0.55";
    // Viewer enrollment 协议兼容下限，不代表任何客户端发布版本；
    // 取 viewer 类型首次随主机发布的版本号（当前主线下一版本）。
    internal const string MinimumViewerVersion = "0.0.49";
    internal const string MobileDownloadUrl =
        "https://gitee.com/PackingProof/PackingProof-Mobile/releases/latest";
    internal const string DesktopDownloadUrl =
        "https://github.com/PackingProof/PackingProof-Desktop/releases/latest";

    internal static BackupCompatibilityInfo CreateHostInfo() => new()
    {
        HostVersion = GetCurrentDesktopVersion(),
        Protocol = BackupProtocol,
        EnrollmentVersion = EnrollmentVersion,
        AuthVersion = AuthenticationVersion,
        MinimumMobileVersion = MinimumMobileVersion,
        MinimumMobileBuildNumber = MinimumMobileBuildNumber,
        MinimumWorkstationVersion = MinimumDesktopVersion
    };

    internal static bool IsCompatibleHost(BackupCompatibilityInfo? info) =>
        info != null
        && string.Equals(info.Protocol, BackupProtocol, StringComparison.Ordinal)
        && info.EnrollmentVersion == EnrollmentVersion
        && info.AuthVersion == AuthenticationVersion
        && CompareVersions(info.HostVersion, MinimumDesktopVersion) >= 0;

    internal static BackupCompatibilityFailure? ValidateClient(BackupDeviceEnrollmentRequest request)
    {
        bool workstation = string.Equals(request.DeviceKind, "pc", StringComparison.OrdinalIgnoreCase);
        bool viewer = string.Equals(request.DeviceKind, "viewer", StringComparison.OrdinalIgnoreCase);
        bool mobile = !workstation && !viewer;
        string minimumVersion = viewer
            ? MinimumViewerVersion
            : workstation
                ? MinimumDesktopVersion
                : MinimumMobileVersion;
        int minimumBuildNumber = mobile ? MinimumMobileBuildNumber : 0;
        string downloadUrl = mobile ? MobileDownloadUrl : DesktopDownloadUrl;
        string updateTarget = viewer ? "viewer" : mobile ? "mobile" : "recording-workstation";

        bool protocolCompatible = string.Equals(
                request.BackupProtocol,
                BackupProtocol,
                StringComparison.Ordinal)
            && request.EnrollmentVersion == EnrollmentVersion
            && request.AuthVersion == AuthenticationVersion;
        bool versionCompatible = CompareVersions(request.ClientVersion, minimumVersion) >= 0;
        bool buildCompatible = !mobile || request.ClientBuildNumber >= minimumBuildNumber;
        if (protocolCompatible && versionCompatible && buildCompatible)
            return null;

        string message = viewer
            ? "查看端版本过低，请更新后重新连接"
            : mobile
                ? "手机 App 版本过低，请更新后重新连接"
                : "录制工位版本过低，请更新电脑端后重新连接";
        return new BackupCompatibilityFailure(
            updateTarget,
            minimumVersion,
            minimumBuildNumber,
            downloadUrl,
            message);
    }

    internal static int CompareVersions(string? left, string? right)
    {
        if (!TryParseVersion(left, out Version leftVersion)) return -1;
        if (!TryParseVersion(right, out Version rightVersion)) return 1;
        return leftVersion.CompareTo(rightVersion);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim().TrimStart('v', 'V');
        int suffix = normalized.IndexOfAny(['+', '-']);
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out version!);
    }

    private static string GetCurrentDesktopVersion()
    {
        string current = AppVersion.Current.Trim().TrimStart('v', 'V');
        return CompareVersions(current, MinimumDesktopVersion) >= 0
            ? current
            : MinimumDesktopVersion;
    }
}

internal sealed record BackupCompatibilityFailure(
    string UpdateTarget,
    string MinimumVersion,
    int MinimumBuildNumber,
    string DownloadUrl,
    string Message);
