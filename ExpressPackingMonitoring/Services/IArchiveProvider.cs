using System.IO;

namespace ExpressPackingMonitoring.Services;

internal enum RemoteProbeResult
{
    NotExists,
    ExistsSameSize,
    ExistsDifferentSize,
    Error
}

/// <summary>
/// 归档传输抽象：本版只有 NAS（文件系统）实现，未来可扩展云存储 Provider。
/// </summary>
internal interface IArchiveProvider
{
    /// <summary>
    /// 把本地文件复制为网络目标：临时文件 + 长度/SHA-256 校验 + 同目录改名发布。
    /// 返回已校验的源文件 SHA-256；目标存在且内容不同时抛出 ArchiveConflictException。
    /// </summary>
    Task<string> CopyVerifiedAsync(
        string sourcePath,
        string destinationPath,
        long recordId,
        CancellationToken cancellationToken);

    Task<RemoteProbeResult> ProbeAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken);

    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

internal sealed class ArchiveConflictException(string message) : IOException(message);
