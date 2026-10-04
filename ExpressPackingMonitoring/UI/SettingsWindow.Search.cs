using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.UI.SettingsSearch;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 设置页搜索的接线：输入框在左栏顶部（TabControl 的模板里），
    /// 过滤、高亮、计数这些逻辑都在 <see cref="SettingsSearchController"/> 里，
    /// 这里只负责取到模板里的几个元素并把事件接上。
    /// </summary>
    public partial class SettingsWindow
    {
        private SettingsSearchController? _settingsSearch;

        /// <summary>
        /// 输入停下来再过滤：中文输入法在组词过程中会连续触发 TextChanged，
        /// 每个字都跑一遍"改显隐 + 重建高亮"既浪费，也容易在输入法合成过程中动界面。
        /// </summary>
        private readonly DispatcherTimer _settingsSearchDebounce = new()
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        private TextBox? _settingsSearchBox;

        /// <summary>输入框刚进模板时把控制器建起来：顺带拿到计数文字和"没有找到"提示。</summary>
        private void SettingsSearchBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (_settingsSearch is not null || sender is not TextBox box)
                return;
            if (box.TemplatedParent is not TabControl tabs)
                return;

            TextBlock? countText = tabs.Template?.FindName("SettingsSearchCountText", tabs) as TextBlock;
            Border? emptyState = tabs.Template?.FindName("SettingsSearchEmptyState", tabs) as Border;
            _settingsSearch = new SettingsSearchController(this, tabs, countText, emptyState);
            _settingsSearchBox = box;
            _settingsSearchDebounce.Stop();
            _settingsSearchDebounce.Tick -= SettingsSearchDebounce_Tick;
            _settingsSearchDebounce.Tick += SettingsSearchDebounce_Tick;
        }

        private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box)
                return;

            if (box.TemplatedParent is TabControl tabs
                && tabs.Template?.FindName("SettingsSearchPlaceholder", tabs) is TextBlock placeholder)
            {
                placeholder.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed);
            }

            _settingsSearchDebounce.Stop();
            _settingsSearchDebounce.Start();
        }

        private void SettingsSearchDebounce_Tick(object? sender, EventArgs e)
        {
            _settingsSearchDebounce.Stop();
            _settingsSearch?.ApplyQuery(_settingsSearchBox?.Text);
        }

        /// <summary>Esc 一键清空搜索，回到搜索前的样子。</summary>
        private void SettingsSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || sender is not TextBox box)
                return;

            if (box.Text.Length == 0)
                return;

            box.Text = string.Empty;
            _settingsSearchDebounce.Stop();
            _settingsSearch?.ApplyQuery(string.Empty);
            e.Handled = true;
        }
    }
}
