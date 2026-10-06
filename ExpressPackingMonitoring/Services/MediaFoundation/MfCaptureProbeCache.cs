using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// Media Foundation 可用性探测的缓存。
///
/// 为什么要缓存：探测本身要"开设备 → 等首帧 → 关设备"，一次约 0.9~1.3 秒，而且没有缓存时
/// 每一次 <c>StartCamera</c> 都要先探测再正式启动——设备在 1 秒内被开两次。现场日志里
/// 摄像头休眠唤醒（22:52 / 22:58）正是这个模式：探测 0.9 秒拿到首帧并关掉设备，紧接着
/// 正式源打开后 1.5 秒一帧都没有，看门狗于是判信号丢失、又整体重启一次，整个唤醒约 4.5 秒。
/// 探测结果（这台设备能不能走 MF）只跟设备与分辨率/帧率配置有关，短时间内不会变，
/// 所以按这些参数缓存：首次启动照旧探测，唤醒等后续启动直接复用，设备只开一次。
///
/// 失效条件：超过 TTL、或调用方在启动失败后主动 <see cref="Invalidate"/>。
/// 虚拟摄像头那类"协商成功但不出帧"的黑屏保护不受影响——第一次仍然真探测。
/// </summary>
internal static class MfCaptureProbeCache
{
    /// <summary>缓存有效期：够覆盖"休眠唤醒"这类重启，又不会长期沿用过期结论。</summary>
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(MfCaptureProbe.Result Result, DateTime ExpiresAtUtc);

    /// <summary>按设备与配置取探测结果：命中缓存直接用，未命中才真的探测。</summary>
    internal static MfCaptureProbe.Result Probe(
        string symbolicLink,
        int targetWidth,
        int targetHeight,
        int targetFps,
        string? colorMatrixMode)
    {
        return Probe(
            symbolicLink,
            targetWidth,
            targetHeight,
            targetFps,
            colorMatrixMode,
            MfCaptureProbe.Probe,
            DateTime.UtcNow,
            DefaultTtl);
    }

    /// <summary>测试用：注入探测实现、当前时间与 TTL。</summary>
    internal static MfCaptureProbe.Result Probe(
        string symbolicLink,
        int targetWidth,
        int targetHeight,
        int targetFps,
        string? colorMatrixMode,
        Func<string, int, int, int, string?, TimeSpan?, MfCaptureProbe.Result> probe,
        DateTime nowUtc,
        TimeSpan ttl)
    {
        string key = BuildKey(symbolicLink, targetWidth, targetHeight, targetFps, colorMatrixMode);
        lock (Sync)
        {
            if (Entries.TryGetValue(key, out Entry? cached) && cached.ExpiresAtUtc > nowUtc)
            {
                RuntimeLog.Info(
                    "Camera",
                    $"Media Foundation 探测命中缓存（设备与配置未变）：usable={cached.Result.Usable}");
                return cached.Result;
            }
        }

        MfCaptureProbe.Result result = probe(
            symbolicLink,
            targetWidth,
            targetHeight,
            targetFps,
            colorMatrixMode,
            null);
        lock (Sync)
        {
            Entries[key] = new Entry(result, nowUtc + ttl);
        }

        return result;
    }

    /// <summary>启动/取帧失败后清掉这台设备的缓存，下次重新探测（不长期沿用错误结论）。</summary>
    internal static void Invalidate(string? symbolicLink)
    {
        if (string.IsNullOrWhiteSpace(symbolicLink))
            return;

        lock (Sync)
        {
            foreach (string key in Entries.Keys
                         .Where(key => key.StartsWith(
                             symbolicLink.Trim() + "\n",
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                Entries.Remove(key);
            }
        }
    }

    /// <summary>测试用：清空全部缓存。</summary>
    internal static void Clear()
    {
        lock (Sync)
            Entries.Clear();
    }

    private static string BuildKey(
        string symbolicLink,
        int targetWidth,
        int targetHeight,
        int targetFps,
        string? colorMatrixMode) =>
        $"{symbolicLink?.Trim() ?? ""}\n{targetWidth}x{targetHeight}@{targetFps}\n{colorMatrixMode?.Trim() ?? ""}";
}
