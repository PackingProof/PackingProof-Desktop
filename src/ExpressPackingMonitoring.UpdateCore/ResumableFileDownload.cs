using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>断点续传下载的进度：BytesReceived 里包含上一次已经落盘的字节。</summary>
public sealed record ResumableDownloadProgress(long BytesReceived, long TotalBytes);

/// <summary>
/// 断点续传下载的结果。FilePath 是本次真正落盘的文件——正常就是 partialPath；
/// 断点文件正被另一个下载占用时会退化成同目录的一次性临时文件。
/// </summary>
public sealed record ResumableDownloadResult(
    string FilePath,
    long TotalBytes,
    long ResumedFromBytes,
    bool ReusedExistingFile);

/// <summary>
/// 带断点续传的下载，桌面端和根启动器共用。
///
/// 背景：0.0.82 这个过渡增量包有 75MB，而用户多半是店里那条不稳的网。旧实现每次下载都新建
/// 一个 download-&lt;guid&gt; 目录从头下，断一次就把已经下好的几十兆全丢掉，用户反复点重试也过不去。
/// 现在把没下完的字节留在 *.partial 里，下一次带 Range 从断点继续；跨重启依然有效（启动器的
/// 后台下载每次开机都会再试一遍）。
///
/// 三条不变量：
/// 1) 半截文件只叫 *.partial，调用方校验体积和 SHA256 之前不许当成补丁发布；
/// 2) 服务端不认 Range（回 200 或 416）就截断重来，绝不把两段不同来源的字节拼在一起；
/// 3) 已经下满的断点直接复用，不再走一遍网络。
/// </summary>
public static class ResumableFileDownload
{
    public const string PartialFileSuffix = ".partial";
    public const string InFlightMarker = ".inflight-";

    private static string UserAgent => BrandMetadata.ArtifactPrefix;

    /// <summary>停滞判据：这么久没收到任何新字节就认为这条连接已经死了（字节留着下次接着下）。</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 81920;
    private const int MaxAttempts = 2;

    /// <summary>
    /// 下载 url 到 partialPath，能续就续。
    ///
    /// 半截文件只在两种情况下会被丢掉重来：服务端不认 Range，或者调用方校验出这个文件不对。
    /// 其余情况（断线、停滞、用户关掉程序）都留着，下次从断点接着下。
    /// </summary>
    public static async Task<ResumableDownloadResult> DownloadAsync(
        HttpClient client,
        string url,
        string partialPath,
        long expectedSize,
        IProgress<ResumableDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? stallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);

        TimeSpan stall = stallTimeout is { TotalMilliseconds: > 0 } value ? value : DefaultStallTimeout;
        string fullPartialPath = Path.GetFullPath(partialPath);
        string? directory = Path.GetDirectoryName(fullPartialPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            PruneStaleDownloads(directory, Path.GetFileName(fullPartialPath));
        }

        string downloadPath = fullPartialPath;
        if (!TryOpenExclusive(fullPartialPath, out FileStream target))
        {
            // 断点文件被另一个下载占着：本次退化成同目录的一次性临时文件，不去踩它的进度。
            downloadPath = fullPartialPath + InFlightMarker + Guid.NewGuid().ToString("N");
            target = new FileStream(
                downloadPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);
        }

        await using (target)
        {
            long existing = TakeUsableLength(target, expectedSize);
            if (expectedSize > 0 && existing == expectedSize)
            {
                // 上次已经下满、只是没来得及发布：直接复用，不用再走一遍网络。
                progress?.Report(new ResumableDownloadProgress(existing, expectedSize));
                return new ResumableDownloadResult(
                    downloadPath, existing, existing, ReusedExistingFile: true);
            }

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                DownloadAttemptResult result = await TryDownloadOnceAsync(
                    client, url, target, existing, expectedSize, progress, stall, cancellationToken);
                if (result.Finished)
                {
                    return new ResumableDownloadResult(
                        downloadPath,
                        result.Received,
                        result.ResumedFrom,
                        ReusedExistingFile: result.ResumedFrom > 0);
                }

                // 416：远端文件比手里的半截还短（多半是换过包），丢掉半截从 0 重来一次。
                existing = 0;
            }

