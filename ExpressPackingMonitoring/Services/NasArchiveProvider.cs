using System.IO;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 基于文件系统的 NAS/UNC 归档 Provider。
/// </summary>
internal sealed class NasArchiveProvider : IArchiveProvider
{
    public async Task PublishFileAsync(
        string sourcePath,
        string destinationPath,
        long recordId,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new IOException("网络归档目标目录无效");
        Directory.CreateDirectory(destinationDirectory);

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

        string temporaryPath = destinationPath + $".{recordId}.uploading";
        // 该操作拥有此唯一临时名；仅在完整本地源仍存在时清理此前的不完整副本。
        if (File.Exists(temporaryPath) && File.Exists(sourcePath))
            File.Delete(temporaryPath);

        try
        {
            await CopyFileAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);

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
            if (File.Exists(sourcePath) && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            throw;
        }
    }

    public Task<RemoteProbeResult> ProbeAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
                return Task.FromResult(RemoteProbeResult.NotExists);
            return Task.FromResult(
                new FileInfo(path).Length == expectedSize
                    ? RemoteProbeResult.ExistsSameSize
                    : RemoteProbeResult.ExistsDifferentSize);
        }
        catch
        {
            return Task.FromResult(RemoteProbeResult.Error);
        }
    }

    public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
        ComputeSha256CoreAsync(path, cancellationToken);

    public Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        File.Move(sourcePath, destinationPath, overwrite: true);
        return Task.CompletedTask;
    }

    internal static Task<string> ComputeSha256FileAsync(string path, CancellationToken cancellationToken) =>
        ComputeSha256CoreAsync(path, cancellationToken);

    private static async Task<string> ComputeSha256CoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
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
        await source.CopyToAsync(destination, bufferSize, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        if (source.Length != destination.Length)
            throw new IOException("网络临时文件长度校验失败");
    }
}
