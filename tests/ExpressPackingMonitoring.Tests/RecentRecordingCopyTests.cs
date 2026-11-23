using System.Text;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 「本机录制动态」复制单号的接线约束：单击单号和右键菜单两条入口都要有，
/// 而且必须走同一个处理器 —— XAML 里少写一处只会表现为"点了没反应"，编译期看不出来。
/// </summary>
public sealed class RecentRecordingCopyTests
{
    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    [Fact]
    public void RecentList_OffersBothClickAndContextMenuCopy()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        // 右键菜单是窗口级的一个实例，行模板里引用它
        Assert.Contains("x:Key=\"RecentRecordingContextMenu\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{loc:Localized 复制单号}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenu=\"{StaticResource RecentRecordingContextMenu}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("MouseLeftButtonUp=\"CopyRecentOrderId_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentListCopy_ClickAndMenuShareOneHandler()
    {
        string codeBehind = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("private void CopyRecentOrderId_Click(", codeBehind, StringComparison.Ordinal);
        // 单击、悬浮按钮与右键菜单项共用同一个处理器。
        int handlerReferences = xaml.Split("CopyRecentOrderId_Click").Length - 1;
        Assert.Equal(3, handlerReferences);
        Assert.Contains("ClipboardHelper.TryCopyText", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AppLanguage.Get(\"单号已复制\")", codeBehind, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(7.9, "7s")]
    [InlineData(59.9, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(125.9, "2m 5s")]
    public void RecentDuration_UsesWallClockDuration(double seconds, string expected)
    {
        Assert.Equal(expected, ScanRecord.FormatDuration(seconds));
    }

    [Fact]
    public void RecentRecord_CanPlayOnlyAfterRecordingStops()
    {
        var record = new ScanRecord(
            "SF-PLAY",
            "2s",
            "10:00:00",
            "发货",
            isActive: true,
            videoRecordId: 9,
            generatedName: "MAN_20261123_100000");
        var changed = new List<string>();
        record.PropertyChanged += (_, args) =>
            changed.Add(args.PropertyName ?? "");

        Assert.False(record.CanPlay);
        record.IsActive = false;

        Assert.True(record.CanPlay);
        Assert.Contains(nameof(ScanRecord.CanPlay), changed);
        Assert.True(record.CanCopyOrderNumber);

        record.OrderId = "";
        Assert.True(record.CanCopyOrderNumber);
        Assert.Equal("MAN_20261123_100000", record.CopyOrderNumberText);
        Assert.Contains(nameof(ScanRecord.CanCopyOrderNumber), changed);

        record.GeneratedName = "";
        Assert.False(record.CanCopyOrderNumber);

        record.SaveStatusText = "已保存";
        Assert.True(record.HasSaveStatus);
        Assert.Contains(nameof(ScanRecord.HasSaveStatus), changed);
    }

    [Fact]
    public void RecentList_OffersHoverPlaybackEntry()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"PlayRecentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CopyRecentOrderButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecentRowActions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Separator Style=\"{StaticResource AppSeparatorStyle}\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PlayRecentRecording_Click\"", xaml, StringComparison.Ordinal);
        int playHandlerReferences = xaml.Split("PlayRecentRecording_Click").Length - 1;
        Assert.Equal(2, playHandlerReferences);
        Assert.Contains(
            "IsEnabled=\"{Binding CanPlay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding CanCopyOrderNumber}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"34\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"34\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SavedStatusPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SaveStatusText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"{StaticResource FluentHourglassIcon}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DurationPanel\"", xaml, StringComparison.Ordinal);
        int activeDurationTrigger = xaml.IndexOf(
            "<DataTrigger Binding=\"{Binding IsActive}\" Value=\"True\">",
            StringComparison.Ordinal);
        int rightAlignedDuration = xaml.IndexOf(
            "<Setter Property=\"HorizontalAlignment\" Value=\"Right\"/>",
            activeDurationTrigger,
            StringComparison.Ordinal);
        Assert.InRange(rightAlignedDuration, activeDurationTrigger, activeDurationTrigger + 250);
        Assert.Contains("Margin=\"0,0,6,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"6,0,0,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<Setter TargetName=\"BadgeBorder\" Property=\"Visibility\" Value=\"Hidden\"/>",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter TargetName=\"SavedStatusPanel\" Property=\"Visibility\" Value=\"Hidden\"/>",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Binding=\"{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=ListViewItem}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"FluentHistoryIcon\"", ReadProjectFile(
            Path.Combine("Themes", "FluentIcons.xaml")), StringComparison.Ordinal);
        Assert.Contains(
            "Data=\"{StaticResource FluentHistoryIcon}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=\"{loc:Localized 播放录像}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"52\"/>", xaml, StringComparison.Ordinal);
        string recentPlayback = ReadProjectFile(
            Path.Combine("UI", "PlaybackWindow.RecentPlayback.cs"));
        Assert.DoesNotContain("_mediaKind = \"video\";", recentPlayback, StringComparison.Ordinal);
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
