using System;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 卡片底部不再多留一截。卡片自带内边距，行与行之间的间距由行自己的下边距提供，
    /// 但"最后一行"是会变的：关掉高级设置、副画面那一张从"无"改成接了设备、按机型收起的选项露出来之后，
    /// 排在最下面的换成了另一行，若还照它的下边距算，卡片底部就比顶部多出一圈。
    ///
    /// 静态 XAML 预知不了谁最后露出来，所以这里在布局时把卡片的下内边距减掉
    /// "最后一个可见子元素链上的下边距"，让底部留白始终等于顶部留白；最后一行换人时自动重算。
    /// 挂在 SectionCardStyle 上，所有卡片一次性生效，新增卡片不用再手工标记哪一行是最后一行。
    /// </summary>
    public static class SectionCardSpacing
    {
        public static readonly DependencyProperty TrimTrailingGapProperty =
            DependencyProperty.RegisterAttached(
                "TrimTrailingGap",
                typeof(bool),
                typeof(SectionCardSpacing),
                new PropertyMetadata(false, OnTrimTrailingGapChanged));

        /// <summary>样式给出的原始内边距：我们改的是本地值，得先把原值记下来。</summary>
        private static readonly DependencyProperty BasePaddingProperty =
            DependencyProperty.RegisterAttached(
                "BasePadding",
                typeof(Thickness),
                typeof(SectionCardSpacing),
                new PropertyMetadata(new Thickness(double.NaN)));

        public static void SetTrimTrailingGap(DependencyObject element, bool value) =>
            element.SetValue(TrimTrailingGapProperty, value);

        public static bool GetTrimTrailingGap(DependencyObject element) =>
            (bool)element.GetValue(TrimTrailingGapProperty);

        private static void OnTrimTrailingGapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border card)
                return;

            card.Loaded -= OnCardLoaded;
            card.SizeChanged -= OnCardSizeChanged;
            if (e.NewValue is true)
            {
                // 卡片里的选项是静态 XAML，显隐由绑定驱动：盯住后代的显隐就够了，
                // 不依赖布局事件（布局事件在没接上窗口的树上不会来）。
                card.Loaded += OnCardLoaded;
                // 最后一行收起/放开都会让卡片高度变一次，这条在没有窗口的树上也能触发。
                card.SizeChanged += OnCardSizeChanged;
                Watch(card);
                Apply(card);
            }
            else
            {
                Watch(card, subscribe: false);
            }
        }

        private static void OnCardLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border card)
            {
                Watch(card);
                Apply(card);
            }
        }

        private static void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border card)
                Apply(card);
        }

        private static void OnDescendantVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 收起或放开都算：露在最后的那一行换人了，就得重算一次。
            if (sender is DependencyObject changed && FindCard(changed) is { } card)
                Apply(card);
        }

        /// <summary>从被改动的元素往上找它所属的卡片。</summary>
        private static Border? FindCard(DependencyObject element)
        {
            DependencyObject? current = element;
            while (current is not null)
            {
                if (current is Border { } border && GetTrimTrailingGap(border))
                    return border;

                current = LogicalTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>卡片内容里的所有后代都挂上显隐监听；重复调用不会重复挂钩。</summary>
        private static void Watch(Border card, bool subscribe = true)
        {
            foreach (FrameworkElement element in Descendants(card.Child))
            {
                element.IsVisibleChanged -= OnDescendantVisibilityChanged;
                if (subscribe)
                    element.IsVisibleChanged += OnDescendantVisibilityChanged;
            }
        }

        private static IEnumerable<FrameworkElement> Descendants(DependencyObject? root)
        {
            if (root is null)
                yield break;

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not FrameworkElement element)
                    continue;

                yield return element;
                foreach (FrameworkElement grandChild in Descendants(element))
                    yield return grandChild;
            }
        }

        private static void Apply(Border card)
        {
            if (card.Visibility != Visibility.Visible || TrailingGap(card) is not double trailingGap)
                return;

            var basePadding = (Thickness)card.GetValue(BasePaddingProperty);
            if (double.IsNaN(basePadding.Top))
            {
                basePadding = card.Padding;
                card.SetValue(BasePaddingProperty, basePadding);
            }

            double bottom = Math.Max(0, basePadding.Bottom - trailingGap);
            if (Math.Abs(card.Padding.Bottom - bottom) < 0.1)
                return; // 已经对上了：这一步也顺手挡住"改完内边距又触发一轮布局"的反复

            card.Padding = new Thickness(basePadding.Left, basePadding.Top, basePadding.Right, bottom);
        }

        /// <summary>
        /// 最后一个可见子元素还会在卡片底部留多宽：从卡片内容往下顺着"最后一个可见子元素"走，
        /// 把这条链上每一层的下边距加起来（中间的分组 StackPanel 自己也可能带下边距）。
        /// 末尾是按钮、表格这类没有下边距的元素时返回 0，卡片内边距保持原样。
        /// </summary>
        private static double? TrailingGap(Border card)
        {
            FrameworkElement? current = card.Child as FrameworkElement;
            if (current is null)
                return null;

            double gap = current.Margin.Bottom;
            for (int depth = 0; depth < 16 && current is StackPanel group; depth++)
            {
                FrameworkElement? next = null;
                for (int i = group.Children.Count - 1; i >= 0; i--)
                {
                    if (group.Children[i] is FrameworkElement child && child.Visibility == Visibility.Visible)
                    {
                        next = child;
                        break;
                    }
                }

                if (next is null)
                    return gap;

                gap += next.Margin.Bottom;
                current = next;
            }

            return gap;
        }
    }
}
