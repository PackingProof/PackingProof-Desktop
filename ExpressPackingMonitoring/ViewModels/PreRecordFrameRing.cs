using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>一次入环的结果：是否复用了旧槽位、是否因换分辨率重建了缓冲。</summary>
internal readonly record struct PreRecordAddResult(
    bool ReusedSlot,
    bool ResetAfterSizeChange,
    int PreviousWidth,
    int PreviousHeight);

/// <summary>
/// 事件预录的原始帧环形缓冲（画质不降：存的就是采集到的原始 BGR 帧）。
///
/// 装满之后不再每帧新分配一块整帧：最旧那一格直接接住新帧（覆盖写）。整块分配要重新拿内存
/// 再碰一遍新页，1080p 一帧约 1.5 ms、2K 约 2 ms，纯拷贝只要 0.09 / 0.7 ms
/// （见 Tools/Diagnose-CameraPipeline.ps1 第 6 节），所以这里省下的是分配那一段。
///
/// 槽位里的像素只有本类会写：取帧（<see cref="TakeUntil"/>）把 Mat 的所有权交给录像队列，
/// 该槽位随即从环里消失，之后由新帧重新长出来 —— 绝不会回头复用已经交出去的 Mat。
///
/// 线程约定：所有成员都要求调用方持有 MainViewModel 的 _eventBufferLock。本类不自己加锁，
/// 否则"取帧线程持自己的锁、采集线程持事件缓冲锁"时，读到的帧数与帧内容可能对不上。
/// </summary>
internal sealed class PreRecordFrameRing : IDisposable
{
    private sealed class Slot
    {
        public required Mat Frame { get; init; }
        public required long Bytes { get; init; }
        public DateTime Timestamp { get; set; }
        public long Sequence { get; set; }
    }

    private readonly LinkedList<Slot> _slots = new();
    private long _bytes;
    private long _sequence;
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
    internal IReadOnlyList<Mat> SlotFrames => _slots.Select(slot => slot.Frame).ToArray();

    /// <summary>
    /// 追加一帧。缓冲已经装不下这一帧时复用最旧槽位（覆盖写），未满或换分辨率后按需新建。
    /// </summary>
    public PreRecordAddResult Add(Mat frame, DateTime timestamp, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);

        long bytes = GetFrameBytes(frame);
        bool resetAfterSizeChange = false;
        int previousWidth = _width;
        int previousHeight = _height;
        if (_width > 0 && _height > 0 && (_width != frame.Cols || _height != frame.Rows))
        {
            ClearCore();
            resetAfterSizeChange = true;
        }

        _width = frame.Cols;
        _height = frame.Rows;

        bool reused = false;
        if (maxBytes > 0 && _bytes + bytes > maxBytes && _slots.First != null)
        {
            Slot oldest = _slots.First.Value;
            _slots.RemoveFirst();
            _bytes -= oldest.Bytes;
            DroppedFrames++;
            HasWrapped = true;

            if (oldest.Frame.Rows == frame.Rows && oldest.Frame.Cols == frame.Cols
                && oldest.Frame.Type() == frame.Type())
            {
                // 覆盖写：最旧那一格直接接住新帧，省掉这一次整块分配
                frame.CopyTo(oldest.Frame);
                oldest.Timestamp = timestamp;
                oldest.Sequence = ++_sequence;
                _slots.AddLast(oldest);
                _bytes += oldest.Bytes;
                reused = true;
            }
            else
            {
                oldest.Frame.Dispose();
            }
        }

        if (!reused)
        {
            _slots.AddLast(new Slot
            {
                Frame = frame.Clone(),
                Bytes = bytes,
                Timestamp = timestamp,
                Sequence = ++_sequence
            });
            _bytes += bytes;
        }

        if (DisplayCapacityFrames <= 0 && bytes > 0 && maxBytes > 0)
            DisplayCapacityFrames = Math.Max(1, (int)Math.Min(int.MaxValue, maxBytes / bytes));

        TrimTo(maxBytes);
        return new PreRecordAddResult(reused, resetAfterSizeChange, previousWidth, previousHeight);
    }

    /// <summary>
    /// 取走 eventTime 及之前的所有帧，Mat 的所有权一并交给调用方（录像队列），返回取走的字节数。
    /// </summary>
    public long TakeUntil(
        DateTime eventTime,
        List<Mat> frames,
        List<DateTime> timestamps,
        out DateTime? firstTimestamp)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(timestamps);

        long takenBytes = 0;
        firstTimestamp = null;
        LinkedListNode<Slot>? node = _slots.First;
        while (node != null)
        {
            LinkedListNode<Slot>? next = node.Next;
            Slot slot = node.Value;
            if (slot.Timestamp <= eventTime)
            {
                _slots.Remove(node);
                _bytes -= slot.Bytes;
                frames.Add(slot.Frame);
                timestamps.Add(slot.Timestamp);
                firstTimestamp ??= slot.Timestamp;
                takenBytes += slot.Bytes;
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
            Slot oldest = _slots.First.Value;
            _slots.RemoveFirst();
            _bytes -= oldest.Bytes;
            oldest.Frame.Dispose();
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

    private void ClearCore()
    {
        foreach (Slot slot in _slots)
            slot.Frame.Dispose();
        _slots.Clear();
        _bytes = 0;
        _width = 0;
        _height = 0;
        DroppedFrames = 0;
        HasWrapped = false;
    }

    private static long GetFrameBytes(Mat frame) =>
        (long)frame.Rows * frame.Cols * Math.Max(1, frame.ElemSize());
}
