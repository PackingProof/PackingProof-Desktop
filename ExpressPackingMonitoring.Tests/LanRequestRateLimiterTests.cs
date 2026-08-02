using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LanRequestRateLimiterTests
{
    [Fact]
    public void SingleAddressCannotOccupyMoreThanEightRequests()
    {
        var limiter = new LanRequestRateLimiter();
        var leases = new List<IDisposable>();
        try
        {
            for (int index = 0; index < 8; index++)
            {
                Assert.True(limiter.TryEnter(
                    "192.168.1.20",
                    LanRequestCategory.General,
                    out IDisposable? lease,
                    out _));
                leases.Add(Assert.IsAssignableFrom<IDisposable>(lease));
            }

            Assert.False(limiter.TryEnter(
                "192.168.1.20",
                LanRequestCategory.General,
                out _,
                out int retryAfterSeconds));
            Assert.Equal(2, retryAfterSeconds);
            Assert.True(limiter.TryEnter(
                "192.168.1.21",
                LanRequestCategory.General,
                out IDisposable? otherClient,
                out _));
            otherClient?.Dispose();
        }
        finally
        {
            foreach (IDisposable lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public void EnrollmentUsesBoundedMinuteWindow()
    {
        var limiter = new LanRequestRateLimiter();
        DateTimeOffset start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < 24; index++)
        {
            Assert.True(limiter.TryEnter(
                "192.168.1.20",
                LanRequestCategory.Enrollment,
                out IDisposable? lease,
                out _,
                start.AddSeconds(index)));
            lease?.Dispose();
        }

        Assert.False(limiter.TryEnter(
            "192.168.1.20",
            LanRequestCategory.Enrollment,
            out _,
            out int retryAfterSeconds,
            start.AddSeconds(25)));
        Assert.InRange(retryAfterSeconds, 34, 35);

        Assert.True(limiter.TryEnter(
            "192.168.1.20",
            LanRequestCategory.Enrollment,
            out IDisposable? renewed,
            out _,
            start.AddMinutes(1)));
        renewed?.Dispose();
    }

    [Fact]
    public void ThumbnailBurstAllowsNormalBrowserConcurrencyWithoutRelaxingMediaStreams()
    {
        var limiter = new LanRequestRateLimiter();
        var thumbnailLeases = new List<IDisposable>();
        try
        {
            for (int index = 0; index < 8; index++)
            {
                Assert.True(limiter.TryEnter(
                    "192.168.1.20",
                    LanRequestCategory.Thumbnail,
                    out IDisposable? lease,
                    out _));
                thumbnailLeases.Add(Assert.IsAssignableFrom<IDisposable>(lease));
            }

            Assert.False(limiter.TryEnter(
                "192.168.1.20",
                LanRequestCategory.Thumbnail,
                out _,
                out int retryAfterSeconds));
            Assert.Equal(2, retryAfterSeconds);
        }
        finally
        {
            foreach (IDisposable lease in thumbnailLeases)
                lease.Dispose();
        }

        var mediaLeases = new List<IDisposable>();
        try
        {
            for (int index = 0; index < 4; index++)
            {
                Assert.True(limiter.TryEnter(
                    "192.168.1.21",
                    LanRequestCategory.MediaStream,
                    out IDisposable? lease,
                    out _));
                mediaLeases.Add(Assert.IsAssignableFrom<IDisposable>(lease));
            }

            Assert.False(limiter.TryEnter(
                "192.168.1.21",
                LanRequestCategory.MediaStream,
                out _,
                out _));
        }
        finally
        {
            foreach (IDisposable lease in mediaLeases)
                lease.Dispose();
        }
    }

    [Theory]
    [InlineData("POST", "/api/mobile-backup/enroll", 1)]
    [InlineData("POST", "/api/connections/heartbeat", 2)]
    [InlineData("PUT", "/api/mobile-backup/uploads/abc/chunks", 3)]
    [InlineData("GET", "/api/videos/12/play", 4)]
    [InlineData("GET", "/api/videos/12/thumbnail", 6)]
    [InlineData("POST", "/api/videos/12/clip/preview", 5)]
    [InlineData("GET", "/api/videos", 0)]
    public void RequestsAreClassifiedByCost(string method, string path, int expected)
    {
        Assert.Equal((LanRequestCategory)expected, WebServer.ClassifyRequest(method, path));
    }
}
