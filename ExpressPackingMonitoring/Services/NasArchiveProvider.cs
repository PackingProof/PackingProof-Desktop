using System.IO;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 基于文件系统的 NAS/UNC 归档 Provider。
/// </summary>
internal sealed class NasArchiveProvider : IArchiveProvider, IArchiveTransferThrottleAware
{
    /// <summary>残留上传临时文件超过该年龄且本地源仍存在时，发布前清理。</summary>
    private static readonly TimeSpan StaleTempCleanupAge = TimeSpan.FromHours(24);
    private static readonly ConcurrentDictionary<string, byte> ActiveTemporaryFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private ArchiveTransferThrottle? _transferThrottle;

    public void SetTransferThrottle(ArchiveTransferThrottle? throttle) =>
        Volatile.Write(ref _transferThrottle, throttle);

    public async Task PublishFileAsync(
        string sourcePath,
        string destinationPath,
        long recordId,
        string expectedSha256,
        string attemptToken,
        CancellationToken cancellationToken)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new IOException("网络归档目标目录无效");
        Directory.CreateDirectory(destinationDirectory);

        string attemptSuffix = string.IsNullOrWhiteSpace(attemptToken)
            ? Guid.NewGuid().ToString("N")[..8]
            : attemptToken;
        string temporaryPath = destinationPath + $".{recordId}.{attemptSuffix}.uploading";
        if (!ActiveTemporaryFiles.TryAdd(temporaryPath, 0))
            throw new IOException("同一网络归档临时文件已有任务正在使用");
        bool temporaryOwned = false;

        try
        {
            CleanupStaleTemporaryFiles(
                destinationDirectory,
                Path.GetFileName(destinationPath),
                sourcePath);

            long sourceLength = new FileInfo(sourcePath).Length;
            if (File.Exists(destinationPath))
            {
                if (new FileInfo(destinationPath).Length != sourceLength)
                    throw new ArchiveConflictException("网络目标已存在同名但大小不同的文件，已禁止覆盖");
                string existingHash = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
                if (string.Equals(expectedSha256, existingHash, StringComparison.OrdinalIgnoreCase))
                    return;
                throw new ArchiveConflictException("网络目标已存在同名但内容不同的文件，已禁止覆盖");
            }

            await CopyFileAsync(
                    sourcePath,
                    temporaryPath,
                    cancellationToken,
                    () => temporaryOwned = true,
                    () => Volatile.Read(ref _transferThrottle))
                .ConfigureAwait(false);

            if (File.Exists(destinationPath))
            {
                string concurrentHash = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(expectedSha256, concurrentHash, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveConflictException("发布时发现同名文件冲突，已禁止覆盖");
                File.Delete(temporaryPath);
                return;
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch
        {
            if (temporaryOwned && File.Exists(sourcePath) && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            throw;
        }
        finally
        {
            ActiveTemporaryFiles.TryRemove(temporaryPath, out _);
        }
    }

    public async Task<RemoteProbeResult> ProbeAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        // 与 RemoteFileProbe 共用全局门禁：同一时间最多一个 SMB 探测，
        // 挂死时拿不到门禁按 Error 处理，避免线程池被放弃的探测堆积。
        if (!await RemoteFileProbe.ProbeGate.WaitAsync(
                RemoteFileProbe.ProbeTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            return RemoteProbeResult.Error;
        }
        try
        {
            return await Task.Run(
                () => ProbeCore(path, expectedSize),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoteFileProbe.ProbeGate.Release();
        }
    }

    private static RemoteProbeResult ProbeCore(string path, long expectedSize)
    {
        try
        {
            if (!File.Exists(path))
                return RemoteProbeResult.NotExists;
            return new FileInfo(path).Length == expectedSize
                ? RemoteProbeResult.ExistsSameSize
                : RemoteProbeResult.ExistsDifferentSize;
        }
        catch
        {
            return RemoteProbeResult.Error;
        }
    }

    public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
        ComputeSha256CoreAsync(path, cancellationToken, () => Volatile.Read(ref _transferThrottle));

    public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
        string path,
        IReadOnlyList<string> allowedRoots,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new IOException("网络归档删除路径无效");
        if (allowedRoots == null
            || allowedRoots.Count == 0
            || !IsUnderAnyAllowedRoot(path, allowedRoots))
        {
            throw new InvalidOperationException(
                "拒绝删除未授权根目录之外的网络文件");
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException("网络归档目标是目录，拒绝删除");
            File.Delete(path);
            return Task.FromResult(IArchiveProvider.DeleteOutcome.Deleted);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(IArchiveProvider.DeleteOutcome.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(IArchiveProvider.DeleteOutcome.NotFound);
        }
        catch (Exception ex) when (
            ex is not InvalidOperationException
                and not IOException)
        {
            throw new IOException($"NAS 删除失败：{ex.Message}", ex);
        }
    }

    public Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        File.Move(sourcePath, destinationPath, overwrite: true);
        return Task.CompletedTask;
    }

    internal static Task<string> ComputeSha256FileAsync(string path, CancellationToken cancellationToken) =>
        ComputeSha256CoreAsync(path, cancellationToken, throttleProvider: null);

    /// <summary>
    /// 清理同一目标下超过 24 小时的残留上传临时文件；只清理本地源仍存在的目标，
    /// 不删除可能仍在写入的本次尝试临时文件。
    /// </summary>
    private static void CleanupStaleTemporaryFiles(
        string directory,
        string destinationFileName,
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(destinationFileName) || !File.Exists(sourcePath))
            return;
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(
                         directory,
                         "*.uploading",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string name = Path.GetFileName(candidate);
                    if (!name.StartsWith(
                            destinationFileName + ".",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate)
                        < StaleTempCleanupAge)
                    {
                        continue;
                    }
                    if (ActiveTemporaryFiles.ContainsKey(candidate)
                        || IsTemporaryFileInUse(candidate))
                    {
                        // 进程内仍有归档任务持有该临时文件，即使时间戳很旧也不能清理。
                        continue;
                    }
                    File.Delete(candidate);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsTemporaryFileInUse(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsUnderAnyAllowedRoot(
        string path,
        IReadOnlyList<string> allowedRoots)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        foreach (string root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string rootFull;
            try
            {
                rootFull = Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            catch
            {
                continue;
            }
            if (fullPath.StartsWith(
                    rootFull + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<string> ComputeSha256CoreAsync(
        string path,
        CancellationToken cancellationToken,
        Func<ArchiveTransferThrottle?>? throttleProvider)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            ArchiveTransferThrottle? throttle = throttleProvider?.Invoke();
            if (throttle != null)
                await throttle.WaitAsync(read, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        Action onDestinationCreated,
        Func<ArchiveTransferThrottle?> throttleProvider)
    {
        const int bufferSize = 1024 * 1024;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        onDestinationCreated();
        byte[] buffer = new byte[bufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            ArchiveTransferThrottle? throttle = throttleProvider();
            if (throttle != null)
                await throttle.WaitAsync(read, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        if (source.Length != destination.Length)
            throw new IOException("网络临时文件长度校验失败");
    }
}
