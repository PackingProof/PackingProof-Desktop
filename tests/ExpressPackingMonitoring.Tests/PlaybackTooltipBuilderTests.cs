using System.Globalization;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.UI;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放列表悬浮提示的字段规则：一行一个字段（字段名 + 值），顺序固定，
/// 空字段整行省略 —— 否则会出现一堆"买家留言："这样的空标签。
/// </summary>
public sealed class PlaybackTooltipBuilderTests : IDisposable
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-Hans");
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    public PlaybackTooltipBuilderTests()
    {
        CultureInfo.CurrentUICulture = Chinese;
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _originalUiCulture;
    }

    private static string L(string key) => AppLanguage.Get(key);

    private static VideoItem CreateItem() => new()
    {
        OrderId = "JD0123456789",
        Mode = "发货",
        FullPath = @"D:\videos\a.mp4",
        FileName = "a.mp4"
    };

    private static string Flat(VideoItem item) =>
        string.Join(
            "\n",
            PlaybackTooltipBuilder.BuildRows(item).Select(row => $"{row.Label}：{row.Value}"));

    private static string ValueOf(VideoItem item, string label) =>
        PlaybackTooltipBuilder.BuildRows(item)
            .FirstOrDefault(row => row.Label == label)
            ?.Value ?? "";

    [Fact]
    public void EmptyFieldsAreOmittedEntirely()
    {
        string tooltip = Flat(CreateItem());

        Assert.DoesNotContain(L("买家留言"), tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(L("卖家备注"), tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(L("原始订单号"), tooltip, StringComparison.Ordinal);
        Assert.Contains($"{L("订单号")}：JD0123456789", tooltip, StringComparison.Ordinal);
    }

    /// <summary>只有空白的字段同样算空，不能只判 null。</summary>
    [Fact]
    public void WhitespaceOnlyFieldsAreOmitted()
    {
        VideoItem item = CreateItem();
        item.BuyerMessage = "   ";

        Assert.DoesNotContain(L("买家留言"), Flat(item), StringComparison.Ordinal);
    }

    [Fact]
    public void PopulatedFieldsAppearWithLabels()
    {
        VideoItem item = CreateItem();
        item.TrackingNumber = "SF0001";
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.SellerMemo = "易碎";

        string tooltip = Flat(item);

        Assert.Contains($"{L("快递单号")}：SF0001", tooltip, StringComparison.Ordinal);
        Assert.Contains($"{L("原始订单号")}：TB-9", tooltip, StringComparison.Ordinal);
        Assert.Contains($"{L("买家留言")}：尽快发货", tooltip, StringComparison.Ordinal);
        Assert.Contains($"{L("卖家备注")}：易碎", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// 顺序固定：身份 → 订单附带信息 → 录像属性 → 状态与位置。
    /// 店员每次都在同一位置找同一项，顺序乱了提示就没法扫读。
    /// </summary>
    [Fact]
    public void RowsAreGroupedIntoFourSections()
    {
        VideoItem item = CreateItem();
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.Duration = "12s";

        IReadOnlyList<PlaybackTooltipGroup> groups = PlaybackTooltipBuilder.BuildGroups(item);

        Assert.Equal(4, groups.Count);
        Assert.Equal(L("发退货"), groups[0].Rows[0].Label);
        Assert.Equal(L("原始订单号"), groups[0].Rows[^1].Label);
        Assert.Equal(L("买家留言"), groups[1].Rows[0].Label);
        Assert.Equal(L("时长"), groups[2].Rows[0].Label);
        Assert.Equal(L("文件位置"), groups[3].Rows[^1].Label);
    }

    /// <summary>整组都空时这一组不生成：普通录像只剩"身份 + 文件位置"两组，不画多余的分隔线。</summary>
    [Fact]
    public void EmptyGroupsAreDropped()
    {
        VideoItem item = CreateItem();

        IReadOnlyList<PlaybackTooltipGroup> groups = PlaybackTooltipBuilder.BuildGroups(item);

        Assert.Equal(2, groups.Count);
        Assert.Equal(L("发退货"), groups[0].Rows[0].Label);
        Assert.Equal(L("文件位置"), groups[1].Rows[0].Label);
        Assert.All(groups, group => Assert.NotEmpty(group.Rows));
    }

    [Fact]
    public void RowsAppearInFixedOrder()
    {
        VideoItem item = CreateItem();
        item.SourceOrderId = "TB-9";
        item.BuyerMessage = "尽快发货";
        item.Duration = "12s";

        string flat = Flat(item);

        int mode = flat.IndexOf(L("发退货"), StringComparison.Ordinal);
        int source = flat.IndexOf(L("原始订单号"), StringComparison.Ordinal);
        int buyer = flat.IndexOf(L("买家留言"), StringComparison.Ordinal);
        int duration = flat.IndexOf(L("时长"), StringComparison.Ordinal);
        int path = flat.IndexOf(L("文件位置"), StringComparison.Ordinal);

        Assert.True(mode < source, "发退货应在原始订单号之前");
        Assert.True(source < buyer, "原始订单号应在买家留言之前");
        Assert.True(buyer < duration, "买家留言应在时长之前");
        Assert.True(duration < path, "时长应在文件位置之前");
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

        Assert.Equal(AppLanguage.Format("已清理（{0}）", "磁盘清理"), ValueOf(item, L("状态")));
        Assert.DoesNotContain(L("文件已丢失，未在记录的位置找到录像"), Flat(item), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRecording_ShowsMissingState()
    {
        VideoItem item = CreateItem();
        item.IsMissing = true;

        Assert.Contains(L("文件已丢失，未在记录的位置找到录像"), ValueOf(item, L("状态")), StringComparison.Ordinal);
        Assert.DoesNotContain(L("已清理"), Flat(item), StringComparison.Ordinal);
    }

    [Fact]
    public void StoredOnHostRecording_ShowsHostState()
    {
        VideoItem item = CreateItem();
        item.IsStoredOnHost = true;

        Assert.Equal(L("已备份到主机"), ValueOf(item, L("状态")));
    }

    /// <summary>正常录像不该冒出空的状态行，只留文件位置。</summary>
    [Fact]
    public void HealthyRecordingHasNoStatusRow()
    {
        VideoItem item = CreateItem();

        Assert.Equal("", ValueOf(item, L("状态")));
        Assert.Contains(@"D:\videos\a.mp4", ValueOf(item, L("文件位置")), StringComparison.Ordinal);
    }

    [Fact]
    public void LabelsAndKnownValuesFollowCurrentLanguage()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            VideoItem item = CreateItem();
            item.TrackingNumber = "SF0001";
            item.IsStoredOnHost = true;

            IReadOnlyList<PlaybackTooltipRow> rows = PlaybackTooltipBuilder.BuildRows(item);

            Assert.Equal("Tracking number", rows.First(row => row.Value == "SF0001").Label);
            Assert.Equal("Shipping", rows.First(row => row.Label == "Shipping / Return").Value);
            Assert.Equal("Backed up to host", rows.First(row => row.Value == "Backed up to host").Value);
            Assert.Contains(rows, row => row.Label == "File location");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void StopAndCleanupReasonsFollowCurrentLanguage()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            VideoItem item = CreateItem();
            item.StopReason = "扫码切换（音频异常）";
            item.IsDeleted = true;
            item.DeleteReason = "录像过短，少于 3 秒";

            Assert.Equal(
                "Switched by scan (audio error)",
                ValueOf(item, "Stop reason"));
            Assert.Equal(
                "Cleaned up (Recording too short; under 3 s)",
                ValueOf(item, "Status"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void TooltipLabelsUseLocalizedKeys()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "PlaybackTooltipBuilder.cs"));
        string[] rawLabels = Regex.Matches(
                source,
                "AddRow\\([^,]+,\\s*\"[^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*\"")
            .Select(match => match.Value)
            .ToArray();

        Assert.True(
            rawLabels.Length == 0,
            "回放悬浮提示的字段名必须通过 AppLanguage 资源生成: " + string.Join(" | ", rawLabels));
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        foreach (string startPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(startPath);
            while (directory != null)
            {
                string candidate = Path.Combine([directory.FullName, .. relativeParts]);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Unable to locate {Path.Combine(relativeParts)}");
    }
}
