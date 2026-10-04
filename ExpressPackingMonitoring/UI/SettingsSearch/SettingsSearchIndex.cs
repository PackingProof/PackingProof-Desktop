using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI.SettingsSearch
{
    /// <summary>
    /// 搜索的最小单位：一行设置。
    ///
    /// 行本来就带 Visibility 绑定（高级模式、机型能力、状态联动），所以搜索不直接覆盖它：
    /// 命中时把原来的绑定挂回去（让它自己决定显不显示），没命中才压成 Collapsed，
    /// 清空搜索时同样把绑定/原值还原。
    /// </summary>
    internal sealed class SettingsSearchRow
    {
        internal required FrameworkElement Element { get; init; }
        internal required string Text { get; init; }
        internal required Visibility OriginalVisibility { get; init; }
        internal required BindingBase? OriginalVisibilityBinding { get; init; }
        internal bool Matched { get; set; }
    }

    /// <summary>一张卡：要么按行过滤（只留命中行），要么整张卡就是一项（卡片里没有设置行时）。</summary>
    internal sealed class SettingsSearchCard
    {
        internal required FrameworkElement Element { get; init; }
        internal required Visibility OriginalVisibility { get; init; }
        internal required BindingBase? OriginalVisibilityBinding { get; init; }
        internal required IReadOnlyList<SettingsSearchRow> Rows { get; init; }
        internal SettingsSearchRow? Self { get; init; }

        internal bool Matched => Self is { Matched: true } || Rows.Any(row => row.Matched);
    }

    /// <summary>一个页签：里面一张卡都没命中就收起这个页签。</summary>
    internal sealed class SettingsSearchTab
    {
        internal required TabItem Element { get; init; }
        internal required Visibility OriginalVisibility { get; init; }
        internal required BindingBase? OriginalVisibilityBinding { get; init; }
        internal required List<SettingsSearchCard> Cards { get; init; }

        internal bool Matched => Cards.Any(card => card.Matched);
    }

    /// <summary>
    /// 打开设置页时把整棵设置树索引出来：页签 → 卡片 → 行，每项都带"可搜索文本"。
    ///
    /// 只读逻辑树，不需要哪个页签被选中过（TabItem.Content 在解析 XAML 时就建好了），
    /// 所以搜索前不用把十个页签挨个点一遍。
    /// </summary>
    internal static class SettingsSearchIndex
    {
        private static readonly string[] RowStyleKeys =
        [
            "SettingRowStyle",
            "SettingChildRowStyle",
            "AdvancedSettingRowStyle",
            "AdvancedSettingChildRowStyle",
        ];

        internal static IReadOnlyList<SettingsSearchTab> Build(TabControl tabs, FrameworkElement resourceOwner)
        {
            ArgumentNullException.ThrowIfNull(tabs);

            Style? cardStyle = resourceOwner.TryFindResource("SectionCardStyle") as Style;
            List<Style> rowStyles = ResolveRowStyles(resourceOwner);

            var result = new List<SettingsSearchTab>();
            foreach (object item in tabs.Items)
            {
                if (item is not TabItem tab)
                    continue;

                var cards = new List<SettingsSearchCard>();
                if (tab.Content is DependencyObject content)
                {
                    foreach (FrameworkElement element in Descendants(content))
                    {
                        if (!IsCard(element, cardStyle))
                            continue;

                        cards.Add(BuildCard(element, rowStyles));
                    }
                }

                result.Add(new SettingsSearchTab
                {
                    Element = tab,
                    OriginalVisibility = tab.Visibility,
                    OriginalVisibilityBinding = BindingOperations.GetBindingBase(tab, UIElement.VisibilityProperty),
                    Cards = cards,
                });
            }

            return result;
        }

        /// <summary>
        /// 逻辑树扫不到 ItemsControl/DataTemplate 生成出来的卡片（副摄像头那张卡就是这样），
        /// 所以还要按视觉树补一遍：只有已经显示过的页签才有视觉树，没显示过的等切过去再补。
        /// </summary>
        internal static IEnumerable<FrameworkElement> FindRealizedCards(
            DependencyObject root,
            FrameworkElement resourceOwner)
        {
            if (resourceOwner.TryFindResource("SectionCardStyle") is not Style cardStyle)
                yield break;

            foreach (FrameworkElement element in VisualDescendants(root))
            {
                if (IsCard(element, cardStyle))
                    yield return element;
            }
        }

        /// <summary>按同一套规则给一张"后补进来"的卡片建索引项。</summary>
        internal static SettingsSearchCard CreateCard(FrameworkElement card, FrameworkElement resourceOwner) =>
            BuildCard(card, ResolveRowStyles(resourceOwner));

        private static List<Style> ResolveRowStyles(FrameworkElement resourceOwner) =>
            RowStyleKeys
                .Select(key => resourceOwner.TryFindResource(key) as Style)
                .Where(style => style is not null)
                .Select(style => style!)
                .ToList();

        private static SettingsSearchCard BuildCard(FrameworkElement card, IReadOnlyList<Style> rowStyles)
        {
            string title = TitleOf(card);
            var rows = new List<SettingsSearchRow>();
            foreach (FrameworkElement element in Descendants(card))
            {
                if (element is not Grid grid || !rowStyles.Contains(grid.Style))
                    continue;

                // 行的可搜索文本带上卡片标题：搜"麦克风"能把这台卡片下的行一起带出来。
                string text = string.Join(" | ", new[] { title, CollectText(grid) }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                rows.Add(new SettingsSearchRow
                {
                    Element = grid,
                    Text = text,
                    OriginalVisibility = grid.Visibility,
                    OriginalVisibilityBinding = BindingOperations.GetBindingBase(grid, UIElement.VisibilityProperty),
                });
            }

            SettingsSearchRow? self = null;
            if (rows.Count == 0)
            {
                // 卡片里没有设置行（按钮、表格这种），整张卡算一项。
                string text = string.Join(" | ", new[] { title, CollectText(card) }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                self = new SettingsSearchRow
                {
                    Element = card,
                    Text = text,
                    OriginalVisibility = card.Visibility,
                    OriginalVisibilityBinding = BindingOperations.GetBindingBase(card, UIElement.VisibilityProperty),
                };
            }

            return new SettingsSearchCard
            {
                Element = card,
                OriginalVisibility = card.Visibility,
                OriginalVisibilityBinding = BindingOperations.GetBindingBase(card, UIElement.VisibilityProperty),
                Rows = rows,
                Self = self,
            };
        }

        private static bool IsCard(FrameworkElement element, Style? cardStyle) =>
            element is Border border
            && cardStyle is not null
            && ReferenceEquals(border.Style, cardStyle);

        /// <summary>卡片标题：卡片里第一个有文字的 TextBlock（和人工看卡片时的第一眼一致）。</summary>
        private static string TitleOf(FrameworkElement card) =>
            Descendants(card).OfType<TextBlock>().FirstOrDefault(text => !string.IsNullOrWhiteSpace(text.Text))?.Text
            ?? string.Empty;

        /// <summary>
        /// 把这一块里"用户看得见、可能拿来搜"的文字都收进来：
        /// 标签、副标题、下拉选项、勾选框/按钮文字、ToolTip、无障碍名称。
        /// </summary>
        private static string CollectText(DependencyObject root)
        {
            var parts = new List<string>();
            foreach (FrameworkElement element in Descendants(root))
            {
                switch (element)
                {
                    case TextBlock { Text.Length: > 0 } text:
                        parts.Add(text.Text);
                        break;
                    case ComboBoxItem item when item.Content is string itemText:
                        parts.Add(itemText);
                        break;
                    case CheckBox check when check.Content is string checkText:
                        parts.Add(checkText);
                        break;
                    case Button { Content: string buttonText }:
                        parts.Add(buttonText);
                        break;
                    case ContentControl { Content: string contentText } when element is not TextBlock:
                        parts.Add(contentText);
                        break;
                }

                if (element.ToolTip is string tooltip)
                    parts.Add(tooltip);

                string? automationName = AutomationProperties.GetName(element);
                if (!string.IsNullOrWhiteSpace(automationName))
                    parts.Add(automationName);
            }

            return string.Join(" | ", parts
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.Ordinal));
        }

        private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
        {
            // 显式栈 + 去过重：逻辑树里万一出现环，递归遍历会直接把进程打崩（栈溢出）。
            var pending = new Stack<DependencyObject>();
            var visited = new HashSet<DependencyObject>();
            pending.Push(root);

            int guard = 0;
            while (pending.Count > 0 && guard++ < 20000)
            {
                DependencyObject current = pending.Pop();
                if (!visited.Add(current))
                    continue;

                foreach (object child in LogicalTreeHelper.GetChildren(current))
                {
                    if (child is not DependencyObject dependency)
                        continue;

                    if (dependency is FrameworkElement element)
                        yield return element;
                    pending.Push(dependency);
                }
            }
        }

        private static IEnumerable<FrameworkElement> VisualDescendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            var visited = new HashSet<DependencyObject>();
            pending.Push(root);

            int guard = 0;
            while (pending.Count > 0 && guard++ < 20000)
            {
                DependencyObject current = pending.Pop();
                if (!visited.Add(current))
                    continue;

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement element)
                        yield return element;
                    pending.Push(child);
                }
            }
        }

        /// <summary>设置页里的 ItemsControl（副画面卡片这种），用来在后补卡片时重新过滤。</summary>
        internal static IEnumerable<ItemsControl> FindItemsControls(DependencyObject root)
        {
            foreach (FrameworkElement element in VisualDescendants(root))
            {
                if (element is ItemsControl control && element is not ComboBox)
                    yield return control;
            }
        }
    }
}
