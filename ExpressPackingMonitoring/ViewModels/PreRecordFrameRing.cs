using OpenCvSharp;
using ExpressPackingMonitoring.Services.MediaFoundation;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>一次入环的结果：是否复用了旧槽位、是否因换分辨率重建了缓冲。</summary>
internal readonly record struct PreRecordAddResult(
    bool ReusedSlot,
    bool ReusedRawSlot,
    bool ResetAfterSizeChange,
    int PreviousWidth,
    int PreviousHeight);

/// <summary>
/// 预录里的一帧：BGR（DirectShow 这类没有原始采样的路径）或原始采样（NV12 / YUY2）。
///
/// 原始采样这一路的转换、旋转和水印都在写入端做，环形缓冲只负责把字节留住：
/// NV12 是 1.5 字节/像素，同样秒数只要一半内存，拷贝也只有一半。
///
/// <see cref="Bytes"/> 一律按 BGR 口径（3 字节/像素）折算，容量、进度条、预录秒数沿用原来那套口径；
/// 不这么折算的话，同样的 384MB 会突然变成两倍时长，录制时间线跟着一起错。
/// </summary>
internal sealed class PreRecordPayload : IDisposable
{
    private Mat? _bgr;
    private PreRecordRawPayload? _raw;

    private PreRecordPayload(Mat? bgr, PreRecordRawPayload? raw, DateTime timestamp, long bytes)
    {
        _bgr = bgr;
        _raw = raw;
        Timestamp = timestamp;
        Bytes = bytes;
    }

    internal DateTime Timestamp { get; set; }

    /// <summary>按 BGR 口径折算的占用字节（NV12 的实际内存约为它的一半）。</summary>
    internal long Bytes { get; }

    internal bool IsRaw => _raw != null;

    internal Mat? Bgr => _bgr;

    internal PreRecordRawPayload? Raw => _raw;

    internal static PreRecordPayload FromBgr(Mat bgr, DateTime timestamp, long bytes) =>
        new(bgr, null, timestamp, bytes);

    internal static PreRecordPayload FromRaw(PreRecordRawPayload raw, DateTime timestamp, long bytes) =>
        new(null, raw, timestamp, bytes);

    internal Mat? TakeBgr()
    {
        Mat? value = _bgr;
        _bgr = null;
        return value;
    }

    internal PreRecordRawPayload? TakeRaw()
    {
        PreRecordRawPayload? value = _raw;
        _raw = null;
        return value;
    }

    public void Dispose()
    {
        _bgr?.Dispose();
        _bgr = null;
        _raw?.Dispose();
        _raw = null;
    }
}

/// <summary>
/// 事件预录的原始帧环形缓冲（画质不降：存的就是采集到的原始 BGR 帧）。
///
/// 装满之后不再每帧新分配一块整帧：最旧那一格直接接住新帧（覆盖写）。整块分配要重新拿内存
/// 再碰一遍新页，1080p 一帧约 1.5 ms、2K 约 2 ms，纯拷贝只要 0.09 / 0.7 ms
/// （见 Tools/Diagnose-CameraPipeline.ps1 第 6 节），所以这里省下的是分配那一段。
///
/// 槽位有两种载荷：BGR（<see cref="Add"/>）和原始采样（<see cref="AddRaw"/>）。后者只存 1.5 字节/像素，
/// 转换与水印在写入端做；两种载荷各自的覆盖写路径互相独立，载荷类型不匹配时宁可丢掉旧槽位重建。
///
/// 槽位里的像素只有本类会写：取帧（<see cref="TakeUntil"/>）把 Mat 的所有权交给录像队列，
/// 该槽位随即从环里消失，之后由新帧重新长出来 —— 绝不会回头复用已经交出去的 Mat。
///
/// 线程约定：所有成员都要求调用方持有 MainViewModel 的 _eventBufferLock。本类不自己加锁，
/// 否则"取帧线程持自己的锁、采集线程持事件缓冲锁"时，读到的帧数与帧内容可能对不上。
/// </summary>
internal sealed class PreRecordFrameRing : IDisposable
{
    private readonly LinkedList<PreRecordPayload> _slots = new();
    private long _bytes;
    private int _width;
    private int _height;

