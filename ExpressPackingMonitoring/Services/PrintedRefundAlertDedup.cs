namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 一段录像里同一个运单号只报一次打印后退款警告。退款状态可能在三个时间点才拿到：
/// 扫码开始时、打包过程中、以及同码停录之后——不管哪条通道先知道，都只播一次。
/// 键按"录像会话 + 运单号"归一，裸号和包裹号指向同一张面单时也能对上。
/// </summary>
internal sealed class PrintedRefundAlertDedup
{
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _expirations = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    internal PrintedRefundAlertDedup(TimeProvider? timeProvider = null, TimeSpan? retention = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
        if (_retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    /// <summary>第一次返回 true（该报），同一会话同一运单号再来返回 false（已经报过）。</summary>
    internal bool TryMark(string? sessionKey, string? trackingNumber)
    {
        string key = BuildKey(sessionKey, trackingNumber);
        if (key.Length == 0) return true;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            Prune(now);
            if (_expirations.TryGetValue(key, out DateTimeOffset expiresAt) && expiresAt > now)
                return false;
            _expirations[key] = now + _retention;
            return true;
        }
    }

    /// <summary>拿不到录像会话或运单号时返回空串：那种情况没法去重，调用方按"该报"处理。</summary>
    internal static string BuildKey(string? sessionKey, string? trackingNumber)
    {
        string session = (sessionKey ?? "").Trim();
        string tracking = JdBarcodePolicy.Waybill(trackingNumber);
        return session.Length == 0 || tracking.Length == 0 ? "" : $"{session}|{tracking}";
    }

    private void Prune(DateTimeOffset now)
    {
        if (_expirations.Count == 0) return;

        List<string>? expired = null;
        foreach ((string key, DateTimeOffset expiresAt) in _expirations)
        {
            if (expiresAt > now) continue;
            (expired ??= []).Add(key);
        }

        if (expired == null) return;
        foreach (string key in expired)
            _expirations.Remove(key);
    }
}
