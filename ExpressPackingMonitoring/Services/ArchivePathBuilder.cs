using ExpressPackingMonitoring.Data;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 负责把“归档目标根目录 + 记录信息”拼成最终 ArchivePath。
/// StorageLocationResolver 不拼接最终路径；本类集中维护归档目录结构，便于未来调整布局。
/// </summary>
internal static class ArchivePathBuilder
{
    /// <summary>本机录像布局：&lt;归档目标&gt;\yyyy-MM-dd\&lt;文件名&gt;。</summary>
    public static string BuildLocalRecordingArchivePath(
        string archiveTarget,
        DateTime startedAt,
        string fileName)
    {
        if (string.IsNullOrWhiteSpace(archiveTarget))
            return "";
        return Path.Combine(
            Path.GetFullPath(archiveTarget),
            startedAt.ToString("yyyy-MM-dd"),
            fileName);
    }

    /// <summary>外部上传布局：&lt;根&gt;\电脑上传|手机备份\&lt;设备&gt;-&lt;短ID&gt;\yyyy-MM-dd\&lt;面单&gt;_&lt;时间&gt;_&lt;模式&gt;.mp4。</summary>
    public static string BuildExternalUploadArchivePath(
        string root,
        string sourceDeviceKind,
        string sourceDeviceId,
        string sourceDeviceName,
        DateTime startedAt,
        string trackingNumber,
        string mode,
        string fileSha256)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "";

        string normalizedTracking = trackingNumber?.Trim().ToUpperInvariant() ?? "";
        string orderId = string.IsNullOrEmpty(normalizedTracking) ? "未识别面单" : normalizedTracking;
        string dateDirectory = Path.Combine(
            Path.GetFullPath(root),
            string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "电脑上传"
                : "手机备份",
            GetDeviceDirectoryName(sourceDeviceId, sourceDeviceName),
            startedAt.ToString("yyyy-MM-dd"));
        string normalizedMode = VideoDatabase.NormalizeRecordingMode(mode);
        string baseName = SanitizeFileName($"{orderId}_{startedAt:yyyyMMdd_HHmmss}_{normalizedMode}");
        return Path.Combine(dateDirectory, $"{baseName}.mp4");
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = value.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "未识别面单" : value;
    }

    private static string GetDeviceDirectoryName(string sourceDeviceId, string sourceDeviceName)
    {
        string readableName = SanitizeFileName(sourceDeviceName ?? "");
        if (string.Equals(readableName, "未识别面单", StringComparison.Ordinal))
            readableName = "手机";
        if (readableName.Length > 32)
            readableName = readableName[..32].TrimEnd('.', ' ');

        string normalizedId = new((sourceDeviceId ?? "")
            .Where(char.IsLetterOrDigit)
            .ToArray());
        string shortId = normalizedId.Length switch
        {
            0 => "未知设备",
            <= 6 => normalizedId.ToUpperInvariant(),
            _ => normalizedId[^6..].ToUpperInvariant()
        };
        return $"{readableName}-{shortId}";
    }
}
