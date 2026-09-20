using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileBackupService
{
    internal const string ProtocolVersion = "mobile-backup-v2";
    internal const int ChunkSizeBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan UploadRetention = TimeSpan.FromDays(3);

    /// <summary>
    /// 备份分片暂存目录名。暂存必须与录像落在同一存储卷：既不占用系统盘，
    /// 也让完成落盘成为同卷改名，避免跨卷复制中断留下不完整的目标文件。
    /// </summary>
    internal const string StagingDirectoryName = ".packingproof-staging";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly VideoDatabase _database;
    private readonly string _stateDirectory;
    private readonly Func<string> _recordingRootResolver;
    private readonly Func<string, OrderInfo?> _orderInfoResolver;
    private readonly Func<string?>? _archiveTargetResolver;
    private readonly Action? _archivePendingCallback;
    internal const int UploadLockStripeCount = 256;
    private readonly object[] _uploadLocks = Enumerable.Range(0, UploadLockStripeCount)
        .Select(_ => new object())
        .ToArray();
    private readonly object _activeUploadsLock = new();
    private readonly HashSet<string> _activeUploads = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool> _uploadsIdle =
        CreateCompletedIdleSource();

    internal event Action<bool>? ActiveUploadsChanged;
    internal bool HasActiveUploads
    {
        get
        {
            lock (_activeUploadsLock)
                return _activeUploads.Count > 0;
        }
    }

    public MobileBackupService(
        VideoDatabase database,
        string stateDirectory,
        Func<string> recordingRootResolver,
        Func<string, OrderInfo?>? orderInfoResolver = null,
        Func<string?>? archiveTargetResolver = null,
        Action? archivePendingCallback = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _stateDirectory = string.IsNullOrWhiteSpace(stateDirectory)
            ? throw new ArgumentException("上传状态目录不能为空", nameof(stateDirectory))
            : Path.GetFullPath(stateDirectory);
        _recordingRootResolver = recordingRootResolver
            ?? throw new ArgumentNullException(nameof(recordingRootResolver));
        _orderInfoResolver = orderInfoResolver ?? (_ => null);
        _archiveTargetResolver = archiveTargetResolver;
        _archivePendingCallback = archivePendingCallback;
        Directory.CreateDirectory(_stateDirectory);
        CleanupExpiredUploads();
        new MobileBackupSharedFileMigrator(_database).Run();
    }

    public MobileBackupCreateResult CreateOrResume(MobileBackupCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sha256 = NormalizeSha256(request.FileSha256);
        ValidateTotalBytes(request.TotalBytes);
        ValidateMimeType(request.MimeType);
        string uploadId = sha256;

        lock (GetUploadLock(uploadId))
        {
            VideoRecord? existing = _database.GetVideoByContentSha256(sha256);
            if (existing != null && File.Exists(existing.FilePath))
            {
                long existingLength = new FileInfo(existing.FilePath).Length;
                if (existingLength == request.TotalBytes
                    && FileMatchesSha256(existing.FilePath, sha256))
                {
                    MarkUploadCompleted(uploadId);
                    return new MobileBackupCreateResult(uploadId, existingLength, ChunkSizeBytes, true);
                }
            }

            // 备份分片必须落在目标存储卷：磁盘未接入、未就绪或空间不足时立即拒收，
            // 客户端保留本地录像并按 storage_unavailable 自动重试。
            string stagingDirectory = EnsureStagingDirectory();

            MobileBackupUploadState? state = LoadState(uploadId);
            if (state != null)
            {
                if (!string.Equals(state.FileSha256, sha256, StringComparison.OrdinalIgnoreCase)
                    || state.TotalBytes != request.TotalBytes
                    || !string.Equals(state.MimeType, request.MimeType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MobileBackupValidationException("upload_conflict", "同一上传任务的文件信息不一致");
                }

                if (TryUseStateFinalFile(state, out _, out long completedSize))
                {
                    MarkUploadCompleted(uploadId);
                    return new MobileBackupCreateResult(uploadId, completedSize, ChunkSizeBytes, true);
                }

                long offset = File.Exists(PartPath(uploadId)) ? new FileInfo(PartPath(uploadId)).Length : 0;
                EnsureStagingFreeSpace(stagingDirectory, request.TotalBytes - offset);
                state.ReceivedBytes = offset;
                state.UpdatedAtUtc = DateTime.UtcNow;
                SaveState(state);
                MarkUploadActive(uploadId);
                return new MobileBackupCreateResult(uploadId, offset, ChunkSizeBytes, false);
            }

            EnsureStagingFreeSpace(stagingDirectory, request.TotalBytes);
            state = new MobileBackupUploadState
            {
                UploadId = uploadId,
                FileSha256 = sha256,
                TotalBytes = request.TotalBytes,
                ReceivedBytes = 0,
                MimeType = request.MimeType.Trim().ToLowerInvariant(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            SaveState(state);
            MarkUploadActive(uploadId);
            return new MobileBackupCreateResult(uploadId, 0, ChunkSizeBytes, false);
        }
    }

    internal Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idleTask;
        lock (_activeUploadsLock)
            idleTask = _uploadsIdle.Task;
        return cancellationToken.CanBeCanceled
            ? idleTask.WaitAsync(cancellationToken)
            : idleTask;
    }

    public long AppendChunk(
        string uploadId,
        long start,
        long end,
        long total,
        byte[] content,
        string chunkSha256)
    {
        uploadId = NormalizeSha256(uploadId);
        ArgumentNullException.ThrowIfNull(content);
        string normalizedChunkSha = NormalizeSha256(chunkSha256);

        lock (GetUploadLock(uploadId))
        {
            MobileBackupUploadState state = LoadState(uploadId)
                ?? throw new MobileBackupValidationException("upload_not_found", "上传任务不存在或已过期");
            if (total != state.TotalBytes || start < 0 || end < start || end >= total)
                throw new MobileBackupValidationException("invalid_content_range", "Content-Range 与上传任务不一致");
            long expectedLength = end - start + 1;
            if (expectedLength != content.LongLength || content.Length > ChunkSizeBytes)
                throw new MobileBackupValidationException("invalid_chunk_size", "分块长度不正确或超过服务端上限");

            // 存储卷不可用时不得写入分片，避免落到系统盘或错误位置
            EnsureStagingDirectory();
            string partPath = PartPath(uploadId);
            long expectedOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (start != expectedOffset)
                throw new MobileBackupOffsetException(expectedOffset);

            string actualChunkSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(actualChunkSha, normalizedChunkSha, StringComparison.Ordinal))
                throw new MobileBackupValidationException("chunk_sha256_mismatch", "分块 SHA256 校验失败");

            using (var stream = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            state.ReceivedBytes = expectedOffset + content.Length;
            state.UpdatedAtUtc = DateTime.UtcNow;
            SaveState(state);
            return state.ReceivedBytes;
        }
    }

    public MobileBackupCompleteResult Complete(string uploadId, MobileBackupCompleteRequest request)
    {
        uploadId = NormalizeSha256(uploadId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateCompleteRequest(request);
        MobileBackupSessionRequest session = request.Sessions[0];
        string fileSha256 = NormalizeSha256(request.FileSha256);
        string videoCodec = NormalizeVideoCodec(request.VideoCodec);
        if (!string.Equals(uploadId, fileSha256, StringComparison.Ordinal))
            throw new MobileBackupValidationException("upload_sha256_mismatch", "上传任务与完整文件 SHA256 不一致");

        lock (GetUploadLock(uploadId))
        {
            VideoRecord? completed = _database.GetVideoBySourceSession(request.SourceDeviceId, session.Id);
            if (completed != null)
            {
                if (!string.Equals(completed.ContentSha256, fileSha256, StringComparison.OrdinalIgnoreCase))
                    throw new MobileBackupValidationException("session_conflict", "该设备录像 ID 已绑定其他文件");
                MarkUploadCompleted(uploadId);
                return new MobileBackupCompleteResult("verified", fileSha256, completed.Id, true);
            }

            // 完成落盘前再次确认存储卷可用：磁盘被拔掉时按 storage_unavailable 拒收，
            // 客户端保留上传状态，磁盘接回后继续完成
            EnsureStagingDirectory();

            string finalPath;
            long fileSize;
            VideoRecord? existingFile = _database.GetVideoByContentSha256(fileSha256);
            if (existingFile != null
                && File.Exists(existingFile.FilePath)
                && FileMatchesSha256(existingFile.FilePath, fileSha256))
            {
                finalPath = ResolveFinalPath(
                    session,
                    fileSha256,
                    request.SourceDeviceId,
                    request.SourceDeviceName,
                    request.SourceDeviceKind,
                    alwaysUseSessionSuffix: true);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (!File.Exists(finalPath))
                    CopyFileAtomically(existingFile.FilePath, finalPath);
                else if (!FileMatchesSha256(finalPath, fileSha256))
                    throw new IOException("目标备份文件已存在但校验值不一致");
                fileSize = new FileInfo(finalPath).Length;
            }
            else if (TryUseStateFinalFile(LoadState(uploadId), out finalPath, out fileSize))
            {
                // 文件已原子移动但数据库写入失败时，重试完成请求可继续落库。
            }
            else
            {
                MobileBackupUploadState state = LoadState(uploadId)
                    ?? throw new MobileBackupValidationException("upload_not_found", "上传任务不存在或已过期");
                string partPath = PartPath(uploadId);
                if (!File.Exists(partPath) || new FileInfo(partPath).Length != state.TotalBytes)
                    throw new MobileBackupOffsetException(File.Exists(partPath) ? new FileInfo(partPath).Length : 0);

                string actualSha256 = ComputeFileSha256(partPath);
                if (!string.Equals(actualSha256, fileSha256, StringComparison.Ordinal))
                {
                    ResetUpload(uploadId);
                    throw new MobileBackupFileHashException();
                }

                finalPath = ResolveFinalPath(
                    session,
                    fileSha256,
                    request.SourceDeviceId,
                    request.SourceDeviceName,
                    request.SourceDeviceKind);
                state.FinalPath = finalPath;
                state.UpdatedAtUtc = DateTime.UtcNow;
                SaveState(state);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (File.Exists(finalPath))
                {
                    if (!string.Equals(ComputeFileSha256(finalPath), fileSha256, StringComparison.Ordinal))
                        throw new IOException("目标备份文件已存在但校验值不一致");
                    File.Delete(partPath);
                }
                else
                {
                    File.Move(partPath, finalPath);
                }
                fileSize = new FileInfo(finalPath).Length;
            }

            string archivePath = BuildUploadArchivePath(
                session,
                fileSha256,
                request.SourceDeviceId,
                request.SourceDeviceName,
                request.SourceDeviceKind);
            string trackingNumber = session.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
            OrderInfo? orderInfo = string.IsNullOrEmpty(trackingNumber) ? null : _orderInfoResolver(trackingNumber);
            DateTime localStartTime = session.StartedAt.ToLocalTime().DateTime;
            long recordId = _database.InsertMobileBackupRecord(
                trackingNumber,
                finalPath,
                fileSize,
                localStartTime,
                session.DurationMilliseconds / 1000.0,
                request.SourceDeviceId,
                // 昵称不逐条存：设备号才是身份，名字随时会改，
                // 映射只保存在主机设备登记表里（WebServer 启动时会用录像来源补齐）。
                "",
                session.Id,
                fileSha256,
                orderInfo,
                request.SourceDeviceKind,
                session.Mode,
                archivePath,
                string.IsNullOrWhiteSpace(archivePath)
                    ? VideoArchiveStatus.LocalOnly
                    : VideoArchiveStatus.Pending,
                videoCodec);

            if (!string.IsNullOrWhiteSpace(archivePath))
                _archivePendingCallback?.Invoke();

            DeleteStateFile(uploadId);
            MarkUploadCompleted(uploadId);
            return new MobileBackupCompleteResult("verified", fileSha256, recordId, false);
        }
    }

    private string BuildUploadArchivePath(
        MobileBackupSessionRequest session,
        string fileSha256,
        string sourceDeviceId,
        string sourceDeviceName,
        string sourceDeviceKind,
        bool alwaysUseSessionSuffix = false)
    {
        string? archiveTarget;
        try
        {
            archiveTarget = _archiveTargetResolver?.Invoke();
        }
        catch
        {
            return "";
        }
        if (string.IsNullOrWhiteSpace(archiveTarget))
            return "";

        string trackingNumber = session.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(trackingNumber)) trackingNumber = "未识别面单";
        DateTime startedAt = session.StartedAt.ToLocalTime().DateTime;
        return ArchivePathBuilder.BuildExternalUploadArchivePath(
            archiveTarget,
            sourceDeviceKind,
            sourceDeviceId,
            sourceDeviceName,
            startedAt,
            trackingNumber,
            session.Mode,
            fileSha256);
    }

    internal void CleanupExpiredUploads()
    {
        if (!Directory.Exists(_stateDirectory)) return;
        DateTime cutoff = DateTime.UtcNow - UploadRetention;
        foreach (string statePath in Directory.EnumerateFiles(_stateDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            // 只有本服务写入的上传状态文件（64 位十六进制 SHA-256 + .json）才参与清理；
            // 同目录下的设备凭据、接收器、昵称等持久状态文件不得被误删。
            if (!IsUploadStateFileName(statePath)) continue;
            try
            {
                MobileBackupUploadState? state = JsonSerializer.Deserialize<MobileBackupUploadState>(File.ReadAllText(statePath), JsonOptions);
                if (state == null || state.UpdatedAtUtc >= cutoff) continue;
                string uploadId = Path.GetFileNameWithoutExtension(statePath);
                lock (GetUploadLock(uploadId))
                    ResetUpload(uploadId);
            }
            catch
            {
                if (File.GetLastWriteTimeUtc(statePath) < cutoff)
                {
                    try { File.Delete(statePath); } catch { }
                }
            }
        }

        CleanupOrphanStagingParts();
    }

    private static bool IsUploadStateFileName(string statePath)
    {
        string fileName = Path.GetFileName(statePath);
        if (fileName.Length != 64 + 5
            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return IsUploadId(fileName[..64]);
    }

    private static bool IsUploadId(string value)
    {
        if (value.Length != 64) return false;
        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }

    /// <summary>
    /// 清理没有对应上传状态的孤儿分片：目标盘暂存目录与状态目录里遗留的旧分片。
    /// 存储位置不可用时静默跳过，等磁盘接回后再清理。
    /// </summary>
    private void CleanupOrphanStagingParts()
    {
        foreach (string directory in EnumeratePartDirectories())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string partPath in Directory.EnumerateFiles(
                    directory,
                    "*.part",
                    SearchOption.TopDirectoryOnly))
                {
                    string uploadId = Path.GetFileNameWithoutExtension(partPath);
                    if (!IsUploadId(uploadId)) continue;
                    if (File.Exists(StatePath(uploadId))) continue;
                    TryDelete(partPath);
                }
            }
            catch
            {
                // 单个目录不可读不影响其它位置的清理
            }
        }
    }

    private IEnumerable<string> EnumeratePartDirectories()
    {
        string? stagingDirectory = TryResolveStagingDirectory();
        if (stagingDirectory != null) yield return stagingDirectory;
        yield return _stateDirectory;
    }

    private object GetUploadLock(string uploadId) => _uploadLocks[GetUploadLockStripeIndex(uploadId)];

    internal static int GetUploadLockStripeIndex(string uploadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        uint hash = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(uploadId));
        return (int)(hash % UploadLockStripeCount);
    }

    private string StatePath(string uploadId) => Path.Combine(_stateDirectory, $"{uploadId}.json");

    /// <summary>
    /// 暂存分片路径。优先使用目标存储卷的暂存目录；旧版本遗留在状态目录的分片继续用到完成，
    /// 不打断升级前正在进行的上传。这里只做路径计算，解析失败也不会创建任何目录。
    /// </summary>
    private string PartPath(string uploadId)
    {
        string? stagingDirectory = TryResolveStagingDirectory();
        if (stagingDirectory != null
            && File.Exists(Path.Combine(stagingDirectory, $"{uploadId}.part")))
        {
            return Path.Combine(stagingDirectory, $"{uploadId}.part");
        }

        string legacy = Path.Combine(_stateDirectory, $"{uploadId}.part");
        if (File.Exists(legacy) || stagingDirectory == null) return legacy;
        return Path.Combine(stagingDirectory, $"{uploadId}.part");
    }

    /// <summary>
    /// 解析并准备暂存目录；存储位置不可用时抛 <see cref="MobileBackupStorageUnavailableException"/>。
    /// 录像根目录由 StorageLocationResolver 做 fail-closed 校验，挂载点断开时不会回退到系统盘上的同名目录。
    /// </summary>
    private string EnsureStagingDirectory()
    {
        string root;
        try
        {
            root = _recordingRootResolver()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            throw new MobileBackupStorageUnavailableException($"录像保存位置不可用：{ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(root))
            throw new MobileBackupStorageUnavailableException("录像保存位置为空，无法接收备份");

        string directory = Path.Combine(root, StagingDirectoryName);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            throw new MobileBackupStorageUnavailableException($"无法在 {root} 下创建暂存目录：{ex.Message}");
        }
        return directory;
    }

    private string? TryResolveStagingDirectory()
    {
        try
        {
            return EnsureStagingDirectory();
        }
        catch (MobileBackupStorageUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// 剩余空间不足时提前拒收，避免在目标盘上写入半截分片；读不到空间信息时不阻断上传。
    /// </summary>
    private static void EnsureStagingFreeSpace(string stagingDirectory, long requiredBytes)
    {
        if (requiredBytes <= 0) return;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(stagingDirectory));
            if (string.IsNullOrWhiteSpace(root)) return;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                throw new MobileBackupStorageUnavailableException($"存储位置 {root} 未就绪");
            long availableBytes = drive.AvailableFreeSpace;
            if (availableBytes >= requiredBytes) return;
            throw new MobileBackupStorageUnavailableException(
                $"存储位置剩余空间不足，本次备份还需约 {requiredBytes / (1024 * 1024)} MB，当前可用约 {availableBytes / (1024 * 1024)} MB");
        }
        catch (MobileBackupStorageUnavailableException)
        {
            throw;
        }
        catch
        {
            // 读不到空间信息时交由后续写入与完成校验兜底
        }
    }

    private bool TryUseStateFinalFile(MobileBackupUploadState? state, out string finalPath, out long fileSize)
    {
        finalPath = state?.FinalPath ?? "";
        fileSize = 0;
        if (state == null || string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath)) return false;
        if (!string.Equals(ComputeFileSha256(finalPath), state.FileSha256, StringComparison.Ordinal))
            throw new IOException("目标备份文件已存在但校验值不一致");
        fileSize = new FileInfo(finalPath).Length;
        return true;
    }

    private string ResolveFinalPath(
        MobileBackupSessionRequest session,
        string fileSha256,
        string sourceDeviceId,
        string sourceDeviceName,
        string sourceDeviceKind,
        bool alwaysUseSessionSuffix = false)
    {
        string trackingNumber = session.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(trackingNumber)) trackingNumber = "未识别面单";
        DateTime startedAt = session.StartedAt.ToLocalTime().DateTime;
        string root;
        try
        {
            root = _recordingRootResolver()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            throw new MobileBackupStorageUnavailableException($"录像保存位置不可用：{ex.Message}");
        }
        if (string.IsNullOrWhiteSpace(root))
            throw new MobileBackupStorageUnavailableException("电脑录像存储路径为空，无法接收备份");

        string dateDirectory = Path.Combine(
            Path.GetFullPath(root),
            string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "电脑上传"
                : "手机备份",
            ArchivePathBuilder.GetDeviceDirectoryName(sourceDeviceId),
            startedAt.ToString("yyyy-MM-dd"));
        string mode = VideoDatabase.NormalizeRecordingMode(session.Mode);
        string baseName = SanitizeFileName($"{trackingNumber}_{startedAt:yyyyMMdd_HHmmss}_{mode}");
        string preferredPath = Path.Combine(dateDirectory, $"{baseName}.mp4");
        if (!alwaysUseSessionSuffix && !File.Exists(preferredPath))
            return preferredPath;

        string sessionFingerprint = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sourceDeviceId}\n{session.Id}")))[..16]
            .ToLowerInvariant();
        string collisionPath = Path.Combine(dateDirectory, $"{baseName}_{sessionFingerprint}.mp4");
        if (!File.Exists(collisionPath) || FileMatchesSha256(collisionPath, fileSha256))
            return collisionPath;
        throw new IOException("目标录像文件名冲突");
    }

    private static void CopyFileAtomically(string sourcePath, string destinationPath)
    {
        string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, tempPath);
            File.Move(tempPath, destinationPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool FileMatchesSha256(string path, string sha256) =>
        string.Equals(ComputeFileSha256(path), sha256, StringComparison.Ordinal);

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = value.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "未识别面单" : value;
    }

    private MobileBackupUploadState? LoadState(string uploadId)
    {
        string path = StatePath(uploadId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MobileBackupUploadState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MobileBackupValidationException("upload_state_corrupt", $"上传任务状态损坏：{ex.Message}");
        }
    }

    private void SaveState(MobileBackupUploadState state)
    {
        Directory.CreateDirectory(_stateDirectory);
        string path = StatePath(state.UploadId);
        string tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private void ResetUpload(string uploadId)
    {
        TryDelete(PartPath(uploadId));
        DeleteStateFile(uploadId);
    }

    private void DeleteStateFile(string uploadId)
    {
        TryDelete(StatePath(uploadId));
        TryDelete($"{StatePath(uploadId)}.tmp");
        MarkUploadCompleted(uploadId);
    }

    private void MarkUploadActive(string uploadId)
    {
        bool changed;
        lock (_activeUploadsLock)
        {
            if (_activeUploads.Count == 0)
                _uploadsIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            changed = _activeUploads.Add(uploadId);
        }
        if (changed)
        {
            try { ActiveUploadsChanged?.Invoke(true); } catch { }
        }
    }

    private void MarkUploadCompleted(string uploadId)
    {
        TaskCompletionSource<bool>? completed = null;
        bool changed;
        lock (_activeUploadsLock)
        {
            changed = _activeUploads.Remove(uploadId);
            if (changed && _activeUploads.Count == 0)
                completed = _uploadsIdle;
        }
        completed?.TrySetResult(true);
        if (changed)
        {
            try { ActiveUploadsChanged?.Invoke(HasActiveUploads); } catch { }
        }
    }

    private static TaskCompletionSource<bool> CreateCompletedIdleSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new MobileBackupValidationException("invalid_sha256", "SHA256 必须是 64 位十六进制字符串");
        return normalized;
    }

    private static void ValidateTotalBytes(long totalBytes)
    {
        if (totalBytes <= 0)
            throw new MobileBackupValidationException("invalid_file_size", "文件大小必须大于 0");
    }

    private static void ValidateMimeType(string mimeType)
    {
        if (!string.Equals(mimeType?.Trim(), "video/mp4", StringComparison.OrdinalIgnoreCase))
            throw new MobileBackupValidationException("unsupported_format", "mobile-backup-v2 仅支持 video/mp4");
    }

    private static void ValidateCompleteRequest(MobileBackupCompleteRequest request)
    {
        request.SourceDeviceKind = string.Equals(
            request.SourceDeviceKind,
            "pc",
            StringComparison.OrdinalIgnoreCase)
                ? "pc"
                : "mobile";
        if (string.IsNullOrWhiteSpace(request.SourceDeviceId) || request.SourceDeviceId.Trim().Length > 128)
            throw new MobileBackupValidationException("invalid_source_device_id", "来源设备 ID 不能为空且最多 128 个字符");
        if (string.IsNullOrWhiteSpace(request.SourceDeviceName) || request.SourceDeviceName.Trim().Length > 100)
            throw new MobileBackupValidationException("invalid_source_device_name", "来源设备名称不能为空且最多 100 个字符");
        if (request.Sessions is not { Count: 1 })
            throw new MobileBackupValidationException("invalid_sessions", "每个视频必须且只能包含一条录像记录");
        MobileBackupSessionRequest session = request.Sessions[0];
        if (string.IsNullOrWhiteSpace(session.Id) || session.Id.Trim().Length > 128)
            throw new MobileBackupValidationException("invalid_session_id", "id 不能为空且最多 128 个字符");
        if ((session.TrackingNumber?.Trim().Length ?? 0) > 100)
            throw new MobileBackupValidationException("invalid_tracking_number", "面单号最多 100 个字符");
        if (session.StartedAt == default)
            throw new MobileBackupValidationException("invalid_started_at", "startedAt 不能为空");
        if (session.EndedAt == default || session.EndedAt <= session.StartedAt)
            throw new MobileBackupValidationException("invalid_ended_at", "endedAt 必须晚于 startedAt");
        if (session.MediaStartMs < 0
            || session.MediaEndMs <= session.MediaStartMs
            || session.DurationMilliseconds > TimeSpan.FromDays(2).TotalMilliseconds)
        {
            throw new MobileBackupValidationException("invalid_media_range", "媒体区间必须大于 0 且不超过 48 小时");
        }
    }

    internal static string NormalizeVideoCodec(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc" => "h264",
            "h265" or "hevc" => "h265",
            "av1" => "av1",
            _ => ""
        };
    }
}

