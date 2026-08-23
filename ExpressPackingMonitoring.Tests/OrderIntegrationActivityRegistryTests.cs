using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class OrderIntegrationActivityRegistryTests
{
    [Fact]
    public void RecordReceived_PersistsLatestConfirmedBusinessActivity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "epm-order-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 23, 6, 0, 0, TimeSpan.Zero));
            string path = Path.Combine(directory, "activity.json");
            var registry = new OrderIntegrationActivityRegistry(path, time);
            registry.RecordReceived("mobile-node", 3);
            time.Advance(TimeSpan.FromMinutes(2));
            registry.RecordReceived("mobile-node", 5);

            OrderIntegrationActivity activity = Assert.Single(
                new OrderIntegrationActivityRegistry(path, time).GetSnapshot()).Value;
            Assert.Equal(5, activity.ReceivedCount);
            Assert.Equal(time.GetUtcNow(), activity.LastActivityUtc);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Snapshot_RemovesActivityAfterThirtyDays()
    {
        string directory = Path.Combine(Path.GetTempPath(), "epm-order-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            var registry = new OrderIntegrationActivityRegistry(Path.Combine(directory, "activity.json"), time);
            registry.RecordReceived("pc-node", 1);
            time.Advance(TimeSpan.FromDays(31));

            Assert.Empty(registry.GetSnapshot());
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