    public int Count => _slots.Count;

    public long Bytes => _bytes;

    /// <summary>当前槽位的帧宽度；0 表示缓冲是空的。</summary>
    public int Width => _width;

    public int Height => _height;

    public long DroppedFrames { get; private set; }

    public bool HasWrapped { get; private set; }

    /// <summary>按容量折算的显示帧数，用于界面进度条；由调用方在配置变化时刷新。</summary>
    public int DisplayCapacityFrames { get; set; }

    /// <summary>当前槽位里的 Mat 实例（最旧在前）。单元测试用它断言满环时复用同一块内存。</summary>
    internal IReadOnlyList<PreRecordPayload> Slots => _slots.ToArray();

    /// <summary>
    /// 追加一帧。缓冲已经装不下这一帧时复用最旧槽位（覆盖写），未满或换分辨率后按需新建。
    /// </summary>
    public PreRecordAddResult Add(Mat frame, DateTime timestamp, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);

        long bytes = GetFrameBytes(frame.Cols, frame.Rows);
        return AddCore(
            frame.Cols,
            frame.Rows,
            bytes,
            timestamp,
            maxBytes,
            reusedRaw: false,
            tryReuse: payload =>
                !payload.IsRaw
                && payload.Bgr is { } target
                && target.Rows == frame.Rows
                && target.Cols == frame.Cols
                && target.Type() == frame.Type()
                    ? CopyInto(() => frame.CopyTo(target))
                    : ReuseResult.NotReusable,
            createNew: () => PreRecordPayload.FromBgr(frame.Clone(), timestamp, bytes));
    }

    /// <summary>
    /// 追加一帧原始采样（NV12 / YUY2）。只留字节，不做转换；转换与水印由写入端负责。
    /// </summary>
    public PreRecordAddResult AddRaw(CameraRawFrame raw, DateTime timestamp, long maxBytes)
    {
        long bytes = GetFrameBytes(raw.Width, raw.Height);
        return AddCore(
            raw.Width,
            raw.Height,
            bytes,
            timestamp,
            maxBytes,
            reusedRaw: true,
            tryReuse: payload =>
                payload.Raw is { } target
                && target.Width == raw.Width
                && target.Height == raw.Height
                && target.Format == raw.Format
                    ? CopyInto(() => raw.CopyTo(target.Buffer))
                    : ReuseResult.NotReusable,
            createNew: () => PreRecordPayload.FromRaw(
                new PreRecordRawPayload(
                    raw.CopyToCompactMat(),
                    raw.Width,
                    raw.Height,
                    raw.Format,
                    raw.UsesBt709),
                timestamp,
                bytes));
    }

    /// <summary>
    /// 取走 eventTime 及之前的所有载荷，所有权一并交给调用方（录像队列），返回取走的折算字节数。
    /// </summary>
    public long TakeUntil(
        DateTime eventTime,
        List<PreRecordPayload> payloads,
        out DateTime? firstTimestamp)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        long takenBytes = 0;
        firstTimestamp = null;
        LinkedListNode<PreRecordPayload>? node = _slots.First;
        while (node != null)
        {
            LinkedListNode<PreRecordPayload>? next = node.Next;
            PreRecordPayload payload = node.Value;
            if (payload.Timestamp <= eventTime)
            {
                _slots.Remove(node);
                _bytes -= payload.Bytes;
                payloads.Add(payload);
                firstTimestamp ??= payload.Timestamp;
                takenBytes += payload.Bytes;
            }

            node = next;
        }

        return takenBytes;
    }

    /// <summary>容量被调小（配置变化）后裁掉多余帧，返回裁掉的数量。</summary>
    public int TrimTo(long maxBytes)
    {
        int removed = 0;
        while (_slots.First != null && _bytes > maxBytes)
        {
            PreRecordPayload oldest = _slots.First.Value;
            _slots.RemoveFirst();
            _bytes -= oldest.Bytes;
            oldest.Dispose();
            removed++;
        }

        if (removed > 0)
        {
            DroppedFrames += removed;
            HasWrapped = true;
        }

        return removed;
    }

    /// <summary>按新的容量上限重算显示帧数，并清掉上一轮的丢弃/已满标记。</summary>
    public void RefreshCapacity(long maxBytes, int fallbackCapacityFrames)
    {
        long bytesPerFrame = _slots.First?.Value.Bytes ?? 0;
        DisplayCapacityFrames = bytesPerFrame > 0 && maxBytes > 0
            ? Math.Max(1, (int)Math.Min(int.MaxValue, maxBytes / bytesPerFrame))
            : Math.Max(0, fallbackCapacityFrames);
        DroppedFrames = 0;
        HasWrapped = false;
    }

    public void ResetDropCounters()
    {
        DroppedFrames = 0;
        HasWrapped = false;
    }

    /// <summary>清空并释放所有槽位（停摄像头、关开关、退出时用）。</summary>
    public void Clear()
    {
        ClearCore();
        DisplayCapacityFrames = 0;
    }

    public void Dispose() => Clear();

    private enum ReuseResult
    {
        Reused,
        NotReusable
    }

    private static ReuseResult CopyInto(Action copy)
    {
        copy();
        return ReuseResult.Reused;
    }

    private PreRecordAddResult AddCore(
        int width,
        int height,
        long bytes,
        DateTime timestamp,
        long maxBytes,
        bool reusedRaw,
        Func<PreRecordPayload, ReuseResult> tryReuse,
        Func<PreRecordPayload> createNew)
    {
        bool resetAfterSizeChange = false;
        int previousWidth = _width;
        int previousHeight = _height;
        if (_width > 0 && _height > 0 && (_width != width || _height != height))
        {
            ClearCore();
            resetAfterSizeChange = true;
        }

        _width = width;
        _height = height;

        bool reused = false;
        if (maxBytes > 0 && _bytes + bytes > maxBytes && _slots.First != null)
        {
            PreRecordPayload oldest = _slots.First.Value;
            _slots.RemoveFirst();
            _bytes -= oldest.Bytes;
            DroppedFrames++;
            HasWrapped = true;

            if (tryReuse(oldest) == ReuseResult.Reused)
            {
                // 覆盖写：最旧那一格直接接住新帧，省掉这一次整块分配
                oldest.Timestamp = timestamp;
                _slots.AddLast(oldest);
                _bytes += oldest.Bytes;
                reused = true;
            }
            else
            {
                oldest.Dispose();
            }
        }

        if (!reused)
        {
            PreRecordPayload payload = createNew();
            _slots.AddLast(payload);
            _bytes += payload.Bytes;
        }

        if (DisplayCapacityFrames <= 0 && bytes > 0 && maxBytes > 0)
            DisplayCapacityFrames = Math.Max(1, (int)Math.Min(int.MaxValue, maxBytes / bytes));

        TrimTo(maxBytes);
        return new PreRecordAddResult(reused, reusedRaw && reused, resetAfterSizeChange, previousWidth, previousHeight);
    }

    private void ClearCore()
    {
        foreach (PreRecordPayload payload in _slots)
            payload.Dispose();
        _slots.Clear();
        _bytes = 0;
        _width = 0;
        _height = 0;
        DroppedFrames = 0;
        HasWrapped = false;
    }

    /// <summary>容量口径固定按 BGR（3 字节/像素）折算，原始采样实际占用是它的一半。</summary>
    private static long GetFrameBytes(int width, int height) =>
        (long)width * height * 3;
}