internal sealed class MobileBackupCreateRequest
{
    public string FileSha256 { get; set; } = "";
    public long TotalBytes { get; set; }
    public string MimeType { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MobileBackupCompleteRequest
{
    [JsonRequired]
    public string FileSha256 { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoCodec { get; set; }
    public string SourceDeviceId { get; set; } = "";
    [JsonRequired]
    public string SourceDeviceName { get; set; } = "";
    // 旧手机客户端不发送此字段时保持既有行为；PC 录制工位使用 "pc"。
    public string SourceDeviceKind { get; set; } = "mobile";
    [JsonRequired]
    public List<MobileBackupSessionRequest> Sessions { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MobileBackupSessionRequest
{
    [JsonRequired]
    public string Id { get; set; } = "";
    [JsonRequired]
    public string TrackingNumber { get; set; } = "";
    [JsonRequired]
    public DateTimeOffset StartedAt { get; set; }
    [JsonRequired]
    public DateTimeOffset EndedAt { get; set; }
    [JsonRequired]
    public long MediaStartMs { get; set; }
    [JsonRequired]
    public long MediaEndMs { get; set; }
    [JsonRequired]
    public string Mode { get; set; } = "";
    [JsonRequired]
    public List<MobileBackupMarkerRequest> Markers { get; set; } = new();

    [JsonIgnore]
    public long DurationMilliseconds => MediaEndMs - MediaStartMs;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MobileBackupMarkerRequest
{
    public string Code { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public long OffsetMs { get; set; }
}

internal sealed record MobileBackupCreateResult(string UploadId, long Offset, int ChunkSize, bool FileReady);

internal sealed record MobileBackupCompleteResult(
    string Status,
    string FileSha256,
    long RecordId,
    bool AlreadyCompleted);

internal sealed class MobileBackupUploadState
{
    public string UploadId { get; set; } = "";
    public string FileSha256 { get; set; } = "";
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public string MimeType { get; set; } = "";
    public string FinalPath { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class MobileBackupValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// 保存主机的存储位置当前不可用：磁盘未接入、未就绪、不可写或空间不足。
/// 服务端按 storage_unavailable 拒收，客户端保留本地录像与上传队列并自动重试。
/// </summary>
internal sealed class MobileBackupStorageUnavailableException(string message) : Exception(message);

internal sealed class MobileBackupOffsetException(long expectedOffset) : Exception("上传偏移与服务端不一致")
{
    public long ExpectedOffset { get; } = expectedOffset;
}

internal sealed class MobileBackupFileHashException() : Exception("完整文件 SHA256 校验失败");
