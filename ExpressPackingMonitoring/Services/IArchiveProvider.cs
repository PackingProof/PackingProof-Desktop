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
/// Provider 只提供发布、校验与存在探测能力，不提供删除能力——
/// NAS 是纯归档目标，程序只上传、永不删除 NAS 文件。
/// RenameAsync 仅用于把本次刚上传的损坏目标改名 .corrupt（自己的不完整文件，不是删除既有文件）。
/// </summary>
internal interface IArchiveProvider
{
    /// <summary>
    /// 把本地文件发布为网络目标：复制中只做长度校验，临时文件 + 同目录改名发布；
    /// 目标已存在时先比大小、同大小才哈希（一致视为已完成，不同抛 ArchiveConflictException）；
    /// attemptToken 用于生成本次尝试唯一的上传临时文件名。
    /// 发布后的 SHA-256 校验由调用方在 Verifying 阶段执行。
    /// </summary>
    Task PublishFileAsync(
        string sourcePath,
        string destinationPath,
        long recordId,
        string expectedSha256,
        string attemptToken,
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
}

internal sealed class ArchiveConflictException(string message) : IOException(message);
