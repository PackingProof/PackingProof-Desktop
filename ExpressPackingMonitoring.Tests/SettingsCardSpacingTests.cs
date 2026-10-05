using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 设置页卡片上下的留白要一样宽。
///
/// 卡片自带 20 的内边距，行与行之间靠行自己的 14 下边距拉开；但"最后一行"会随显隐变化
/// （关掉高级设置、副画面那一张改回"无"、按机型收起的选项…），静态标记哪一行是最后一行迟早会错。
/// 现在由卡片自己收掉最后一个可见元素多留的那截下边距，这里盯着这套规则。
/// </summary>
[Collection("WPF render tests")]
public sealed class SettingsCardSpacingTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// 左侧页签之间的间距必须写在模板里面，不能写成 TabItem 的 Margin：
    /// TabStripPlacement=Left 时 TabPanel 给的布局槽只有内容那么高，控件自己的下边距
    /// 会被 layout 系统再减一次，页签最底下那段间隔连方框底边一起被 layout clip 裁掉
    /// （现场就是"选中方框下面少了一截"）。
    /// </summary>
    [Fact]
    public void SidebarTab_GapLivesInTemplateInsteadOfItemMargin()
    {
        XDocument document = XDocument.Load(FindSettingsXaml());
        XElement tabItemStyle = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute("TargetType") == "TabItem");

        Assert.DoesNotContain(
            tabItemStyle.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Margin");

        XElement templateRoot = tabItemStyle
            .Descendants(Presentation + "ControlTemplate")
            .Single()
            .Elements()
            .First();
        Assert.Equal("Grid", templateRoot.Name.LocalName);
        Assert.Equal("0,0,0,4", (string?)templateRoot.Attribute("Margin"));
    }
    /// <summary>卡片样式必须挂上"收掉尾部留白"，不用每张卡单独声明。</summary>
    [Fact]
    public void SectionCardStyle_EnablesTrailingGapTrim()
    {
        XDocument document = XDocument.Load(FindSettingsXaml());
        XElement style = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "SectionCardStyle");

        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            setter => ((string?)setter.Attribute("Property"))?.EndsWith(
                "SectionCardSpacing.TrimTrailingGap",
                StringComparison.Ordinal) == true
                && (string?)setter.Attribute("Value") == "True");
    }

    /// <summary>最后一行换人（原来那行被收起）时，卡片底部留白必须保持和顶部一致。</summary>
    [Fact]
    public void CardBottomGap_StaysEqualWhenTheLastRowChanges()
    {
        RunOnStaThread(() =>
        {
            var firstRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            var secondRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            // 末尾换成没有下边距的元素（按钮、表格这类）
            var trailingButton = new Button { Visibility = Visibility.Collapsed };
            var content = new StackPanel();
            content.Children.Add(firstRow);
            content.Children.Add(secondRow);
            content.Children.Add(trailingButton);
            var host = new Grid();
            // 卡片高度跟着内容走（生产里卡片也在 StackPanel 里），否则尺寸不变、钩子不会重算
            var card = new Border
            {
                Padding = new Thickness(20),
                Child = content,
                VerticalAlignment = VerticalAlignment.Top
            };
            host.Children.Add(card);
            SectionCardSpacing.SetTrimTrailingGap(card, true);

            Arrange(host);
            Assert.Equal(20, card.Padding.Top);
            Assert.Equal(6, card.Padding.Bottom, precision: 3);

            // 收起最后一行：露在最下面的换成另一行，底部留白不能因此变大
            secondRow.Visibility = Visibility.Collapsed;
            Arrange(host);
            Assert.Equal(20, card.Padding.Top);
            Assert.Equal(6, card.Padding.Bottom, precision: 3);

            // 末尾换成没有下边距的元素：卡片恢复完整内边距，底部照样是 20
            trailingButton.Visibility = Visibility.Visible;
            Arrange(host);
            Assert.Equal(20, card.Padding.Top);
            Assert.Equal(20, card.Padding.Bottom, precision: 3);
        });
    }

    /// <summary>
    /// 行间距只有一套：每个设置行都留同样的下边距，谁最后露出来由卡片收尾；
    /// 分隔线只补线下面的间距（线上的间距由上一行的下边距提供）。
    /// 这样任何一行被收起都不会留下半截间距，也不会再出现"上一行标了末行、下一行却还在"的粘连。
    /// </summary>
    [Fact]
    public void RowsShareOneSpacingAndDividersOnlyPadBelow()
    {
        string xaml = File.ReadAllText(FindSettingsXaml());
        Assert.DoesNotContain("SettingRowLastMargin", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BeforeDividerStyle", xaml, StringComparison.Ordinal);

        XDocument document = XDocument.Parse(xaml);
        Assert.Equal(14d, ReadThickness(document, "SettingRowMargin").Bottom, precision: 3);
        Assert.Equal(0d, ReadThickness(document, "SectionDividerMargin").Top, precision: 3);
        Assert.Equal(14d, ReadThickness(document, "SectionDividerMargin").Bottom, precision: 3);
    }

    private static Thickness ReadThickness(XDocument document, string key)
    {
        XElement element = Assert.Single(
            document.Descendants(Presentation + "Thickness"),
            candidate => (string?)candidate.Attribute(Xaml + "Key") == key);
        double[] parts = element.Value.Split(',').Select(part => double.Parse(part.Trim())).ToArray();
        Assert.Equal(4, parts.Length);
        return new Thickness(parts[0], parts[1], parts[2], parts[3]);
    }

    private static void Arrange(FrameworkElement host)
    {
        host.Measure(new Size(320, 640));
        host.Arrange(new Rect(0, 0, 320, 640));
        host.UpdateLayout();
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                    _ = new Application();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
            throw new Xunit.Sdk.XunitException($"卡片留白验证失败：{failure}");
    }

    private static string FindSettingsXaml()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                "UI",
                "SettingsWindow.xaml");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 SettingsWindow.xaml");
    }
}
