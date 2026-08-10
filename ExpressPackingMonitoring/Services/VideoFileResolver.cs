using ExpressPackingMonitoring.Data;
using System.Collections.Concurrent;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 回放/下载/剪辑统一路径解析：本地完整文件优先，否则已归档且可达的网络副本兜底。
/// 可达性带短 TTL 缓存，NAS 离线时历史界面保持响应。
/// </summary>
internal static class VideoFileResolver
{
    private static readonly ConcurrentDictionary<string, AvailabilityEntry> AvailabilityCache = new();
    private static readonly TimeSpan AvailableTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnavailableTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static string ResolvePlaybackPath(VideoRecord record)
    {
        if (record == null)
            return "";

        string localPath = record.FilePath ?? "";
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            return localPath;

        string archivePath = record.ArchivePath ?? "";
        if (string.IsNullOrWhiteSpace(archivePath))
            return "";
        if (record.ArchiveStatus is not (VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted))
            return "";

        return IsArchiveAvailable(archivePath) ? archivePath : "";
    }

    public static bool IsArchiveAvailable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (AvailabilityCache.TryGetValue(path, out AvailabilityEntry entry))
        {
            TimeSpan ttl = entry.Available ? AvailableTtl : UnavailableTtl;
            if (DateTime.UtcNow - entry.CheckedAtUtc < ttl)
                return entry.Available;
        }

        bool available = RemoteFileProbe.TryProbeFile(path, ProbeTimeout);
        AvailabilityCache[path] = new AvailabilityEntry(available, DateTime.UtcNow);
        return available;
    }

    /// <summary>播放遇到远端 IO 异常时使缓存立即失效，避免长时间误判可用。</summary>
    public static void MarkUnavailable(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            AvailabilityCache[path] = new AvailabilityEntry(false, DateTime.UtcNow);
    }

    private readonly record struct AvailabilityEntry(bool Available, DateTime CheckedAtUtc);
}
