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

    /// <summary>外部上传布局：&lt;根&gt;\电脑上传|手机备份\设备-&lt;短ID&gt;\yyyy-MM-dd\&lt;面单&gt;_&lt;时间&gt;_&lt;模式&gt;.mp4。</summary>
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
            GetDeviceDirectoryName(sourceDeviceId),
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

    /// <summary>
    /// 设备目录只用稳定标识，不带昵称：昵称随时可以改，写进目录名会让同一台设备的录像
    /// 散落到多个目录里。设备号与昵称的对照关系由录像根目录下的"设备对照表.txt"给出
    /// （见 <see cref="RecordingDeviceFolderIndex"/>）。
    /// </summary>
    internal static string GetDeviceDirectoryName(string sourceDeviceId)
    {
        string normalizedId = new((sourceDeviceId ?? "")
            .Where(char.IsLetterOrDigit)
            .ToArray());
        string shortId = normalizedId.Length switch
        {
            0 => "未识别",
            <= 6 => normalizedId.ToUpperInvariant(),
            _ => normalizedId[^6..].ToUpperInvariant()
        };
        return $"设备-{shortId}";
    }
}
