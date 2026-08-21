using ExpressPackingMonitoring.Services;
using System.Collections;
using System.Reflection;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoLifecycleCoordinatorTests
{
    [Fact]
    public async Task CancelledWait_ReleasesReferenceAndRemovesEntry()
    {
        long recordId = DateTime.UtcNow.Ticks;
        using IDisposable first = await VideoLifecycleCoordinator.EnterAsync(
            recordId,
            CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VideoLifecycleCoordinator.EnterAsync(recordId, cancellation.Token));

        first.Dispose();

        FieldInfo entriesField = typeof(VideoLifecycleCoordinator).GetField(
            "Entries",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var entries = (IDictionary)entriesField.GetValue(null)!;
        Assert.False(entries.Contains(recordId));
    }
}
