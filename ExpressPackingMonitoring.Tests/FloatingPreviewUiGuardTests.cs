using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 悬浮小窗的界面约束：主界面不新增按钮，小窗必须复用既有流光与圆角风格。
/// 这些点在代码审查里很容易被重新写一套，用守卫钉住。
/// </summary>
public sealed class FloatingPreviewUiGuardTests
{
    /// <summary>主界面底部按钮行是固定的，小窗只能由最小化进入，不允许新增入口按钮。</summary>
    [Fact]
    public void MainWindow_DoesNotAddFloatingPreviewButton()
    {
        string mainWindow = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.DoesNotContain("FloatingPreviewButton", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("BtnFloatingPreview_Click", mainWindow, StringComparison.Ordinal);
    }

    /// <summary>
    /// 流光必须沿用主界面录制卡片的做法：平移渐变画刷的 RelativeTransform，
    /// 并复用 RecordingShimmer 颜色令牌，而不是自己平移一个控件、写死颜色。
    /// </summary>
    [Fact]
    public void FloatingPreview_ReusesRecordingShimmerTechnique()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml"));
        string codeBehind = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml.cs"));

        Assert.Contains("RecordingShimmerEdgeColor", xaml, StringComparison.Ordinal);
        Assert.Contains("RecordingShimmerCoreColor", xaml, StringComparison.Ordinal);
        Assert.Contains("LinearGradientBrush.RelativeTransform", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "(Shape.Fill).(Brush.RelativeTransform).(TranslateTransform.X)",
            codeBehind,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 流光要像主界面录制卡片那样铺满整条状态区（连同单号一起扫过），
    /// 而不是退化成一根独立的进度条。
    /// </summary>
    [Fact]
    public void FloatingPreview_ShimmerCoversWholeStatusBar_NotAProgressBar()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml"));

        // 进度条式实现会把流光放进独立的固定高度轨道；
        // 铺满整条则是覆盖状态区并用负外边距抵掉 Padding。
        Assert.DoesNotContain("ShimmerTrack", xaml, StringComparison.Ordinal);

        int shimmerIndex = xaml.IndexOf("x:Name=\"ShimmerLayer\"", StringComparison.Ordinal);
        Assert.True(shimmerIndex >= 0, "未找到流光层");

        int elementEnd = xaml.IndexOf('>', shimmerIndex);
        string shimmerElement = xaml[shimmerIndex..elementEnd];

        Assert.Matches(@"Margin=""-\d+,-\d+""", shimmerElement);
        Assert.DoesNotContain("Height=", shimmerElement, StringComparison.Ordinal);
    }

    /// <summary>悬停控制层不能给预览加黑色蒙版，否则遮挡监控画面。</summary>
    [Fact]
    public void FloatingPreview_HoverLayerHasNoScrim()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml"));

        int layerIndex = xaml.IndexOf("x:Name=\"ControlLayer\"", StringComparison.Ordinal);
        Assert.True(layerIndex >= 0, "未找到悬停控制层");

        int layerEnd = xaml.IndexOf("</Grid>", layerIndex, StringComparison.Ordinal);
        string layer = layerEnd >= 0 ? xaml[layerIndex..layerEnd] : xaml[layerIndex..];

        Assert.DoesNotContain("OverlaySoft", layer, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayMedium", layer, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayStrong", layer, StringComparison.Ordinal);
    }

    /// <summary>小窗要跟随主题，不允许把背景、边框、文字写成固定十六进制颜色。</summary>
    [Fact]
    public void FloatingPreview_UsesThemeResourcesInsteadOfHardcodedColors()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml"));

        string[] hardcoded = xaml
            .Split('\n')
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, "\"#[0-9A-Fa-f]{6,8}\""))
            .Select(line => line.Trim())
            .ToArray();

        Assert.True(
            hardcoded.Length == 0,
            "悬浮小窗出现写死颜色，应改用主题资源：" + Environment.NewLine + string.Join(Environment.NewLine, hardcoded));
    }

    /// <summary>四个角对应回到主界面、关闭小窗、选择麦克风、选择播放设备。</summary>
    [Fact]
    public void FloatingPreview_ExposesFourCornerActions()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml"));

        foreach (string handler in new[]
                 {
                     "RestoreButton_Click",
                     "CloseButton_Click",
                     "MicrophoneButton_Click",
                     "SpeakerButton_Click"
                 })
        {
            Assert.Contains(handler, xaml, StringComparison.Ordinal);
        }
    }

    /// <summary>右上角的叉只关小窗，不能顺手把主界面弹回来打断店员。</summary>
    [Fact]
    public void CloseButton_DoesNotRestoreMainWindow()
    {
        string codeBehind = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml.cs"));

        int index = codeBehind.IndexOf("private void CloseButton_Click", StringComparison.Ordinal);
        Assert.True(index >= 0, "未找到关闭小窗的处理方法");

        string body = codeBehind.Substring(index, Math.Min(240, codeBehind.Length - index));
        Assert.DoesNotContain("RestoreMainWindowAndClose", body, StringComparison.Ordinal);
        Assert.Contains("CloseFromOwner", body, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

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
