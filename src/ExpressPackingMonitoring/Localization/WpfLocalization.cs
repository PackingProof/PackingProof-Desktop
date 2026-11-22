using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.Localization;

/// <summary>
/// 本地化的 WPF 部分：控件树自动翻译与绑定转换器。
/// 拆分出来是为了让不引用 WPF 的宿主（例如 macOS 备份主机）能共用同一份文案逻辑。
/// </summary>
public static class WpfLocalization
{
    private static readonly ConditionalWeakTable<DependencyObject, HashSet<DependencyProperty>> WatchedProperties = new();

    public static readonly DependencyProperty AutoLocalizeProperty = DependencyProperty.RegisterAttached(
        "AutoLocalize",
        typeof(bool),
        typeof(WpfLocalization),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetAutoLocalize(DependencyObject element, bool value) =>
        element.SetValue(AutoLocalizeProperty, value);

    public static bool GetAutoLocalize(DependencyObject element) =>
        (bool)element.GetValue(AutoLocalizeProperty);

    public static void EnableAutomaticWpfLocalization()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => LocalizeElement(sender as FrameworkElement)));
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => LocalizeContentElement(sender as FrameworkContentElement)));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler((sender, _) => LocalizeToolTip(sender as FrameworkElement)));
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler((sender, _) => LocalizeToolTip(sender as FrameworkContentElement)));
    }

    private static void LocalizeElement(FrameworkElement? element)
    {
        if (AppLanguage.IsChinese || element == null) return;
        if (element is Window windowRoot)
        {
            windowRoot.Dispatcher.BeginInvoke(
                () => LocalizeTree(windowRoot),
                DispatcherPriority.Loaded);
        }

        LocalizeSingleElement(element);
    }

    internal static void LocalizeTree(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        LocalizeTreeCore(root, visited);
    }

    private static void LocalizeTreeCore(DependencyObject current, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(current)) return;

        // Snapshot both trees before changing any text. Updating Run.Text or a
        // ContentControl can invalidate WPF's live logical-tree enumerator.
        DependencyObject[] logicalChildren = LogicalTreeHelper.GetChildren(current)
            .OfType<DependencyObject>()
            .ToArray();
        int visualChildrenCount = current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetChildrenCount(current)
            : 0;
        var visualChildren = new DependencyObject[visualChildrenCount];
        for (int index = 0; index < visualChildrenCount; index++)
            visualChildren[index] = VisualTreeHelper.GetChild(current, index);

        if (current is FrameworkElement element)
            LocalizeSingleElement(element);
        else if (current is FrameworkContentElement contentElement)
            LocalizeContentElement(contentElement);

        foreach (DependencyObject child in logicalChildren)
            LocalizeTreeCore(child, visited);
        foreach (DependencyObject child in visualChildren)
            LocalizeTreeCore(child, visited);
    }

    private static void LocalizeSingleElement(FrameworkElement element)
    {
        if (!GetAutoLocalize(element))
            return;

        switch (element)
        {
            case Window window:
                window.SetCurrentValue(Window.TitleProperty, AppLanguage.Translate(window.Title));
                Watch(window, Window.TitleProperty);
                break;
            case TextBlock textBlock:
                if (ShouldLocalizeTextProperty(textBlock))
                    textBlock.SetCurrentValue(TextBlock.TextProperty, AppLanguage.Translate(textBlock.Text));
                // 绑定来的运行时文案（设备状态、副摄像头编号等）也要翻；
                // 只认整条词条或完整句式，不改用户数据里的“标签：内容”。
                WatchRuntime(textBlock, TextBlock.TextProperty);
                break;
            case HeaderedContentControl headered:
                if (headered.Header is string header)
                    headered.SetCurrentValue(HeaderedContentControl.HeaderProperty, AppLanguage.Translate(header));
                WatchRuntime(headered, HeaderedContentControl.HeaderProperty);
                if (headered.Content is string headerContent)
                    headered.SetCurrentValue(ContentControl.ContentProperty, AppLanguage.Translate(headerContent));
                WatchRuntime(headered, ContentControl.ContentProperty);
                break;
            case ContentControl control:
                if (control.Content is string text)
                    control.SetCurrentValue(ContentControl.ContentProperty, AppLanguage.Translate(text));
                WatchRuntime(control, ContentControl.ContentProperty);
                break;
            case ContentPresenter presenter:
                if (presenter.Content is string presenterText)
                    presenter.SetCurrentValue(
                        ContentPresenter.ContentProperty,
                        AppLanguage.TranslateRuntime(presenterText));
                WatchRuntime(presenter, ContentPresenter.ContentProperty);
                break;
        }

        if (element.ToolTip is string tooltip)
            element.SetCurrentValue(FrameworkElement.ToolTipProperty, AppLanguage.Translate(tooltip));

        string automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName))
            element.SetCurrentValue(
                AutomationProperties.NameProperty,
                AppLanguage.Translate(automationName));
    }

    /// <summary>
    /// ToolTip 经常由 ViewModel 在窗口加载后才赋值，而且可能包含多行“标签：内容”。
    /// 在真正弹出前再翻译一次，既不丢掉绑定，也不用给每个元素挂属性监听。
    /// </summary>
    internal static void LocalizeToolTip(DependencyObject? element)
    {
        if (element == null || AppLanguage.IsChinese) return;
        if (ToolTipService.GetToolTip(element) is not string text) return;
        string translated = AppLanguage.Translate(text);
        if (string.Equals(translated, text, StringComparison.Ordinal)) return;
        element.SetCurrentValue(
            element is FrameworkContentElement
                ? FrameworkContentElement.ToolTipProperty
                : FrameworkElement.ToolTipProperty,
            translated);
    }

    internal static bool ShouldLocalizeTextProperty(TextBlock textBlock) =>
        GetAutoLocalize(textBlock)
        && textBlock.ReadLocalValue(TextBlock.TextProperty) != DependencyProperty.UnsetValue;

    private static void LocalizeContentElement(FrameworkContentElement? element)
    {
        if (AppLanguage.IsChinese || element == null || !GetAutoLocalize(element)) return;
        if (element is Run run)
        {
            run.SetCurrentValue(Run.TextProperty, AppLanguage.Translate(run.Text));
            Watch(run, Run.TextProperty);
        }
    }

    private static void Watch(DependencyObject element, DependencyProperty property)
    {
        HashSet<DependencyProperty> properties = WatchedProperties.GetOrCreateValue(element);
        if (!properties.Add(property)) return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
        if (descriptor == null) return;
        descriptor.AddValueChanged(element, (_, _) =>
        {
            object value = element.GetValue(property);
            if (value is string text)
            {
                string translated = AppLanguage.Translate(text);
                if (translated != text) element.SetCurrentValue(property, translated);
            }
        });
    }

    /// <summary>
    /// 监听绑定到界面上的字符串，按整条词条翻译。
    /// SetCurrentValue 不会拆掉绑定，下一次绑定更新会重新走这里。
    /// </summary>
    private static void WatchRuntime(DependencyObject element, DependencyProperty property)
    {
        HashSet<DependencyProperty> properties = WatchedProperties.GetOrCreateValue(element);
        if (!properties.Add(property)) return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
        if (descriptor == null) return;
        descriptor.AddValueChanged(element, (_, _) =>
        {
            if (AppLanguage.IsChinese) return;
            if (element.GetValue(property) is not string text) return;
            string translated = AppLanguage.TranslateRuntime(text);
            if (!string.Equals(translated, text, StringComparison.Ordinal))
                element.SetCurrentValue(property, translated);
        });
    }
}

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        AppLanguage.Translate(value?.ToString() ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
