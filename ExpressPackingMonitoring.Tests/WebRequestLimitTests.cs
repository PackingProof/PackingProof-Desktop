using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebRequestLimitTests
{
    [Fact]
    public void ListenerTimeouts_BoundSlowHeadersBodiesAndIdleConnections()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), WebServer.RequestHeaderWaitTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), WebServer.RequestEntityBodyTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), WebServer.IdleConnectionTimeout);
    }

    [Theory]
    [InlineData("secret-key", "secret-key", true)]
    [InlineData("secret-key", "SECRET-KEY", false)]
    [InlineData("", "secret-key", false)]
    public void AccessKeysEqual_UsesExactComparison(string left, string right, bool expected)
    {
        Assert.Equal(expected, WebServer.AccessKeysEqual(left, right));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void ShouldServeClipInline_OnlyAcceptsExplicitFlag(string? value, bool expected)
    {
        Assert.Equal(expected, WebServer.ShouldServeClipInline(value));
    }

    [Theory]
    [InlineData("AppleCoreMedia/1.0.0.21A329 (iPhone; U; CPU OS 18_3 like Mac OS X; zh_cn)", true)]
    [InlineData("AppleCoreMedia/1.0 (Macintosh; U; Intel Mac OS X)", true)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15", true)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1", true)]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1", true)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", false)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsApplePlaybackClientUserAgent_OnlyRecognizesAppleClients(string? userAgent, bool expected)
    {
        Assert.Equal(expected, WebServer.IsApplePlaybackClientUserAgent(userAgent));
    }

    [Theory]
    [InlineData("h265", true)]
    [InlineData("hevc", true)]
    [InlineData("h264", false)]
    [InlineData("av1", false)]
    [InlineData("", false)]
    public void IsHevcVideoCodec_OnlyAcceptsHevcAliases(string codec, bool expected)
    {
        Assert.Equal(expected, WebServer.IsHevcVideoCodec(codec));
    }

    [Theory]
    [InlineData("h264", true, false)]
    [InlineData("avc", true, false)]
    [InlineData("h265", true, true)]
    [InlineData("av1", true, true)]
    [InlineData("", true, true)]
    [InlineData("vp9", true, true)]
    [InlineData("h265", false, false)]
    [InlineData("", false, false)]
    public void ShouldTranscodeForPlayback_UsesCompatibilityModeAndTreatsUnknownAsIncompatible(
        string codec,
        bool compatMode,
        bool expected)
    {
        Assert.Equal(expected, WebServer.ShouldTranscodeForPlayback(codec, compatMode));
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=900-", 1000, 900, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=0-9999", 1000, 0, 999)]
    public void TryResolveByteRange_AcceptsSingleValidRange(
        string header,
        long fileLength,
        long expectedStart,
        long expectedEnd)
    {
        Assert.True(WebServer.TryResolveByteRange(header, fileLength, out long start, out long end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=20-10", 1000)]
    [InlineData("bytes=0-1,3-4", 1000)]
    [InlineData("items=0-10", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=0-0", 0)]
    public void TryResolveByteRange_RejectsMalformedOrUnsatisfiedRange(string header, long fileLength)
    {
        Assert.False(WebServer.TryResolveByteRange(header, fileLength, out _, out _));
    }

    [Fact]
    public void ValidateOrderInfoItems_AcceptsBoundarySizedBatch()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems)
            .Select(index => new OrderInfo
            {
                TrackingNumber = $"TRACK-{index}",
                BuyerMessage = new string('买', 2000),
                SellerMemo = new string('卖', 2000),
                ProductInfo = new string('商', 4000)
            })
            .ToList();

        WebServer.ValidateOrderInfoItems(items);
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsTooManyOrders()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems + 1)
            .Select(index => new OrderInfo { TrackingNumber = index.ToString() })
            .ToList();

        Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsOversizedField()
    {
        var items = new List<OrderInfo>
        {
            new() { TrackingNumber = "TRACK-1", BuyerMessage = new string('x', 2001) }
        };

        var error = Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));

        Assert.Contains("买家留言过长", error.Message);
    }

    [Fact]
    public void ValidateOrderInfoItems_AcceptsExtensionCounts()
    {
        WebServer.ValidateOrderInfoItems(new List<OrderInfo>
        {
            new()
            {
                TrackingNumber = "TRACK-1",
                TotalItemCount = 12,
                MergedOrderCount = 3,
                ProviderId = "scale.example"
            }
        });
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100001, 0)]
    [InlineData(0, 201)]
    public void ValidateOrderInfoItems_RejectsInvalidExtensionCounts(int totalItemCount, int mergedOrderCount)
    {
        Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(new List<OrderInfo>
        {
            new()
            {
                TrackingNumber = "TRACK-1",
                TotalItemCount = totalItemCount,
                MergedOrderCount = mergedOrderCount
            }
        }));
    }
}
