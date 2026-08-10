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
    /// 把本地文件发布为网络目标：复制中只做长度校验，临时文件 + 同目录改名发布；
    /// 目标已存在时先比大小、同大小才哈希（一致视为已完成，不同抛 ArchiveConflictException）。
    /// 发布后的 SHA-256 校验由调用方在 Verifying 阶段执行。
    /// </summary>
    Task PublishFileAsync(
        string sourcePath,
        string destinationPath,
        long recordId,
        string expectedSha256,
        CancellationToken cancellationToken);

    Task<RemoteProbeResult> ProbeAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken);

    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken);

    Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

internal sealed class ArchiveConflictException(string message) : IOException(message);