            throw new IOException("服务端不支持从断点继续下载，重试一次仍然失败");
        }
    }

    /// <summary>
    /// 下载 + 校验（体积 + SHA256），返回校验通过的文件路径。
    ///
    /// 校验不过时分两种处理：拿上一次落盘的字节拼出来的文件不对（远端换过包、字节被改写），
    /// 就清掉断点重下一次；干净下完还是不对（清单与实际文件不符），直接报失败，别让用户白等第二遍。
    /// </summary>
    public static async Task<string> DownloadVerifiedAsync(
        HttpClient client,
        string url,
        string partialPath,
        long expectedSize,
        string expectedSha256,
        IProgress<ResumableDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? stallTimeout = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            ResumableDownloadResult result = await DownloadAsync(
                client, url, partialPath, expectedSize, progress, cancellationToken, stallTimeout);

            long actualSize = new FileInfo(result.FilePath).Length;
            bool sizeMatches = expectedSize <= 0 || actualSize == expectedSize;
            bool hashMatches = expectedSha256.Length == 0
                || string.Equals(
                    ComputeSha256(result.FilePath),
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase);
            if (sizeMatches && hashMatches)
                return result.FilePath;

            bool canRetry = result.ReusedExistingFile && attempt == 0;
            // 校验不过的文件绝不是可复用的断点：留着只会让下一次接着拼出同样的错。
            TryDeleteFile(result.FilePath);
            if (!canRetry)
            {
                throw new InvalidDataException(sizeMatches
                    ? "补丁包 SHA256 校验失败"
                    : $"补丁包大小不匹配，期望 {expectedSize}，实际 {actualSize}");
            }

            progress?.Report(new ResumableDownloadProgress(0, expectedSize));
        }
    }

    /// <summary>
    /// 清掉下载目录里的过期残渣：别的版本的 *.partial，以及上次崩溃留下的一次性临时文件。
    /// 正在被别的下载写着的文件删不掉（占用中），所以这里天然不会误删活跃下载。
    /// </summary>
    public static void PruneStaleDownloads(string directory, string keepFileName)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.GetFiles(directory))
            {
                string name = Path.GetFileName(path);
                if (string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(PartialFileSuffix, StringComparison.OrdinalIgnoreCase)
                    && !name.Contains(InFlightMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理残渣失败不影响下载本身。
        }
    }

    public static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<DownloadAttemptResult> TryDownloadOnceAsync(
        HttpClient client,
        string url,
        FileStream target,
        long existing,
        long expectedSize,
        IProgress<ResumableDownloadProgress>? progress,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        bool wantRange = existing > 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 根启动器原来给附件下载带了 User-Agent，这里保持一致，避免被附件 CDN 挡掉。
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (wantRange)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using HttpResponseMessage response = await SendAsyncWithTimeoutAsync(
            client, request, cancellationToken);
        if (wantRange && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return new DownloadAttemptResult(Finished: false, Received: existing, ResumedFrom: existing);

        response.EnsureSuccessStatusCode();

        // 服务端必须明确回答"从第 existing 个字节开始"，否则这段字节是整包的一部分，接上去就错了。
        bool accepted = wantRange
            && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == existing;
        long start = accepted ? existing : 0;
        target.SetLength(start);
        target.Position = start;

        long total = response.Content.Headers.ContentLength is long contentLength && contentLength > 0
            ? start + contentLength
            : expectedSize;
        progress?.Report(new ResumableDownloadProgress(start, total));

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byte[] buffer = new byte[BufferSize];
        long received = start;
        try
        {
            while (true)
            {
                stall.CancelAfter(stallTimeout);
                int read = await source.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new ResumableDownloadProgress(received, total));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"下载停滞：{stallTimeout.TotalSeconds:0} 秒没有收到新数据，已保存 {received} 字节，下次从断点继续");
        }

        await target.FlushAsync(cancellationToken);
        return new DownloadAttemptResult(Finished: true, Received: received, ResumedFrom: start);
    }

    private static async Task<HttpResponseMessage> SendAsyncWithTimeoutAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(HandshakeTimeout);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, handshake.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"下载地址 {HandshakeTimeout.TotalSeconds:0} 秒没有响应");
        }
    }

    /// <summary>现有断点能接着用多少字节；比声明的体积还长说明是别的包的残留，不能往上拼。</summary>
    private static long TakeUsableLength(FileStream target, long expectedSize)
    {
        long length = target.Length;
        if (length <= 0) return 0;
        if (expectedSize > 0 && length > expectedSize)
        {
            target.SetLength(0);
            return 0;
        }
        return length;
    }

    private static bool TryOpenExclusive(string path, out FileStream stream)
    {
        try
        {
            stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            return true;
        }
        catch (IOException)
        {
            stream = null!;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着，下一次下载会再试。
        }
    }

    private readonly record struct DownloadAttemptResult(bool Finished, long Received, long ResumedFrom);
}
