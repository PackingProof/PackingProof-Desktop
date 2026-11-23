using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.UpdateCore;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal sealed class PhotoCaptureService
{
    private readonly PhotoStore _store;
    private readonly Func<string> _localRootProvider;
    private readonly Func<string?> _archiveRootProvider;
    private readonly SemaphoreSlim _fileOperationGate = new(1, 1);
    private int _activeArchiveOperations;

    internal PhotoCaptureService(
        PhotoStore store,
        Func<string> localRootProvider,
        Func<string?>? archiveRootProvider = null)
    {
        _store = store;
        _localRootProvider = localRootProvider;
        _archiveRootProvider = archiveRootProvider ?? (() => null);
    }

    internal async Task<PhotoRecord> CaptureAsync(
        BitmapSource frame,
        string trackingNumber,
        string mode,
        string sourceDeviceId,
        string sourceDeviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsFrozen == false)
            frame.Freeze();
        return await Task.Run(
            () => CaptureCore(
                frame,
                trackingNumber,
                mode,
                sourceDeviceId,
                sourceDeviceName,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PhotoRecord> CaptureAsync(
        Mat frame,
        string trackingNumber,
        string mode,
        string sourceDeviceId,
        string sourceDeviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return await Task.Run(
            () =>
            {
                using (frame)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BitmapSource bitmap = CreateBitmap(frame);
                    return CaptureCore(
                        bitmap,
                        trackingNumber,
                        mode,
                        sourceDeviceId,
                        sourceDeviceName,
                        cancellationToken);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task DeleteLocalPhotoAsync(
        long photoId,
        string reason = "UserRequested",
        CancellationToken cancellationToken = default) =>
        await Task.Run(
            () => DeleteCore(photoId, reason, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    private PhotoRecord CaptureCore(
        BitmapSource frame,
        string trackingNumber,
        string mode,
        string sourceDeviceId,
        string sourceDeviceName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset createdAt = DateTimeOffset.Now;
        string root = ResolveWritableRoot(_localRootProvider());
        string dateFolder = Path.Combine(
            root,
            createdAt.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateFolder);
        string baseName = BuildBaseName(trackingNumber, mode, createdAt);
        string finalPath = UniquePath(dateFolder, baseName, ".jpg");
        string partialPath = finalPath + ".partial";
        bool published = false;
        try
        {
            using (FileStream stream = new(
                       partialPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(frame));
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            published = true;

            long size = new FileInfo(finalPath).Length;
            string? archiveRoot = _archiveRootProvider();
            string archiveStatus = string.IsNullOrWhiteSpace(archiveRoot)
                ? "Disabled"
                : "Pending";
            PhotoRecord record = _store.Add(
                trackingNumber,
                mode,
                finalPath,
                sourceDeviceId,
                sourceDeviceName,
                frame.PixelWidth,
                frame.PixelHeight,
                size,
                createdAt,
                "",
                archiveStatus);
            if (archiveStatus == "Pending")
            {
                Interlocked.Increment(ref _activeArchiveOperations);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ArchivePhotoAsync(record.Id, archiveRoot!);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeArchiveOperations);
                    }
                });
            }
            return record;
        }
        catch
        {
            TryDelete(partialPath);
            if (published)
            {
                TryDelete(finalPath);
            }
            throw;
        }
    }

    internal async Task ResumePendingArchivesAsync()
    {
        string? archiveRoot = _archiveRootProvider();
        if (string.IsNullOrWhiteSpace(archiveRoot))
            return;
        Interlocked.Increment(ref _activeArchiveOperations);
        try
        {
            IReadOnlyList<PhotoRecord> pending;
            do
            {
                pending = _store.QueryPendingArchives();
                foreach (PhotoRecord record in pending)
                    await ArchivePhotoAsync(record.Id, archiveRoot).ConfigureAwait(false);
            }
            while (pending.Count > 0);
        }
        finally
        {
            Interlocked.Decrement(ref _activeArchiveOperations);
        }
    }

    internal Task<bool> HasOutstandingArchiveWorkAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsArchiveActive)
                    return true;
                if (string.IsNullOrWhiteSpace(_archiveRootProvider()))
                    return false;
                return _store.HasPendingArchives();
            },
            cancellationToken);

    internal async Task RetryArchiveAsync(long photoId)
    {
        Interlocked.Increment(ref _activeArchiveOperations);
        try
        {
            string? archiveRoot = _archiveRootProvider();
            if (string.IsNullOrWhiteSpace(archiveRoot))
                throw new InvalidOperationException("未配置 NAS 备份位置");
            if (!_store.MarkArchivePending(photoId))
                throw new InvalidOperationException("照片不存在或不需要重试归档");
            await ArchivePhotoAsync(photoId, archiveRoot).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeArchiveOperations);
        }
    }

    private async Task ArchivePhotoAsync(long photoId, string archiveRoot)
    {
        await _fileOperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PhotoRecord? record = _store.Get(photoId);
            if (record == null)
                return;
            if (!File.Exists(record.FilePath))
            {
                _store.UpdateArchiveState(
                    photoId,
                    "Failed",
                    "",
                    "Local file is missing",
                    record.ArchiveAttempts + 1);
                return;
            }

            string archivePath = "";
            string error = "";
            int attempts = record.ArchiveAttempts;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                attempts++;
                string partialPath = "";
                try
                {
                    string archiveFolder = Path.Combine(
                        archiveRoot,
                        "PhotoBackup",
                        record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd"));
                    Directory.CreateDirectory(archiveFolder);
                    archivePath = UniquePath(
                        archiveFolder,
                        Path.GetFileNameWithoutExtension(record.FilePath),
                        ".jpg");
                    partialPath = archivePath + ".partial";
                    using (FileStream source = new(
                               record.FilePath,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.Read,
                               64 * 1024,
                               FileOptions.SequentialScan))
                    using (FileStream destination = new(
                               partialPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               64 * 1024,
                               FileOptions.WriteThrough))
                    {
                        await source.CopyToAsync(destination).ConfigureAwait(false);
                        await destination.FlushAsync().ConfigureAwait(false);
                        destination.Flush(flushToDisk: true);
                    }
                    File.Move(partialPath, archivePath);
                    _store.UpdateArchiveState(
                        photoId,
                        "Completed",
                        archivePath,
                        "",
                        attempts);
                    return;
                }
                catch (Exception ex)
                {
                    TryDelete(partialPath);
                    error = ex.Message;
                    _store.UpdateArchiveState(
                        photoId,
                        "Pending",
                        "",
                        error,
                        attempts);
                    if (attempt < 2)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(1 << attempt)).ConfigureAwait(false);
                    }
                }
            }
            _store.UpdateArchiveState(
                photoId,
                "Failed",
                "",
                error,
                attempts);
        }
        catch
        {
            // 调用方只需要可持久化的状态；后台归档失败不能变成未观察异常。
            try
            {
                _store.UpdateArchiveState(
                    photoId,
                    "Failed",
                    "",
                    "Photo archive worker failed",
                    1);
            }
            catch { }
        }
        finally
        {
            _fileOperationGate.Release();
        }
    }

    private readonly object _deleteSync = new();

    internal bool IsArchiveActive =>
        Volatile.Read(ref _activeArchiveOperations) != 0;

    private void DeleteCore(
        long photoId,
        string reason,
        CancellationToken cancellationToken)
    {
        _fileOperationGate.Wait(cancellationToken);
        try
        {
            lock (_deleteSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PhotoRecord record = _store.Get(photoId)
                    ?? throw new InvalidOperationException("照片记录不存在");
                if (!File.Exists(record.FilePath))
                {
                    _store.MarkDeleted(photoId, reason);
                    return;
                }

                string trashPath =
                    record.FilePath + "." + Guid.NewGuid().ToString("N") + ".delete";
                File.Move(record.FilePath, trashPath);
                try
                {
                    if (!_store.MarkDeleted(photoId, reason))
                        throw new InvalidOperationException("照片记录状态更新失败");
                }
                catch
                {
                    try
                    {
                        if (File.Exists(trashPath) && !File.Exists(record.FilePath))
                            File.Move(trashPath, record.FilePath);
                    }
                    catch { }
                    throw;
                }

                // 数据库已经确认删除后，残留的 .delete 文件不影响用户事实；
                // 可留给后续维护清理，不能为了物理清理失败再把记录和文件拆成相反状态。
                TryDelete(trashPath);
            }
        }
        finally
        {
            _fileOperationGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { File.Delete(path); } catch { }
    }

    private static string ResolveWritableRoot(string preferredRoot)
    {
        string root = Path.GetFullPath(preferredRoot);
        try
        {
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            return root;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                BrandMetadata.ArtifactPrefix + "Photos");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string BuildBaseName(
        string trackingNumber,
        string mode,
        DateTimeOffset createdAt)
    {
        string normalized = new string(
            (string.IsNullOrWhiteSpace(trackingNumber) ? "Photo" : trackingNumber)
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '_')
            .Take(80)
            .ToArray());
        string modeSuffix = string.Equals(mode, "退货", StringComparison.Ordinal)
            ? "Return"
            : "Ship";
        return $"{normalized}_{createdAt:yyyyMMdd_HHmmss}_{modeSuffix}";
    }

    private static BitmapSource CreateBitmap(Mat frame)
    {
        if (frame.Empty() || frame.Type() != MatType.CV_8UC3)
            throw new InvalidOperationException("摄像头帧格式不可用于拍照");
        using Mat continuous = frame.Clone();
        int length = checked(continuous.Rows * continuous.Cols * 3);
        byte[] pixels = new byte[length];
        Marshal.Copy(continuous.Data, pixels, 0, length);
        BitmapSource bitmap = BitmapSource.Create(
            continuous.Cols,
            continuous.Rows,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgr24,
            palette: null,
            pixels,
            checked((int)continuous.Step()));
        bitmap.Freeze();
        return bitmap;
    }

    private static string UniquePath(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        int suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{baseName}_{suffix++}{extension}");
        }
        return path;
    }
}
