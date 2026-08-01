using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System.IO;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.Services;

internal readonly record struct ImportedVideoMetadata(double DurationSeconds);

internal readonly record struct VideoImportProgress(
    int Processed,
    int Total,
    int Imported,
    int Skipped,
    int Failed,
    string CurrentFile);

internal readonly record struct VideoImportResult(
    int Imported,
    int Skipped,
    int Failed,
    bool Cancelled);

internal sealed class VideoFolderImportService
{
    private readonly VideoDatabase _database;
    private readonly IReadOnlyList<string> _managedRoots;
    private readonly string _sourceDeviceId;
    private readonly string _sourceDeviceName;
    private readonly Func<string, ImportedVideoMetadata?> _metadataReader;

    internal VideoFolderImportService(
        VideoDatabase database,
        IEnumerable<string> managedRoots,
        string sourceDeviceId,
        string sourceDeviceName,
        Func<string, ImportedVideoMetadata?>? metadataReader = null)
    {
        _database = database;
        _managedRoots = managedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _sourceDeviceId = sourceDeviceId?.Trim() ?? "";
        _sourceDeviceName = sourceDeviceName?.Trim() ?? "";
        _metadataReader = metadataReader ?? ReadMetadata;
    }

    internal IReadOnlyList<string> ManagedRoots => _managedRoots;

    internal bool IsFolderManaged(string folderPath) =>
        !string.IsNullOrWhiteSpace(folderPath) && TryGetManagedRoot(folderPath, out _);

    internal async Task<VideoImportResult> ImportAsync(
        string folderPath,
        string mode,
        IProgress<VideoImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        string normalizedFolder = NormalizeDirectory(folderPath);
        if (!TryGetManagedRoot(normalizedFolder, out string managedRoot))
            throw new InvalidOperationException("所选文件夹不在程序管理的录像目录内");
        if (ContainsReparsePoint(managedRoot, normalizedFolder))
            throw new InvalidOperationException("所选文件夹包含不安全的链接目录");

        List<string> files = await Task.Run(
                () => EnumerateMp4Files(normalizedFolder, cancellationToken).ToList(),
                cancellationToken)
            .ConfigureAwait(false);
        int imported = 0;
        int skipped = 0;
        int failed = 0;
        int processed = 0;
        string normalizedMode = string.Equals(mode, "退货", StringComparison.Ordinal) ? "退货" : "发货";

        foreach (string filePath in files)
        {
            if (cancellationToken.IsCancellationRequested)
                return new VideoImportResult(imported, skipped, failed, true);

            try
            {
                if (ContainsReparsePoint(managedRoot, filePath)
                    || _database.HasActiveVideoAtPath(filePath))
                {
                    skipped++;
                    continue;
                }

                var before = new FileInfo(filePath);
                long originalLength = before.Length;
                DateTime originalWriteTimeUtc = before.LastWriteTimeUtc;
                string sha256 = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
                if (_database.GetVideoByContentSha256(sha256) != null)
                {
                    skipped++;
                    continue;
                }

                ImportedVideoMetadata? metadata = _metadataReader(filePath);
                var after = new FileInfo(filePath);
                if (metadata is not ImportedVideoMetadata video
                    || video.DurationSeconds <= 0
                    || after.Length <= 0
                    || after.Length != originalLength
                    || after.LastWriteTimeUtc != originalWriteTimeUtc)
                {
                    failed++;
                    continue;
                }

                string orderId = Path.GetFileNameWithoutExtension(filePath).Trim();
                if (string.IsNullOrWhiteSpace(orderId))
                    orderId = "未识别面单";
                bool inserted = _database.TryInsertImportedVideoRecord(
                    orderId,
                    normalizedMode,
                    filePath,
                    after.Length,
                    after.LastWriteTime,
                    video.DurationSeconds,
                    sha256,
                    _sourceDeviceId,
                    _sourceDeviceName);
                if (inserted) imported++; else skipped++;
            }
            catch (OperationCanceledException)
            {
                return new VideoImportResult(imported, skipped, failed, true);
            }
            catch (Exception ex)
            {
                failed++;
                Logging.RuntimeLog.Warn("VideoImport", $"Skipped {Path.GetFileName(filePath)}: {ex.Message}");
            }
            finally
            {
                processed++;
                progress?.Report(new VideoImportProgress(
                    processed,
                    files.Count,
                    imported,
                    skipped,
                    failed,
                    Path.GetFileName(filePath)));
            }
        }

        return new VideoImportResult(imported, skipped, failed, false);
    }

    internal static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        string root = NormalizeDirectory(rootPath);
        string candidate = Path.GetFullPath(candidatePath);
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private bool TryGetManagedRoot(string path, out string managedRoot)
    {
        managedRoot = _managedRoots.FirstOrDefault(root => IsPathWithinRoot(root, path)) ?? "";
        return managedRoot.Length > 0;
    }

    private static IEnumerable<string> EnumerateMp4Files(string folderPath, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(folderPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetExtension(file), ".mp4", StringComparison.OrdinalIgnoreCase))
                    yield return Path.GetFullPath(file);
            }

            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(directory);
            }
        }
    }

    private static bool ContainsReparsePoint(string managedRoot, string targetPath)
    {
        string root = NormalizeDirectory(managedRoot);
        string target = Path.GetFullPath(targetPath);
        if (!IsPathWithinRoot(root, target)) return true;
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ImportedVideoMetadata? ReadMetadata(string filePath)
    {
        string ffmpegPath = AppPaths.FindFFmpeg();
        return CompletedVideoSpecificationProbe.TryRead(ffmpegPath, filePath, out CompletedVideoMetadata metadata)
            ? new ImportedVideoMetadata(metadata.DurationSeconds)
            : null;
    }
}
