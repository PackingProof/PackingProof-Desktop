using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 事件预录环形缓冲的槽位复用：装满之后必须复用最旧那一格，而不是每帧再分配一块整帧。
/// 1080p 一帧的整块分配约 1.5 ms、2K 约 2 ms，纯拷贝只要 0.09 / 0.7 ms，回退成每帧 Clone
/// 会把预录的常驻开销又拉回去（见 Tools/Diagnose-CameraPipeline.ps1 第 6 节）。
/// </summary>
public sealed class PreRecordFrameRingTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Add_WhenGrowing_AllocatesOneSlotPerFrame()
    {
        using var ring = new PreRecordFrameRing();
        using Mat frame = CreateFrame(8, 6, value: 1);

        PreRecordAddResult first = ring.Add(frame, BaseTime, maxBytes: FrameBytes(8, 6) * 4);
        PreRecordAddResult second = ring.Add(frame, BaseTime.AddSeconds(1), maxBytes: FrameBytes(8, 6) * 4);

        Assert.False(first.ReusedSlot);
        Assert.False(second.ReusedSlot);
        Assert.Equal(2, ring.Count);
        Assert.Equal(FrameBytes(8, 6) * 2, ring.Bytes);
        Assert.Equal(0, ring.DroppedFrames);
        Assert.False(ring.HasWrapped);
    }

    [Fact]
    public void Add_WhenFull_ReusesOldestSlotInsteadOfAllocating()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 3;
        using Mat frame = CreateFrame(8, 6, value: 1);
        for (int index = 0; index < 3; index++)
            ring.Add(frame, BaseTime.AddSeconds(index), maxBytes);

        Mat[] before = ring.SlotFrames.ToArray();
        Assert.Equal(3, before.Length);
        Assert.Equal(3, ring.Count);

        using Mat newest = CreateFrame(8, 6, value: 9);
        PreRecordAddResult result = ring.Add(newest, BaseTime.AddSeconds(3), maxBytes);

        Assert.True(result.ReusedSlot);
        Assert.Equal(3, ring.Count);
        Assert.Equal(maxBytes, ring.Bytes);
        Assert.Equal(1, ring.DroppedFrames);
        Assert.True(ring.HasWrapped);

        // 最旧那一格被搬到队尾继续服役：实例没换，只覆盖写了新内容
        Mat[] after = ring.SlotFrames.ToArray();
        Assert.Same(before[0], after[2]);
        Assert.Same(before[1], after[0]);
        Assert.Same(before[2], after[1]);
        Assert.Equal(9, after[2].At<byte>(0, 0));
        Assert.Equal(1, after[1].At<byte>(0, 0));
    }

    [Fact]
    public void TakeUntil_TransfersOwnershipAndKeepsNewerFrames()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 4;
        using Mat frame = CreateFrame(8, 6, value: 2);
        for (int index = 0; index < 4; index++)
            ring.Add(frame, BaseTime.AddSeconds(index), maxBytes);

        var frames = new List<Mat>();
        var timestamps = new List<DateTime>();
        long taken = ring.TakeUntil(BaseTime.AddSeconds(1.5), frames, timestamps, out DateTime? firstTimestamp);

        Assert.Equal(2, frames.Count);
        Assert.Equal(FrameBytes(8, 6) * 2, taken);
        Assert.Equal(BaseTime, firstTimestamp);
        Assert.Equal(new[] { BaseTime, BaseTime.AddSeconds(1) }, timestamps);
        Assert.Equal(2, ring.Count);
        Assert.Equal(FrameBytes(8, 6) * 2, ring.Bytes);

        foreach (Mat taken_frame in frames)
            taken_frame.Dispose();
    }

    [Fact]
    public void Add_AfterTakeUntil_RefillsWithFreshSlots()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 2;
        using Mat frame = CreateFrame(8, 6, value: 3);
        ring.Add(frame, BaseTime, maxBytes);
        ring.Add(frame, BaseTime.AddSeconds(1), maxBytes);

        var drained = new List<Mat>();
        ring.TakeUntil(BaseTime.AddMinutes(1), drained, new List<DateTime>(), out _);
        foreach (Mat taken in drained)
            taken.Dispose();
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Bytes);

        // 交出去的 Mat 归录像队列，环形缓冲只能重新长出来，绝不回头复用已交出去的那一块
        PreRecordAddResult refilled = ring.Add(frame, BaseTime.AddSeconds(2), maxBytes);

        Assert.False(refilled.ReusedSlot);
        Assert.Equal(1, ring.Count);
        Mat[] slots = ring.SlotFrames.ToArray();
        Assert.Single(slots);
        Assert.DoesNotContain(slots[0], drained);
    }

    [Fact]
    public void Add_WhenFrameSizeChanges_ResetsRingAndDisposesOldSlots()
    {
        using var ring = new PreRecordFrameRing();
        using Mat small = CreateFrame(8, 6, value: 4);
        using Mat large = CreateFrame(16, 12, value: 5);
        ring.Add(small, BaseTime, FrameBytes(8, 6) * 4);
        Mat[] before = ring.SlotFrames.ToArray();

        PreRecordAddResult result = ring.Add(large, BaseTime.AddSeconds(1), FrameBytes(16, 12) * 4);

        Assert.True(result.ResetAfterSizeChange);
        Assert.Equal(8, result.PreviousWidth);
        Assert.Equal(6, result.PreviousHeight);
        Assert.True(before[0].IsDisposed);
        Assert.Equal(1, ring.Count);
        Assert.Equal(16, ring.Width);
        Assert.Equal(12, ring.Height);
        Assert.Equal(0, ring.DroppedFrames);
    }

    /// <summary>
    /// 换分辨率后显示容量必须按新尺寸重算：抽取前的老代码在重建时会把容量清零重算，
    /// 漏了这一步界面会拿旧尺寸算出来的帧数继续显示进度。
    /// </summary>
    [Fact]
    public void Add_WhenFrameSizeChanges_RecomputesDisplayCapacity()
    {
        using var ring = new PreRecordFrameRing();
        using Mat small = CreateFrame(8, 6, value: 1);
        using Mat large = CreateFrame(16, 12, value: 2);
        long maxBytes = FrameBytes(16, 12) * 4;

        ring.Add(small, BaseTime, maxBytes);
        Assert.Equal(maxBytes / FrameBytes(8, 6), ring.DisplayCapacityFrames);

        ring.Add(large, BaseTime.AddSeconds(1), maxBytes);

        Assert.Equal(maxBytes / FrameBytes(16, 12), ring.DisplayCapacityFrames);
    }

    [Fact]
    public void TrimTo_DropsOldestFramesUntilUnderLimit()
    {
        using var ring = new PreRecordFrameRing();
        using Mat frame = CreateFrame(8, 6, value: 6);
        long maxBytes = FrameBytes(8, 6) * 4;
        for (int index = 0; index < 4; index++)
            ring.Add(frame, BaseTime.AddSeconds(index), maxBytes);
        Mat[] before = ring.SlotFrames.ToArray();

        int removed = ring.TrimTo(FrameBytes(8, 6));

        Assert.Equal(3, removed);
        Assert.Equal(1, ring.Count);
        Assert.Equal(FrameBytes(8, 6), ring.Bytes);
        Assert.Equal(3, ring.DroppedFrames);
        Assert.True(before[0].IsDisposed);
        Assert.True(before[1].IsDisposed);
        Assert.False(before[3].IsDisposed);
    }

    [Fact]
    public void Add_WhenSingleFrameExceedsCapacity_LeavesRingEmpty()
    {
        using var ring = new PreRecordFrameRing();
        using Mat frame = CreateFrame(8, 6, value: 7);

        ring.Add(frame, BaseTime, FrameBytes(8, 6) - 1);

        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Bytes);
        Assert.Equal(1, ring.DroppedFrames);
    }

    [Fact]
    public void Clear_DisposesEverySlotAndResetsCounters()
    {
        using var ring = new PreRecordFrameRing();
        using Mat frame = CreateFrame(8, 6, value: 8);
        long maxBytes = FrameBytes(8, 6) * 2;
        ring.Add(frame, BaseTime, maxBytes);
        ring.Add(frame, BaseTime.AddSeconds(1), maxBytes);
        ring.Add(frame, BaseTime.AddSeconds(2), maxBytes);
        Mat[] slots = ring.SlotFrames.ToArray();
        ring.DisplayCapacityFrames = 2;

        ring.Clear();

        Assert.All(slots, slot => Assert.True(slot.IsDisposed));
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Bytes);
        Assert.Equal(0, ring.Width);
        Assert.Equal(0, ring.Height);
        Assert.Equal(0, ring.DroppedFrames);
        Assert.False(ring.HasWrapped);
        Assert.Equal(0, ring.DisplayCapacityFrames);
    }

    private static Mat CreateFrame(int width, int height, int value) =>
        new(height, width, MatType.CV_8UC3, new Scalar(value, value, value));

    private static long FrameBytes(int width, int height) =>
        (long)width * height * 3;
}
