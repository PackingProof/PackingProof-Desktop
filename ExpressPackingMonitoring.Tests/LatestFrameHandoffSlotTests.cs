using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LatestFrameHandoffSlotTests
{
    [Fact]
    public void PublishKeepsOnlyNewestFrameAndReleasesReplacedOne()
    {
        var slot = new LatestFrameHandoffSlot<Frame>();
        var first = new Frame();
        var latest = new Frame();

        slot.Publish(first);
        slot.Publish(latest);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, latest.DisposeCount);
        Assert.Same(latest, slot.Take());
        Assert.Null(slot.Take());
    }

    [Fact]
    public void TakenFrameIsOwnedByCallerAndNotDisposedByLaterPublish()
    {
        var slot = new LatestFrameHandoffSlot<Frame>();
        var frame = new Frame();
        slot.Publish(frame);

        Frame? taken = slot.Take();

        Assert.Same(frame, taken);
        var next = new Frame();
        slot.Publish(next);
        Assert.Equal(0, frame.DisposeCount);

        taken!.Dispose();
        Assert.Equal(1, frame.DisposeCount);
        slot.Clear();
        Assert.Equal(1, next.DisposeCount);
    }

    [Fact]
    public void ClearReleasesPendingFrameAndIsIdempotent()
    {
        var slot = new LatestFrameHandoffSlot<Frame>();
        var frame = new Frame();
        slot.Publish(frame);

        slot.Clear();
        slot.Clear();

        Assert.Equal(1, frame.DisposeCount);
        Assert.Null(slot.Take());
    }

    [Fact]
    public void ConcurrentPublishTakeAndClearReleaseEachFrameExactlyOnce()
    {
        var slot = new LatestFrameHandoffSlot<Frame>();
        var frames = Enumerable.Range(0, 1000).Select(_ => new Frame()).ToArray();

        Parallel.Invoke(
            () => Parallel.ForEach(frames, frame => slot.Publish(frame)),
            () => { for (int i = 0; i < 1000; i++) slot.Take()?.Dispose(); },
            () => { for (int i = 0; i < 1000; i++) slot.Clear(); });
        slot.Clear();

        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    private sealed class Frame : IDisposable
    {
        public int DisposeCount;

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}
