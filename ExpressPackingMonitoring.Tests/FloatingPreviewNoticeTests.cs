using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 主界面最小化后只有小窗可见，Toast 弹在主界面上等于没提示。
/// 这里锁住"提示同时送到小窗"的接线，避免以后又只在主界面弹。
/// </summary>
public sealed class FloatingPreviewNoticeTests
{
    [Fact]
    public void ToastIsForwardedToFloatingPreviewWhileItIsVisible()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Notifications.cs"));

        int presentIndex = source.IndexOf("private void PresentToast(", StringComparison.Ordinal);
        Assert.True(presentIndex >= 0, "未找到 Toast 呈现方法");

        string present = source[presentIndex..];
        int methodEnd = present.IndexOf("\n        }", StringComparison.Ordinal);
        present = methodEnd >= 0 ? present[..methodEnd] : present;

        Assert.Contains("IsFloatingPreviewActive", present, StringComparison.Ordinal);
        Assert.Contains("FloatingPreviewNoticeRequested", present, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingWindowShowsNoticesAndUnsubscribesOnClose()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "UI",
            "FloatingPreviewWindow.xaml.cs"));

        Assert.Contains("FloatingPreviewNoticeRequested += OnNoticeRequested;", source, StringComparison.Ordinal);
        Assert.Contains("FloatingPreviewNoticeRequested -= OnNoticeRequested;", source, StringComparison.Ordinal);
        Assert.Contains("ShowInlineNotice(message, duration)", source, StringComparison.Ordinal);
    }

    /// <summary>警告与错误要在小窗上多留一会儿，否则店员来不及看清就消失了。</summary>
    [Fact]
    public void WarningNoticesStayLongerThanInformationalOnes()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "UI",
            "FloatingPreviewWindow.xaml.cs"));

        int handlerIndex = source.IndexOf("private void OnNoticeRequested(", StringComparison.Ordinal);
        Assert.True(handlerIndex >= 0, "未找到小窗提示处理");

        string handler = source[handlerIndex..];
        int methodEnd = handler.IndexOf("\n        }", StringComparison.Ordinal);
        handler = methodEnd >= 0 ? handler[..methodEnd] : handler;

        Assert.Contains("ToastSeverity.Warning", handler, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(6)", handler, StringComparison.Ordinal);
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
