using ExpressPackingMonitoring.Services.MediaFoundation;
using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 事件预录环形缓冲：装满之后必须复用最旧那一格（省掉每帧一次整块分配），
/// 并且原始采样（NV12/YUY2）只按 1.5 字节/像素存，转换留给写入端。
/// 1080p 一帧整块分配约 1.5 ms、2K 约 2 ms（见 Tools/Diagnose-CameraPipeline.ps1 第 6 节）。
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

        Mat[] before = BgrSlots(ring);
        Assert.Equal(3, before.Length);

        using Mat newest = CreateFrame(8, 6, value: 9);
        PreRecordAddResult result = ring.Add(newest, BaseTime.AddSeconds(3), maxBytes);

        Assert.True(result.ReusedSlot);
        Assert.Equal(3, ring.Count);
        Assert.Equal(maxBytes, ring.Bytes);
        Assert.Equal(1, ring.DroppedFrames);
        Assert.True(ring.HasWrapped);

        // 最旧那一格被搬到队尾继续服役：实例没换，只覆盖写了新内容
        Mat[] after = BgrSlots(ring);
        Assert.Same(before[0], after[2]);
        Assert.Same(before[1], after[0]);
        Assert.Same(before[2], after[1]);
        Assert.Equal(9, after[2].At<byte>(0, 0));
        Assert.Equal(1, after[1].At<byte>(0, 0));
    }

    [Fact]
    public void AddRaw_WhenFull_ReusesRawSlotInPlace()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 2;
        using Mat nv12 = CreateNv12(width: 8, height: 6, padding: 0);
        CameraRawFrame raw = DescribeNv12(nv12, width: 8, height: 6);
        for (int index = 0; index < 2; index++)
            ring.AddRaw(raw, BaseTime.AddSeconds(index), maxBytes);

        Mat[] before = RawBuffers(ring);
        Assert.Equal(2, before.Length);

        raw = raw with { Data = IntPtr.Add(raw.Data, 0) };
        PreRecordAddResult result = ring.AddRaw(raw, BaseTime.AddSeconds(2), maxBytes);

        Assert.True(result.ReusedSlot);
        Assert.True(result.ReusedRawSlot);
        Assert.Equal(2, ring.Count);
        Assert.Equal(maxBytes, ring.Bytes);
        Mat[] after = RawBuffers(ring);
        Assert.Same(before[0], after[1]);
        Assert.Same(before[1], after[0]);
    }

    [Fact]
    public void AddRaw_KeepsCapacityInBgrUnitsButStoresHalfTheBytes()
    {
        using var ring = new PreRecordFrameRing();
        using Mat nv12 = CreateNv12(width: 32, height: 16, padding: 0);
        long bgrBytes = FrameBytes(32, 16);
        long maxBytes = bgrBytes * 4;

        for (int index = 0; index < 4; index++)
            ring.AddRaw(DescribeNv12(nv12, 32, 16), BaseTime.AddSeconds(index), maxBytes);

        // 容量口径不变：4 帧 NV12 就按 4 帧 BGR 记账（这样预录秒数不会突然翻倍）
        Assert.Equal(4, ring.Count);
        Assert.Equal(bgrBytes * 4, ring.Bytes);
        Assert.Equal(4, ring.DisplayCapacityFrames);

        // 实际内存只有一半：NV12 是 1.5 字节/像素
        long actualBytes = RawBuffers(ring).Sum(buffer => (long)buffer.Total() * buffer.ElemSize());
        Assert.Equal(bgrBytes * 4 / 2, actualBytes);
    }

    [Fact]
    public void AddRawAndBgr_DoNotReuseEachOthersSlots()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 3;
        using Mat nv12 = CreateNv12(width: 8, height: 6, padding: 0);
        using Mat frame = CreateFrame(8, 6, value: 3);

        ring.AddRaw(DescribeNv12(nv12, 8, 6), BaseTime, maxBytes);
        ring.Add(frame, BaseTime.AddSeconds(1), maxBytes);
        ring.Add(frame, BaseTime.AddSeconds(2), maxBytes);
        Assert.Equal(2, BgrSlots(ring).Length);
        Assert.Equal(1, RawBuffers(ring).Length);

        // 满环时最旧那格是原始采样，与 BGR 帧对不上 → 丢掉重建，不做错种类复用
        PreRecordAddResult bgrAdded = ring.Add(frame, BaseTime.AddSeconds(3), maxBytes);

        Assert.False(bgrAdded.ReusedRawSlot);
        Assert.Equal(3, ring.Count);
        Assert.Equal(0, RawBuffers(ring).Length);
        Assert.Equal(3, BgrSlots(ring).Length);
        Assert.Equal(1, ring.DroppedFrames);
    }

    [Fact]
    public void TakeUntil_TransfersOwnershipAndKeepsNewerFrames()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 4;
        using Mat frame = CreateFrame(8, 6, value: 2);
        for (int index = 0; index < 4; index++)
            ring.Add(frame, BaseTime.AddSeconds(index), maxBytes);

        var payloads = new List<PreRecordPayload>();
        long taken = ring.TakeUntil(BaseTime.AddSeconds(1.5), payloads, out DateTime? firstTimestamp);

        Assert.Equal(2, payloads.Count);
        Assert.Equal(FrameBytes(8, 6) * 2, taken);
        Assert.Equal(BaseTime, firstTimestamp);
        Assert.Equal(BaseTime, payloads[0].Timestamp);
        Assert.Equal(BaseTime.AddSeconds(1), payloads[1].Timestamp);
        Assert.Equal(2, ring.Count);
        Assert.Equal(FrameBytes(8, 6) * 2, ring.Bytes);

        foreach (PreRecordPayload payload in payloads)
            payload.Dispose();
    }

    [Fact]
    public void Add_AfterTakeUntil_RefillsWithFreshSlots()
    {
        using var ring = new PreRecordFrameRing();
        long maxBytes = FrameBytes(8, 6) * 2;
        using Mat frame = CreateFrame(8, 6, value: 3);
        ring.Add(frame, BaseTime, maxBytes);
        ring.Add(frame, BaseTime.AddSeconds(1), maxBytes);

        var drained = new List<PreRecordPayload>();
        ring.TakeUntil(BaseTime.AddMinutes(1), drained, out _);
        Mat[] drainedFrames = drained.Select(payload => payload.Bgr!).ToArray();
        foreach (PreRecordPayload payload in drained)
            payload.Dispose();
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Bytes);

        // 交出去的 Mat 归录像队列，环形缓冲只能重新长出来，绝不回头复用已交出去的那一块
        PreRecordAddResult refilled = ring.Add(frame, BaseTime.AddSeconds(2), maxBytes);

        Assert.False(refilled.ReusedSlot);
        Assert.Single(BgrSlots(ring));
        Assert.DoesNotContain(BgrSlots(ring)[0], drainedFrames);
    }

    [Fact]
    public void Add_WhenFrameSizeChanges_ResetsRingAndDisposesOldSlots()
    {
        using var ring = new PreRecordFrameRing();
        using Mat small = CreateFrame(8, 6, value: 4);
        using Mat large = CreateFrame(16, 12, value: 5);
        ring.Add(small, BaseTime, FrameBytes(8, 6) * 4);
        Mat[] before = BgrSlots(ring);

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

    [Fact]
    public void TrimTo_DropsOldestFramesUntilUnderLimit()
    {
        using var ring = new PreRecordFrameRing();
        using Mat frame = CreateFrame(8, 6, value: 6);
        long maxBytes = FrameBytes(8, 6) * 4;
        for (int index = 0; index < 4; index++)
            ring.Add(frame, BaseTime.AddSeconds(index), maxBytes);
        Mat[] before = BgrSlots(ring);

        int removed = ring.TrimTo(FrameBytes(8, 6));

        Assert.Equal(3, removed);
        Assert.Equal(1, ring.Count);
        Assert.Equal(FrameBytes(8, 6), ring.Bytes);
        Assert.Equal(3, ring.DroppedFrames);
        Assert.True(before[0].IsDisposed);
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
        Mat[] slots = BgrSlots(ring);
        ring.DisplayCapacityFrames = 2;

        ring.Clear();

        Assert.All(slots, slot => Assert.True(slot.IsDisposed));
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Bytes);
        Assert.Equal(0, ring.Width);
        Assert.Equal(0, ring.DroppedFrames);
        Assert.False(ring.HasWrapped);
        Assert.Equal(0, ring.DisplayCapacityFrames);
    }

    [Fact]
    public void CameraRawFrame_CopyToCompactMat_RespectsSourceStride()
    {
        // 带行距填充的 NV12：紧凑拷贝只保留可见行，不能把填充字节也算进去
        using Mat padded = CreateNv12(width: 8, height: 4, padding: 6);
        CameraRawFrame raw = DescribeNv12(padded, width: 8, height: 4);

        using Mat compact = raw.CopyToCompactMat();

        Assert.Equal(8, compact.Cols);
        Assert.Equal(6, compact.Rows);            // NV12：4 行 Y + 2 行 UV
        Assert.Equal(MatType.CV_8UC1, compact.Type());
        Assert.Equal(0, compact.At<byte>(0, 0));
        Assert.Equal(7, compact.At<byte>(0, 7));
        Assert.Equal(10, compact.At<byte>(1, 0)); // 第二行 Y：10 + column
        Assert.Equal(30, compact.At<byte>(4, 0)); // UV 行
    }

    [Fact]
    public void CameraRawFrameDecoder_DecodesNeutralNv12ToGray()
    {
        using Mat nv12 = CreateNv12(width: 16, height: 8, padding: 0, y: 76, uv: 128);
        using var decoder = new CameraRawFrameDecoder(CameraRawPixelFormat.Nv12, 16, 8);
        using var bgr = new Mat();

        decoder.DecodeToBgr(DescribeNv12(nv12, 16, 8), bgr);

        Assert.Equal(16, bgr.Cols);
        Assert.Equal(8, bgr.Rows);
        Vec3b pixel = bgr.At<Vec3b>(4, 4);
        // Y=76、UV=128 是中性灰：BT.601/709 都该落在 76 上下
        Assert.InRange(pixel.Item0, 68, 84);
        Assert.InRange(pixel.Item1, 68, 84);
        Assert.InRange(pixel.Item2, 68, 84);
    }

    private static Mat[] BgrSlots(PreRecordFrameRing ring) =>
        ring.Slots.Where(payload => !payload.IsRaw).Select(payload => payload.Bgr!).ToArray();

    private static Mat[] RawBuffers(PreRecordFrameRing ring) =>
        ring.Slots.Where(payload => payload.IsRaw).Select(payload => payload.Raw!.Buffer).ToArray();

    private static Mat CreateFrame(int width, int height, int value) =>
        new(height, width, MatType.CV_8UC3, new Scalar(value, value, value));

    /// <summary>造一块带行距填充的 NV12：Y 平面每行依次是 0..width-1，UV 平面填 30。</summary>
    private static Mat CreateNv12(int width, int height, int padding, byte y = 0, byte uv = 30)
    {
        int stride = width + padding;
        var buffer = new Mat(height + height / 2, stride, MatType.CV_8UC1);
        for (int row = 0; row < height + height / 2; row++)
        {
            for (int column = 0; column < stride; column++)
            {
                byte value = row < height
                    ? (y == 0 ? (byte)(row * 10 + column) : y)
                    : uv;
                buffer.Set(row, column, value);
            }
        }

        return buffer;
    }

    private static CameraRawFrame DescribeNv12(Mat buffer, int width, int height) =>
        new(buffer.Data, (int)buffer.Step(), width, height, CameraRawPixelFormat.Nv12, UsesBt709: false);

    private static long FrameBytes(int width, int height) =>
        (long)width * height * 3;
}
