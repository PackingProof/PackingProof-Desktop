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
}
