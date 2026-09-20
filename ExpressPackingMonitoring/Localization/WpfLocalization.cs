using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.Localization;

/// <summary>
/// 本地化的 WPF 部分：控件树自动翻译与绑定转换器。
/// 拆分出来是为了让不引用 WPF 的宿主（例如 macOS 保存主机）能共用同一份文案逻辑。
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

    private static void LocalizeTree(DependencyObject root)
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
            case TextBlock textBlock when ShouldLocalizeTextProperty(textBlock):
                textBlock.SetCurrentValue(TextBlock.TextProperty, AppLanguage.Translate(textBlock.Text));
                Watch(textBlock, TextBlock.TextProperty);
                break;
            case ContentControl control when control.Content is string text:
                control.SetCurrentValue(ContentControl.ContentProperty, AppLanguage.Translate(text));
                Watch(control, ContentControl.ContentProperty);
                break;
            case HeaderedContentControl control when control.Header is string header:
                control.SetCurrentValue(HeaderedContentControl.HeaderProperty, AppLanguage.Translate(header));
                Watch(control, HeaderedContentControl.HeaderProperty);
                break;
        }

        if (element.ToolTip is string tooltip)
            element.SetCurrentValue(FrameworkElement.ToolTipProperty, AppLanguage.Translate(tooltip));
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
}

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        AppLanguage.Translate(value?.ToString() ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
