using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LatestPreviewFrameSlotTests
{
    [Fact]
    public void BusyUiReceivesNewestFrameAndReleasesSupersededFrames()
    {
        var slot = new LatestPreviewFrameSlot<Frame>();
        slot.Reset(1);
        var first = new Frame();
        var latest = new Frame();
        slot.Publish(1, first, 10);
        slot.Publish(1, latest, 20);
        Assert.Equal(1, first.DisposeCount);
        Assert.Same(latest, slot.Take(1, out long ticks));
        Assert.Equal(20, ticks);
        Assert.Null(slot.Take(1, out _));
        Assert.Equal(0, latest.DisposeCount);
        latest.Dispose();
    }

    [Fact]
    public void RestartRejectsOldProducerAndOldUiCallback()
    {
        var slot = new LatestPreviewFrameSlot<Frame>();
        slot.Reset(1);
        var old = new Frame();
        slot.Publish(1, old, 10);
        slot.Reset(2);
        var current = new Frame();
        slot.Publish(2, current, 30);
        slot.Reset(1);
        Assert.True(slot.HasFrame(2));
        var late = new Frame();
        slot.Publish(1, late, 20);
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(1, late.DisposeCount);
        Assert.Null(slot.Take(1, out _));
        Assert.Same(current, slot.Take(2, out long ticks));
        Assert.False(slot.HasFrame(2));
        Assert.Equal(30, ticks);
        current.Dispose();
    }

    [Fact]
    public void ResetDoesNotDisposeFrameAlreadyOwnedByUi()
    {
        var slot = new LatestPreviewFrameSlot<Frame>();
        slot.Reset(1);
        var frame = new Frame();
        slot.Publish(1, frame, 1);
        Assert.Same(frame, slot.Take(1, out _));
        slot.Reset(2);
        Assert.Equal(0, frame.DisposeCount);
        frame.Dispose();
    }

    [Fact]
    public void ConcurrentPublishTakeAndResetReleaseEachFrameExactlyOnce()
    {
        var slot = new LatestPreviewFrameSlot<Frame>();
        slot.Reset(1);
        var frames = Enumerable.Range(0, 1000).Select(_ => new Frame()).ToArray();
        Parallel.Invoke(
            () => Parallel.ForEach(frames, frame => slot.Publish(1, frame, 1)),
            () => { for (int i = 0; i < 1000; i++) slot.Take(1, out _)?.Dispose(); },
            () => slot.Reset(2));
        slot.Reset(3);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    private sealed class Frame : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}
