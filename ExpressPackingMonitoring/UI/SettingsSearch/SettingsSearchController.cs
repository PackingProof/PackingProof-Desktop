using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.UI.SettingsSearch
{
    /// <summary>
    /// 设置页搜索：把设置树（页签 → 卡片 → 行）当数据过滤，命中的留下、其余收起，
    /// 命中的关键词高亮，当前页签被收起时自动跳到第一个还有结果的页签。
    ///
    /// 三条规则：
    /// 1. 只留命中行：卡片里没有命中行就整张收起；页签里没有命中卡片就整页收起。
    /// 2. 不覆盖原有显隐：行本来带 Visibility 绑定（高级模式、机型能力、状态联动），
    ///    命中时把绑定挂回去让它自己决定；只有"不命中"才压成 Collapsed，清空搜索时原样还原。
    /// 3. 清空搜索 = 完全回到搜索前的样子（包括用户原来停在哪个页签）。
    /// </summary>
    internal sealed class SettingsSearchController
    {
        private readonly FrameworkElement _resourceOwner;
        private readonly TabControl _tabs;
        private readonly TextBlock? _countText;
        private readonly Border? _emptyState;
        private readonly List<HighlightedText> _highlighted = new();

        private IReadOnlyList<SettingsSearchTab> _index = Array.Empty<SettingsSearchTab>();
        private object? _selectedTabBeforeSearch;
        private string _appliedQuery = string.Empty;
        private bool _applying;

        internal SettingsSearchController(
            FrameworkElement resourceOwner,
            TabControl tabs,
            TextBlock? countText,
            Border? emptyState)
        {
            _resourceOwner = resourceOwner;
            _tabs = tabs;
            _countText = countText;
            _emptyState = emptyState;
        }

        internal bool IsActive => _appliedQuery.Length > 0;

        /// <summary>
        /// 高亮过的 TextBlock 要记原文：TextBlock 一旦被写进 Inlines，Text 那条值就不再参与渲染，
        /// 只清 Inlines 会让它变成空白（清空搜索后副标题消失就是这么来的）。
        /// 还原时把原来的 Text 值/绑定重新写回去。
        /// </summary>
        private sealed class HighlightedText
        {
            internal required TextBlock Element { get; init; }
            internal required string OriginalText { get; init; }
            internal required BindingBase? OriginalBinding { get; init; }
        }

        internal void ApplyQuery(string? rawQuery)
        {
            if (_applying)
                return;

            string query = SettingsSearchMatcher.Normalize(rawQuery);
            if (query.Length == 0)
            {
                Restore();
                return;
            }

            EnsureIndex();
            _applying = true;
            try
            {
                ApplyFilter(query);
            }
            finally
            {
                _applying = false;
            }
        }

        private void ApplyFilter(string query)
        {
            _appliedQuery = query;
            ClearHighlights();

            int matches = 0;
            foreach (SettingsSearchTab tab in _index)
            {
                foreach (SettingsSearchCard card in tab.Cards)
                {
                    if (card.Self is { } self)
                        self.Matched = SettingsSearchMatcher.IsMatch(self.Text, query);
                    foreach (SettingsSearchRow row in card.Rows)
                        row.Matched = SettingsSearchMatcher.IsMatch(row.Text, query);
                }
            }

            foreach (SettingsSearchTab tab in _index)
            {
                foreach (SettingsSearchCard card in tab.Cards)
                {
                    ApplyVisibility(card.Element, card.OriginalVisibility, card.OriginalVisibilityBinding, card.Matched);
                    foreach (SettingsSearchRow row in card.Rows)
                    {
                        ApplyVisibility(row.Element, row.OriginalVisibility, row.OriginalVisibilityBinding, row.Matched);
                        if (!row.Matched)
                            continue;

                        matches++;
                        Highlight(row.Element, query);
                    }

                    if (card.Self is { Matched: true } self)
                    {
                        matches++;
                        Highlight(self.Element, query);
                    }
                }

                ApplyVisibility(tab.Element, tab.OriginalVisibility, tab.OriginalVisibilityBinding, tab.Matched);
            }

            // 当前页签被收起之后 WPF 仍然保持选中（内容区会空着），跳到第一个还有结果的页签。
            if (!IsAvailable(_tabs.SelectedItem as TabItem))
            {
                _selectedTabBeforeSearch ??= _tabs.SelectedItem;
                SettingsSearchTab? first = _index.FirstOrDefault(
                    tab => tab.Matched && tab.Element.Visibility == Visibility.Visible);
                if (first is not null)
                    _tabs.SelectedItem = first.Element;
            }

            UpdateStatus(matches);
        }

        /// <summary>清空搜索：还原显隐、清掉高亮、回到搜索前停的那个页签。</summary>
        internal void Restore()
        {
            _appliedQuery = string.Empty;
            ClearHighlights();

            foreach (SettingsSearchTab tab in _index)
            {
                foreach (SettingsSearchCard card in tab.Cards)
                {
                    RestoreVisibility(card.Element, card.OriginalVisibility, card.OriginalVisibilityBinding);
                    foreach (SettingsSearchRow row in card.Rows)
                        RestoreVisibility(row.Element, row.OriginalVisibility, row.OriginalVisibilityBinding);
                }

                RestoreVisibility(tab.Element, tab.OriginalVisibility, tab.OriginalVisibilityBinding);
            }

            if (_selectedTabBeforeSearch is TabItem previous
                && _tabs.Items.Contains(previous)
                && previous.Visibility == Visibility.Visible)
            {
                _tabs.SelectedItem = previous;
            }

            _selectedTabBeforeSearch = null;
            UpdateStatus(null);
        }

        private void EnsureIndex()
        {
            if (_index.Count > 0)
                return;

            _index = SettingsSearchIndex.Build(_tabs, _resourceOwner);
            CollectRealizedCards();
            _tabs.SelectionChanged -= OnTabsSelectionChanged;
            _tabs.SelectionChanged += OnTabsSelectionChanged;
        }

        /// <summary>
        /// 逻辑树扫不到的卡片（ItemsControl + DataTemplate 生成出来的，比如"副摄像头 1"）在这里补进索引：
        /// 没有这一步，搜索就管不到它们 —— 会出现"一条都没命中，那张卡还挂在那儿"。
        /// </summary>
        private void CollectRealizedCards()
        {
            if (_index.Count == 0)
                return;

            foreach (SettingsSearchTab tab in _index)
            {
                if (tab.Element.Content is not DependencyObject content)
                    continue;

                foreach (FrameworkElement card in SettingsSearchIndex.FindRealizedCards(content, _resourceOwner))
                {
                    if (tab.Cards.Any(existing => ReferenceEquals(existing.Element, card)))
                        continue;

                    tab.Cards.Add(SettingsSearchIndex.CreateCard(card, _resourceOwner));
                }
            }
        }

        /// <summary>页签切过去之后视觉树才建出来：补一次索引再跑一遍过滤，别漏掉里面的卡片。</summary>
        private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CollectRealizedCards();
            if (IsActive && !_applying)
                ApplyQuery(_appliedQuery);
        }

        private static bool IsAvailable(TabItem? tab) =>
            tab is not null && tab.Visibility == Visibility.Visible;

        private static void ApplyVisibility(
            FrameworkElement element,
            Visibility original,
            BindingBase? binding,
            bool matched)
        {
            if (!matched)
            {
                element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                return;
            }

            // 命中的行把原来的绑定挂回去：机型能力、状态联动该收起还是收起，搜索不会强行点亮。
            if (binding is not null)
                BindingOperations.SetBinding(element, UIElement.VisibilityProperty, binding);
            else
                element.SetCurrentValue(UIElement.VisibilityProperty, original);
        }

        private static void RestoreVisibility(FrameworkElement element, Visibility original, BindingBase? binding) =>
            ApplyVisibility(element, original, binding, matched: true);

        private void UpdateStatus(int? matches)
        {
            if (_countText is not null)
            {
                bool active = _appliedQuery.Length > 0;
                _countText.Text = !active
                    ? string.Empty
                    : matches is null or 0
                        ? AppLanguage.Get("没有找到设置项")
                        : AppLanguage.Format("共 {0} 项", matches.Value);
                _countText.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    active ? Visibility.Visible : Visibility.Collapsed);
            }

            if (_emptyState is not null)
            {
                bool empty = _appliedQuery.Length > 0 && matches is null or 0;
                _emptyState.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    empty ? Visibility.Visible : Visibility.Collapsed);
            }
        }

        /// <summary>把命中行里出现的查询词标出来：整段文字拆成 Run，命中那段换个底色。</summary>
        private void Highlight(FrameworkElement root, string query)
        {
            Brush? background = _resourceOwner.TryFindResource("AccentBlue") as Brush;
            Brush? foreground = _resourceOwner.TryFindResource("TextOnAccent") as Brush;

            foreach (TextBlock text in Descendants(root).OfType<TextBlock>())
            {
                if (_highlighted.Any(item => ReferenceEquals(item.Element, text)))
                    continue;

                string original = text.Text;
                IReadOnlyList<(int Start, int Length)> ranges = SettingsSearchMatcher.FindRanges(original, query);
                if (ranges.Count == 0)
                    continue;

                _highlighted.Add(new HighlightedText
                {
                    Element = text,
                    OriginalText = original,
                    OriginalBinding = BindingOperations.GetBindingBase(text, TextBlock.TextProperty),
                });

                text.Inlines.Clear();
                int cursor = 0;
                foreach ((int start, int length) in ranges)
                {
                    if (start > cursor)
                        text.Inlines.Add(new Run(original[cursor..start]));

                    var hit = new Run(original.Substring(start, length)) { FontWeight = FontWeights.SemiBold };
                    if (background is not null)
                        hit.Background = background;
                    if (foreground is not null)
                        hit.Foreground = foreground;
                    text.Inlines.Add(hit);
                    cursor = start + length;
                }

                if (cursor < original.Length)
                    text.Inlines.Add(new Run(original[cursor..]));
            }
        }

        private void ClearHighlights()
        {
            foreach (HighlightedText item in _highlighted)
            {
                // 绑定的把绑定挂回去（它会重新写 Text），静态的就把原文写回 Text ——
                // 只清 Inlines 是不够的：Text 那条值不会自动重新参与渲染。
                if (item.OriginalBinding is not null)
                    BindingOperations.SetBinding(item.Element, TextBlock.TextProperty, item.OriginalBinding);
                else
                    item.Element.Text = item.OriginalText;
            }

            _highlighted.Clear();
        }

        private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
        {
            // 用显式栈而不是递归：逻辑树里万一出现环（列/模板这类元素），递归会直接把进程打崩。
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
    }
}
