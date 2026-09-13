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

        Assert.Contains("状态：已清理（磁盘清理）", tooltip);
        Assert.DoesNotContain("文件已丢失", tooltip);
    }

    [Fact]
    public void MissingRecording_ShowsMissingState()
    {
        VideoItem item = CreateItem();
        item.IsMissing = true;

        string tooltip = PlaybackTooltipBuilder.Build(item);

        Assert.Contains("文件已丢失", tooltip);
        Assert.DoesNotContain("已清理", tooltip);
    }

    /// <summary>
    /// 分组顺序固定：身份 → 订单信息 → 录像属性 → 状态与位置。
    /// 店员每次都在同一位置找同一项，顺序乱了提示就没法扫读。
    /// </summary>
    [Fact]
    public void SectionsAppearInFixedOrder()
    {
        VideoItem item = CreateItem();
        item.TrackingNumber = "SF0001";
        item.BuyerMessage = "尽快发货";
        item.Duration = "12s";

        string tooltip = PlaybackTooltipBuilder.Build(item);

        int mode = tooltip.IndexOf("发退货", StringComparison.Ordinal);
        int tracking = tooltip.IndexOf("快递单号", StringComparison.Ordinal);
        int buyer = tooltip.IndexOf("买家留言", StringComparison.Ordinal);
        int duration = tooltip.IndexOf("时长", StringComparison.Ordinal);
        int path = tooltip.IndexOf("文件位置", StringComparison.Ordinal);

        Assert.True(mode < tracking, "发退货应在快递单号之前");
        Assert.True(tracking < buyer, "身份信息应在订单备注之前");
        Assert.True(buyer < duration, "订单备注应在录像属性之前");
        Assert.True(duration < path, "文件位置应排在最后");
    }

    /// <summary>整组为空时不能留下多余空行。</summary>
    [Fact]
    public void EmptySectionsDoNotLeaveBlankLines()
    {
        string tooltip = PlaybackTooltipBuilder.Build(CreateItem());

        Assert.DoesNotContain("\r\n\r\n\r\n", tooltip);
        Assert.DoesNotContain("\n\n\n", tooltip);
        Assert.Equal(tooltip.TrimEnd(), tooltip);
    }

    /// <summary>分组之间要有空行，否则十几行挤在一起没法读。</summary>
    [Fact]
    public void SectionsAreSeparatedByBlankLine()
    {
        VideoItem item = CreateItem();
        item.BuyerMessage = "尽快发货";

        string tooltip = PlaybackTooltipBuilder.Build(item);

        Assert.Contains(Environment.NewLine + Environment.NewLine, tooltip);
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
