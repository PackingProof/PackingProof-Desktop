using ExpressPackingMonitoring.UI.SettingsSearch;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 设置页搜索：匹配规则（忽略大小写、命中区间给高亮用），
/// 以及"搜索框挂在左栏顶部、事件接上了、内容区有找不到的提示"这条结构守卫。
/// </summary>
public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData("帧率", "帧率", true)]
    [InlineData("帧率", "  帧率  ", true)]
    [InlineData("分辨率 帧率", "帧率", true)]
    [InlineData("HEVC / H.265", "hevc", true)]
    [InlineData("720P - 省空间", "720p", true)]
    [InlineData("播放设备 | 麦克风", "麦克风", true)]
    [InlineData("播放设备", "麦克风", false)]
    [InlineData("", "帧率", false)]
    [InlineData("帧率", "", false)]
    public void Match_IgnoresCaseAndOuterSpaces(string text, string query, bool expected) =>
        Assert.Equal(expected, SettingsSearchMatcher.IsMatch(text, SettingsSearchMatcher.Normalize(query)));

    /// <summary>同一段文字里出现多次要都标出来，高亮才不会漏。</summary>
    [Fact]
    public void FindRanges_ReturnsEveryOccurrence()
    {
        IReadOnlyList<(int Start, int Length)> ranges =
            SettingsSearchMatcher.FindRanges("帧率 | 副画面帧率", "帧率");

        Assert.Equal(2, ranges.Count);
        Assert.Equal((0, 2), ranges[0]);
        Assert.Equal((8, 2), ranges[1]);
        Assert.Empty(SettingsSearchMatcher.FindRanges("播放设备", "帧率"));
    }

    [Fact]
    public void SettingsXaml_KeepsTheSearchBoxAtTheTopOfTheSidebar()
    {
        string xaml = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml"),
            System.Text.Encoding.UTF8);

        // 搜索框在左栏（TabControl 模板）里，且排在页签列表前面
        int searchBox = xaml.IndexOf("x:Name=\"SettingsSearchBox\"", StringComparison.Ordinal);
        int tabPanel = xaml.IndexOf("<TabPanel", StringComparison.Ordinal);
        Assert.True(searchBox > 0, "左栏顶部没有搜索框");
        Assert.True(tabPanel > searchBox, "搜索框应该排在页签列表前面");

        // 三个事件都接上了：输入即搜、Esc 清空、载入时建索引
        foreach (string handler in new[]
                 {
                     "TextChanged=\"SettingsSearchBox_TextChanged\"",
                     "PreviewKeyDown=\"SettingsSearchBox_PreviewKeyDown\"",
                     "Loaded=\"SettingsSearchBox_Loaded\"",
                 })
        {
            Assert.Contains(handler, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("x:Name=\"SettingsSearchPlaceholder\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsSearchCountText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsSearchEmptyState\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 副摄像头那张卡是 ItemsControl + DataTemplate 生成出来的，**不在逻辑树里**。
    /// 搜索只扫逻辑树时管不到它，会出现"一条都没命中，那张卡还挂在那儿"，
    /// 所以过滤必须再从视觉树补一次索引。
    /// </summary>
    [Fact]
    public void SearchIndex_AlsoCoversTemplateGeneratedCards()
    {
        string searchDirectory = Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "UI",
            "SettingsSearch");

        string index = File.ReadAllText(
            Path.Combine(searchDirectory, "SettingsSearchIndex.cs"),
            System.Text.Encoding.UTF8);
        Assert.Contains("VisualDescendants(", index, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetChildrenCount", index, StringComparison.Ordinal);
        Assert.Contains("FindRealizedCards(", index, StringComparison.Ordinal);

        string controller = File.ReadAllText(
            Path.Combine(searchDirectory, "SettingsSearchController.cs"),
            System.Text.Encoding.UTF8);
        Assert.Contains("CollectRealizedCards()", controller, StringComparison.Ordinal);
        // 页签切过去之后视觉树才建出来，得再补一次
        Assert.Contains("SelectionChanged", controller, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
