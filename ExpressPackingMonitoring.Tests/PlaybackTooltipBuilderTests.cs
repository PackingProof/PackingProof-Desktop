using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放列表悬浮提示的字段规则。
/// 提示里空字段必须整行省略，否则会出现一堆"买家留言："这样的空标签。
/// </summary>
public sealed class PlaybackTooltipBuilderTests
{
    private static VideoItem CreateItem() => new()
    {
        OrderId = "JD0123456789",
        Mode = "发货",
        FullPath = @"D:\videos\a.mp4",
        FileName = "a.mp4"
    };

    [Fact]
    public void EmptyFieldsAreOmittedEntirely()
    {
        string tooltip = PlaybackTooltipBuilder.Build(CreateItem());

        Assert.DoesNotContain("买家留言", tooltip);
        Assert.DoesNotContain("卖家备注", tooltip);
        Assert.DoesNotContain("原始订单号", tooltip);
        Assert.Contains("订单号：JD0123456789", tooltip);
    }

    /// <summary>只有空白的字段同样算空，不能只判 null。</summary>
    [Fact]
    public void WhitespaceOnlyFieldsAreOmitted()
    {
        VideoItem item = CreateItem();
        item.BuyerMessage = "   ";

        Assert.DoesNotContain("买家留言", PlaybackTooltipBuilder.Build(item));
    }

    [Fact]
    public void PopulatedFieldsAppearWithLabels()
    {
        VideoItem item = CreateItem();
        item.TrackingNumber = "SF0001";
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.SellerMemo = "易碎";

        string tooltip = PlaybackTooltipBuilder.Build(item);

        Assert.Contains("快递单号：SF0001", tooltip);
        Assert.Contains("原始订单号：TB-9", tooltip);
        Assert.Contains("买家留言：尽快发货", tooltip);
        Assert.Contains("卖家备注：易碎", tooltip);
    }

    /// <summary>数据库里历史值有中英混用，提示里必须统一成中文。</summary>
    [Theory]
    [InlineData("return", "退货")]
    [InlineData("shipping", "发货")]
    [InlineData("退货", "退货")]
    [InlineData("", "发货")]
    [InlineData(null, "发货")]
    public void ModeTextIsNormalizedToChinese(string? mode, string expected)
    {
        Assert.Equal(expected, PlaybackTooltipBuilder.NormalizeModeText(mode));
    }

    [Fact]
    public void DeletedRecording_ShowsCleanupReason()
    {
        VideoItem item = CreateItem();
        item.IsDeleted = true;
        item.DeleteReason = "磁盘清理";

        string tooltip = PlaybackTooltipBuilder.Build(item);

        Assert.Contains("清理原因：磁盘清理", tooltip);
        Assert.DoesNotContain("丢失原因", tooltip);
    }

    [Fact]
    public void MissingRecording_ShowsMissingReason()
    {
        VideoItem item = CreateItem();
        item.IsMissing = true;

        string tooltip = PlaybackTooltipBuilder.Build(item);

        Assert.Contains("丢失原因", tooltip);
        Assert.DoesNotContain("清理原因", tooltip);
    }

    /// <summary>复制单号优先取快递单号，与列表显示口径一致。</summary>
    [Fact]
    public void CopyableOrderId_PrefersTrackingNumber()
    {
        VideoItem item = CreateItem();
        item.TrackingNumber = "SF0001";

        Assert.Equal("SF0001", item.CopyableOrderId);
    }

    [Fact]
    public void CopyableOrderId_FallsBackToOrderId()
    {
        Assert.Equal("JD0123456789", CreateItem().CopyableOrderId);
    }

    [Fact]
    public void CopyableOrderId_EmptyWhenNothingRecorded()
    {
        Assert.Equal("", new VideoItem().CopyableOrderId);
    }

    /// <summary>已清理、丢失或只存在于主机的录像都无法在本机资源管理器里定位。</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void UnavailableRecordings_CannotBeLocated(bool deleted, bool missing, bool storedOnHost)
    {
        VideoItem item = CreateItem();
        item.IsDeleted = deleted;
        item.IsMissing = missing;
        item.IsStoredOnHost = storedOnHost;

        Assert.False(item.CanLocateFile);
    }

    [Fact]
    public void AvailableLocalRecording_CanBeLocated()
    {
        Assert.True(CreateItem().CanLocateFile);
    }

    [Fact]
    public void RecordingWithoutPath_CannotBeLocated()
    {
        VideoItem item = CreateItem();
        item.FullPath = "";

        Assert.False(item.CanLocateFile);
    }
}
