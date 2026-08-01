using System.Collections.Concurrent;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebTemporaryGrantTests
{
    [Fact]
    public void TrimExpiredAndOverflow_DoesNothingBelowHardLimit()
    {
        var entries = new ConcurrentDictionary<string, DateTimeOffset>();
        for (int index = 0; index < 4; index++)
            entries[index.ToString()] = DateTimeOffset.UtcNow.AddMinutes(-1);

        int removed = WebServer.TrimExpiredAndOverflow(entries, 4, 3, value => value);

        Assert.Equal(0, removed);
        Assert.Equal(4, entries.Count);
    }

    [Fact]
    public void TrimExpiredAndOverflow_RemovesExpiredThenReturnsToLowWaterMark()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entries = new ConcurrentDictionary<string, DateTimeOffset>();
        entries["expired"] = now.AddMinutes(-1);
        for (int index = 0; index < 5; index++)
            entries[$"valid-{index}"] = now.AddMinutes(index + 1);

        int removed = WebServer.TrimExpiredAndOverflow(entries, 5, 3, value => value);

        Assert.Equal(3, removed);
        Assert.Equal(3, entries.Count);
        Assert.DoesNotContain("expired", entries.Keys);
        Assert.Contains("valid-4", entries.Keys);
    }
}
