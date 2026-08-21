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
/// Provider 提供发布、校验、存在探测与删除能力；删除仅用于 NAS 容量循环清理，
/// 且必须在 Provider 内部校验目标路径属于调用方允许的根目录（防御式设计）。
/// RenameAsync 仅用于把本次刚上传的损坏目标改名 .corrupt（自己的不完整文件，不是删除既有文件）。
/// </summary>
internal interface IArchiveProvider
{
    /// <summary>删除结果：Deleted=已删除；NotFound=目标明确不存在（视为已成功）。</summary>
    internal enum DeleteOutcome
    {
        Deleted,
        NotFound
    }

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

    /// <summary>
    /// 删除网络归档文件。allowedRoots 限定可删除的根目录（规范化、忽略大小写），
    /// 目标不在任一允许根下直接拒绝；只有文件明确不存在才返回 NotFound，
    /// 网络/权限/路径异常一律抛出，由调用方保持数据库原状态。
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(
        string path,
        IReadOnlyList<string> allowedRoots,
        CancellationToken cancellationToken);

    Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);
}

/// <summary>可选的传输节流能力；第三方 Provider 不实现时保持原有行为。</summary>
internal interface IArchiveTransferThrottleAware
{
    void SetTransferThrottle(ArchiveTransferThrottle? throttle);
}

internal sealed class ArchiveConflictException(string message) : IOException(message);
