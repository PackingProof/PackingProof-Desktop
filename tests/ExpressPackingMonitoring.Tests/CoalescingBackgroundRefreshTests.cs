using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CoalescingBackgroundRefreshTests
{
    [Fact]
    public async Task RepeatedRequestsRunSeriallyAndKeepTheLatest()
    {
        using var refresh = new CoalescingBackgroundRefresh();
        var firstStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        long started = Environment.TickCount64;
        refresh.Request("first", async () =>
        {
            Interlocked.Increment(ref runs);
            firstStarted.TrySetResult(true);
            await releaseFirst.Task;
        });
        Assert.True(Environment.TickCount64 - started < 500);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        refresh.Request("second", () =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        refresh.Request("latest", () =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });

        releaseFirst.TrySetResult(true);
        await refresh.WaitForIdleAsync()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref runs));
    }
}
