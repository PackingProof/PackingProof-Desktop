using System.Windows.Controls;
using System.Text;
using System.Windows;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 面单放大：智能模式跟随本次选中的单个 OBB，没有可用坐标时回退到固定取景框。
/// 这一组守卫同时盯住智能目标、编辑态和放大与副画面合成的互斥。
/// </summary>
public sealed class ZoomGuideEditingTests
{
    [Fact]
    public void ZoomBranch_UsesSelectedObbWithGuideFallback()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));
        string zoom = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Zoom.cs"));

        Assert.Contains("ZoomCropPolicy.ResolveScale", zoom, StringComparison.Ordinal);
        Assert.Contains("_smartZoomObbTarget", zoom, StringComparison.Ordinal);
        Assert.Contains("_smartZoomObbTarget = ResolveSmartZoomTarget()", zoom, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_smartZoomObbTarget = CameraVisionSmartZoomTarget",
            zoom,
            StringComparison.Ordinal);
        Assert.Contains("VisionSmartZoomPolicy.ResolveTargetRect", zoom, StringComparison.Ordinal);
        Assert.Contains("CameraVisionSmartZoomTarget is not { } detection", zoom, StringComparison.Ordinal);
        Assert.DoesNotContain("Config.CameraBarcodeRecognitionChannel != 0", zoom, StringComparison.Ordinal);
        Assert.Contains("ZoomGuideGeometry", zoom, StringComparison.Ordinal);
        Assert.Contains("CameraBarcodeGuideLayout.ToDisplayRect", zoom, StringComparison.Ordinal);
        Assert.Contains("ZoomCropPolicy.CreateCropRect", zoom, StringComparison.Ordinal);
        // 倍率只有取景框这一个来源：不能再有第二个倍率参数
        Assert.DoesNotContain("MaxZoomScale", zoom, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewZoomScale", zoom, StringComparison.Ordinal);
        // 使用专门的 OBB 选中框，不再引用按条码结果二次估算的旧几何。
        Assert.DoesNotContain("_lastBarcodeGeometry", zoom, StringComparison.Ordinal);
        // 主循环只问"这一帧该用哪份画面"，放大细节都在独立分部里
        Assert.Contains("ApplyZoomToFrame(currentFrame, currentFrameSequence)", camera, StringComparison.Ordinal);

        string scanner = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Scanner.cs"));
        Assert.Contains("StartZoomForScan()", scanner, StringComparison.Ordinal);
        Assert.DoesNotContain("useVisionObbTarget", scanner, StringComparison.Ordinal);
        Assert.Contains("CanSubmitCameraVisionFrame", scanner, StringComparison.Ordinal);
        Assert.Contains("cameraBarcodeDecodingEnabledProvider", scanner, StringComparison.Ordinal);
        Assert.Contains("visionObbDecodingEnabledProvider", scanner, StringComparison.Ordinal);
        Assert.Contains("updateVision: ShouldRunVisionObb", scanner, StringComparison.Ordinal);
        Assert.DoesNotContain("forceDecode:", scanner, StringComparison.Ordinal);
        Assert.Contains("releaseVisionRuntime: !ShouldRunVisionObb", scanner, StringComparison.Ordinal);
        Assert.Contains("_cameraVisionSmartZoomRecognition", scanner, StringComparison.Ordinal);
        Assert.Contains("TrySubmitCameraSmartZoomFrame(currentFrame)", camera, StringComparison.Ordinal);
        string overlayBoxes = ReadProjectFile(Path.Combine("UI", "MainWindow.CameraVisionObb.cs"));
        Assert.Contains(
            "!vm.ShouldRunVisionObb && !vm.ShouldRunSeparateCameraSmartZoom",
            overlayBoxes,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "|| vm.ShouldUseOverlayChannelForBarcode",
            overlayBoxes,
            StringComparison.Ordinal);
        string service = ReadProjectFile(Path.Combine(
            "Services",
            "CameraBarcodeRecognitionService.cs"));
        Assert.Contains("_visionProcessor.ReleaseDetector()", service, StringComparison.Ordinal);
    }

    /// <summary>设置页里不该再有"最大放大倍数"这类第二个倍率参数。</summary>
    [Fact]
    public void SettingsPage_NoLongerExposesASecondZoomScaleParameter()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        Assert.DoesNotContain("最大放大倍数", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ZoomScaleSlider", xaml, StringComparison.Ordinal);

        string config = ReadProjectFile(Path.Combine("Config", "AppConfig.cs"));
        Assert.DoesNotContain("MaxZoomScale", config, StringComparison.Ordinal);

        string context = ReadProjectFile(Path.Combine("UI", "SettingsContext.cs"));
        Assert.DoesNotContain("SetPreviewZoomScale", context, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideEditing_SuspendsZoomAndHidesRecognitionGuide()
    {
        string guide = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.CameraGuide.cs"));

        Assert.Contains("public bool IsEditingZoomGuide", guide, StringComparison.Ordinal);
        Assert.Contains("IsEditingZoomGuide", guide, StringComparison.Ordinal);
        Assert.Contains("internal void EnterZoomGuideEdit()", guide, StringComparison.Ordinal);
        Assert.Contains("internal void ExitZoomGuideEdit()", guide, StringComparison.Ordinal);
        // 编辑屏里框一定可拖，且写回的是放大取景框那一组配置
        Assert.Contains("ApplyZoomGuideGeometry", guide, StringComparison.Ordinal);
        Assert.Contains("Config.ZoomGuideWidthRatio", guide, StringComparison.Ordinal);
        // 暂停放大用同一个策略入口，编辑态不能漏
        Assert.Contains("IsEditingZoomGuide);", guide, StringComparison.Ordinal);
        Assert.Contains("nameof(IsCameraBarcodeGuideLockVisible)", guide, StringComparison.Ordinal);
        Assert.Contains("nameof(IsCameraBarcodeGuideEditable)", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// 放大状态只有一个来源（相位），界面用的"正在放大"由它派生；起放大只有一个入口，
    /// 打断放大（停录、进"调放大位置"）都走统一复位 —— 否则界面两层框会被残留状态一直藏住。
    /// </summary>
    [Fact]
    public void ZoomStateHasOneSourceAndEveryAbortPathResetsIt()
    {
        string viewModel = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.cs"));
        string zoom = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Zoom.cs"));
        string scanner = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Scanner.cs"));
        string recording = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Recording.cs"));
        string guide = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.CameraGuide.cs"));

        // 唯一状态源：相位。界面标志由它派生，历史包袱字段不许再回来
        Assert.Contains(
            "private enum ZoomPhase { None, Waiting, ZoomingIn, Holding, ZoomingOut }",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("public bool IsZoomingActive =>", viewModel, StringComparison.Ordinal);
        foreach (string legacyField in new[]
                 {
                     "_isScanning", "_delayBeforeZooming", "_lastScanTime", "_isZoomingActive", "LastZoomRect"
                 })
        {
            Assert.DoesNotContain(legacyField, viewModel, StringComparison.Ordinal);
        }

        // 相位步进只走一个出口：值真的翻转时才通知界面
        int setter = zoom.IndexOf("private void SetZoomPhase(ZoomPhase phase)", StringComparison.Ordinal);
        Assert.True(setter > 0, "没找到 SetZoomPhase");
        string setterBody = zoom[setter..Math.Min(zoom.Length, setter + 500)];
        Assert.Contains("_zoomPhase = phase;", setterBody, StringComparison.Ordinal);
        Assert.Contains("nameof(IsZoomingActive)", setterBody, StringComparison.Ordinal);
        Assert.Contains("nameof(IsCameraBarcodeGuideEditable)", setterBody, StringComparison.Ordinal);

        // 起放大只在扫码与手动开录两处，且都走同一个入口，不再各自写一遍状态
        Assert.Equal(2, CountOccurrences(scanner, "StartZoomForScan()"));
        Assert.DoesNotContain("_zoomPhase", scanner, StringComparison.Ordinal);

        // 停录会打断正在跑的放大：这里必须走同一个复位
        int stop = recording.IndexOf("private async Task InternalStopRecordingAsync()", StringComparison.Ordinal);
        Assert.True(stop > 0, "没找到 InternalStopRecordingAsync");
        Assert.Contains(
            "ResetZoomState();",
            recording[stop..Math.Min(recording.Length, stop + 1000)],
            StringComparison.Ordinal);

        // 进"调放大位置"也走同一个复位
        int editing = guide.IndexOf("private void StopZoomForGuideEditing()", StringComparison.Ordinal);
        Assert.True(editing > 0, "没找到 StopZoomForGuideEditing");
        Assert.Contains(
            "ResetZoomState();",
            guide[editing..Math.Min(guide.Length, editing + 200)],
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        for (int index = source.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void ZoomAnimation_LocksTargetAndUsesPhaseEasing()
    {
        string zoom = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Zoom.cs"));

        Assert.Contains("double eased = SmoothStep(t);", zoom, StringComparison.Ordinal);
        Assert.Contains("panProgress = eased;", zoom, StringComparison.Ordinal);
        Assert.Contains("panProgress = 1.0 - eased;", zoom, StringComparison.Ordinal);
        Assert.Contains("ZoomCropPolicy.ResolvePanCenter", zoom, StringComparison.Ordinal);
        Assert.DoesNotContain("_smartZoomObbTarget = CameraVisionSmartZoomTarget", zoom, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomFade_FadesOverlayInSyncWithTheZoomAnimation()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));
        string zoom = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Zoom.cs"));

        // 淡出系数与缩放共用同一条缓动：放大时 100 → 0，停留 0，还原 0 → 100
        Assert.Contains("if (Config.HideOverlayDuringZoom)", zoom, StringComparison.Ordinal);
        Assert.Contains("overlayFadePercent = 100.0 * (1.0 - eased);", zoom, StringComparison.Ordinal);
        Assert.Contains("overlayFadePercent = 0.0;", zoom, StringComparison.Ordinal);
        Assert.Contains("overlayFadePercent = 100.0 * eased;", zoom, StringComparison.Ordinal);
        // 合成要吃到这个系数，预录回灌那条路也要用同一个值
        Assert.Contains("_overlayZoomFadePercent", zoom, StringComparison.Ordinal);
        Assert.Contains("ComposeOverlayChannelsIfNeeded(", camera, StringComparison.Ordinal);

        string recording = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Recording.cs"));
        Assert.Contains("ComposeOverlayChannels(storedFrame, _overlayZoomFadePercent)", recording, StringComparison.Ordinal);

        string composer = ReadProjectFile(Path.Combine("ViewModels", "CameraOverlayComposer.cs"));
        Assert.Contains("overlayOpacityPercent", composer, StringComparison.Ordinal);
        // 带蒙版拷贝对 8 位蒙版是"非零即拷贝"的硬拷贝：透明度必须用加权算，蒙版只负责圆角形状
        Assert.Contains("Cv2.AddWeighted(region", composer, StringComparison.Ordinal);
        Assert.Contains("blended.CopyTo(region, mask)", composer, StringComparison.Ordinal);

        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        Assert.Contains("{Binding Config.HideOverlayDuringZoom}", xaml, StringComparison.Ordinal);

        // 淡出时长复用"过渡时长"（animDuration），不许再冒出一个自己的时长参数：
        // 关掉平滑过渡时淡出也跟着瞬切，两边的节奏始终一致。
        int durationIndex = zoom.IndexOf(
            "double animDuration = Config.EnableZoomAnimation ? Config.ZoomAnimationDurationMs : 0;",
            StringComparison.Ordinal);
        Assert.True(durationIndex > 0, "没找到放大过渡时长的计算");
        int fadeCommitIndex = zoom.IndexOf("_overlayZoomFadePercent =", durationIndex, StringComparison.Ordinal);
        Assert.True(fadeCommitIndex > durationIndex);
        Assert.DoesNotContain("FadeDuration", zoom, StringComparison.Ordinal);
        Assert.DoesNotContain("FadeSeconds", zoom, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideEditing_SuppressesOverlayComposition()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        // 副画面小窗会挡住主画面，摆放大位置时先不合成；退出后自动恢复
        Assert.Contains("if (IsEditingZoomGuide)", channels, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShowsZoomGuideBoxAndHidesItWhileEditing()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"ZoomGuideBoxHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ZoomGuideBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource ZoomGuideStroke}", xaml, StringComparison.Ordinal);
        // 完成按钮两种预览编辑态都要显示
        Assert.Contains("{Binding IsPreviewGuideEditing}", xaml, StringComparison.Ordinal);
        // 编辑态下这一屏显示的是放大取景框
        Assert.Contains("{Binding IsEditingZoomGuide}", xaml, StringComparison.Ordinal);

        string code = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.Contains("UpdateZoomGuideBox", code, StringComparison.Ordinal);
        // 副摄取景编辑与调放大位置这两屏都不画放大取景框
        Assert.Contains("!vm.IsPreviewGuideEditing", code, StringComparison.Ordinal);
        Assert.Contains("ExitPreviewGuideEditing", code, StringComparison.Ordinal);
        // Esc 与"完成"等效，两种编辑态都要能退出
        Assert.Contains("vm.IsEditingOverlayPreview || vm.IsEditingZoomGuide", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 放大进行中不画放大取景框：预览已被裁切拉回整屏，框却按未放大的坐标画，位置是错的。
    /// </summary>
    [Fact]
    public void ZoomEffect_HidesTheRecognitionGuideAndItsHints()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        // 识别框与它的提示层都按"未放大的画面坐标"摆放，放大期间留着会明显错位，必须一起收起
        int guideIndex = xaml.IndexOf("x:Name=\"CameraBarcodeGuide\"", StringComparison.Ordinal);
        int hintIndex = xaml.IndexOf("x:Name=\"CameraBarcodeGuideHintLayer\"", StringComparison.Ordinal);
        Assert.True(guideIndex > 0, "没找到识别框");
        Assert.True(hintIndex > guideIndex, "没找到识别框的提示层");

        string guideBlock = xaml[guideIndex..hintIndex];
        string hintBlock = xaml[hintIndex..Math.Min(xaml.Length, hintIndex + 3000)];
        Assert.Contains("{Binding IsZoomingActive, Mode=OneWay}", guideBlock, StringComparison.Ordinal);
        Assert.Contains("{Binding IsZoomingActive, Mode=OneWay}", hintBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideBox_IsHiddenWhileTheZoomEffectIsRunning()
    {
        string code = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));

        int method = code.IndexOf("private void UpdateZoomGuideBox", StringComparison.Ordinal);
        Assert.True(method > 0, "没找到放大取景框的绘制方法");
        string body = code[method..Math.Min(code.Length, method + 1200)];
        Assert.Contains("!vm.IsZoomingActive", body, StringComparison.Ordinal);
        // 放大开始/结束时也要立刻重画，不能等下一帧预览
        Assert.Contains(
            "nameof(MainViewModel.IsZoomingActive)",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideBox_UsesOneLiveSmartTargetWhenEnabled()
    {
        string code = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));

        Assert.Contains("CameraVisionSmartZoomTarget is { } smartTarget", code, StringComparison.Ordinal);
        Assert.Contains("VisionSmartZoomPolicy.ResolveTargetRect", code, StringComparison.Ordinal);
        Assert.Contains(
            "nameof(MainViewModel.CameraVisionSmartZoomTarget)",
            code,
            StringComparison.Ordinal);
        Assert.Contains("ShowZoomGuideBox: not false", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SmartZoomTargetBox", code, StringComparison.Ordinal);

        string animation = ReadProjectFile(Path.Combine(
            "UI",
            "MainWindow.ZoomGuideAnimation.cs"));
        Assert.Contains("CompositionTarget.Rendering", animation, StringComparison.Ordinal);
        Assert.Contains("ZoomGuideTargetStabilityMs = 180", animation, StringComparison.Ordinal);
        Assert.Contains("SmoothDampZoomGuideComponent", animation, StringComparison.Ordinal);
        Assert.Contains("SetZoomGuideTargetRect", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ZoomGuideBoxHost.RenderTransform = new TranslateTransform", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideAnimation_HasItsOwnContinuousMotionState()
    {
        double velocity = 0;
        double first = MainWindow.SmoothDampZoomGuideComponent(
            0,
            300,
            ref velocity,
            0.016);
        double second = MainWindow.SmoothDampZoomGuideComponent(
            first,
            300,
            ref velocity,
            0.016);

        Assert.True(first > 0);
        Assert.True(first < 300);
        Assert.True(second > first);
        Assert.True(second < 300);
    }

    [Fact]
    public void ZoomGuideAnimation_WaitsForFarTargetStability()
    {
        string animation = ReadProjectFile(Path.Combine(
            "UI",
            "MainWindow.ZoomGuideAnimation.cs"));
        Assert.Contains("_zoomGuidePendingRect", animation, StringComparison.Ordinal);
        Assert.Contains("ZoomGuideTargetStabilityMs", animation, StringComparison.Ordinal);
        Assert.Contains("IsSameZoomGuideTarget", animation, StringComparison.Ordinal);
        Assert.Contains("ZoomGuideMaxSpeed", animation, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideEditingScreen_DrawsTheMainFrameBoxInsteadOfTheRecognitionBox()
    {
        string code = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));

        // "框贴到画中画上"那条分支必须排除所有整屏编辑态：
        // 调放大位置时画的只能是主画面上的放大取景框，否则会画成识别框的样子。
        int branch = code.IndexOf("识别输入来自副摄", StringComparison.Ordinal);
        Assert.True(branch > 0, "没找到识别框贴画中画的分支");
        string branchBlock = code[branch..Math.Min(code.Length, branch + 700)];
        Assert.Contains("if (!vm.IsPreviewGuideEditing", branchBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayDragBoxes_AreHiddenWhileEditingAnyPreviewGuide()
    {
        string boxes = ReadProjectFile(Path.Combine("UI", "MainWindow.OverlayBoxes.cs"));

        Assert.Contains("if (vm.IsPreviewGuideEditing)", boxes, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomTab_KeepsTheAgreedRowOrderAndShortHints()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        int tabStart = xaml.IndexOf("x:Name=\"ZoomTabItem\"", StringComparison.Ordinal);
        int tabEnd = xaml.IndexOf("x:Name=\"StorageTabItem\"", StringComparison.Ordinal);
        Assert.True(tabStart > 0 && tabEnd > tabStart, "没找到面单放大栏");
        string tab = xaml[tabStart..tabEnd];

        // 行序：面单放大 → 智能跟随面单 → 显示放大取景框 → 放大取景框
        // → 放大时隐藏画中画 → 放大前等待 → … → 平滑过渡
        int zoomToggle = tab.IndexOf("x:Name=\"ZoomEnableCheckBox\"", StringComparison.Ordinal);
        int smartFollow = tab.IndexOf("x:Name=\"VisionObbSmartZoomCheckBox\"", StringComparison.Ordinal);
        int showBox = tab.IndexOf("x:Name=\"ShowZoomGuideBoxCheckBox\"", StringComparison.Ordinal);
        int adjustBox = tab.IndexOf("x:Name=\"BtnAdjustZoomGuide\"", StringComparison.Ordinal);
        int hideOverlay = tab.IndexOf("x:Name=\"HideOverlayDuringZoomCheckBox\"", StringComparison.Ordinal);
        int delay = tab.IndexOf("x:Name=\"ZoomDelaySlider\"", StringComparison.Ordinal);
        int smooth = tab.IndexOf("x:Name=\"ZoomAnimationCheckBox\"", StringComparison.Ordinal);
        Assert.True(zoomToggle > 0 && smartFollow > zoomToggle, "智能跟随面单应紧跟面单放大开关");
        Assert.True(showBox > smartFollow, "显示放大取景框应排在智能跟随面单之后");
        Assert.True(adjustBox > showBox, "放大取景框（调整放大位置）应排在显示放大取景框之后");
        Assert.True(hideOverlay > adjustBox, "放大时隐藏画中画应是这一栏的第 4 项");
        Assert.True(delay > hideOverlay && smooth > hideOverlay, "放大时隐藏画中画要排在放大前等待之前");

        // 副标题要短；隐藏画中画的说明指向平滑过渡
        Assert.Contains("放大取景框内的画面，停留一会儿后恢复全景", tab, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.EnableVisionObbSmartZoom}", tab, StringComparison.Ordinal);
        Assert.Contains("{Binding VisionObbSmartZoomHint}", tab, StringComparison.Ordinal);
        Assert.Contains("开启平滑过渡时有淡入淡出动画", tab, StringComparison.Ordinal);

        // 放大前等待/放大停留时间属于常用项：不打开高级设置也要显示
        foreach (string sliderName in new[] { "ZoomDelaySlider", "ZoomDurationSlider" })
        {
            int slider = tab.IndexOf($"x:Name=\"{sliderName}\"", StringComparison.Ordinal);
            Assert.True(slider > 0, $"没找到 {sliderName}");
            int rowStart = tab.LastIndexOf("<Grid Style=", slider, StringComparison.Ordinal);
            string row = tab[rowStart..slider];
            Assert.Contains("SettingRowStyle", row, StringComparison.Ordinal);
            Assert.DoesNotContain("AdvancedSettingRowStyle", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ZoomGuideColors_AreDeclaredAsTokens()
    {
        string tokens = ReadProjectFile(Path.Combine("Themes", "ColorTokens.xaml"));

        Assert.Contains("x:Key=\"ZoomGuideStroke\"", tokens, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ZoomGuideFill\"", tokens, StringComparison.Ordinal);
    }

    /// <summary>
    /// 设置页的入口与交接：有主画面才显示"调整放大位置"，点它先应用再关窗口，
    /// 框选完成自动回到"面单放大"那一栏。
    /// </summary>
    [Fact]
    public void SettingsPage_ProvidesZoomGuideEntryForMainPreviewHostsOnly()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        Assert.Contains("x:Name=\"ZoomTabItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("调整大小与位置…", xaml, StringComparison.Ordinal);
        Assert.Contains("默认放大取景框", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowZoomGuideBoxCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.ShowZoomGuideBox}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding CanAdjustZoomGuide", xaml, StringComparison.Ordinal);
        // 智能那套文案不得残留
        Assert.DoesNotContain("面单智能特写", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("识别面单位置后自动平移特写", xaml, StringComparison.Ordinal);

        string wiring = ReadProjectFile(Path.Combine("UI", "SettingsWindow.ZoomGuide.cs"));
        Assert.Contains("CanAdjustZoomGuide", wiring, StringComparison.Ordinal);
        Assert.Contains("await SaveAndApplyAsync()", wiring, StringComparison.Ordinal);
        Assert.Contains("RequestZoomGuideEdit", wiring, StringComparison.Ordinal);
        Assert.Contains("public void SelectZoomTab()", wiring, StringComparison.Ordinal);

        string context = ReadProjectFile(Path.Combine("UI", "SettingsContext.cs"));
        Assert.Contains("RequestZoomGuideEdit = mainViewModel.RequestZoomGuideEdit", context, StringComparison.Ordinal);

        string viewModel = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Settings.cs"));
        Assert.Contains("_zoomGuideEditRequested", viewModel, StringComparison.Ordinal);
        Assert.Contains("EnterZoomGuideEdit();", viewModel, StringComparison.Ordinal);
        Assert.Contains("settingsWin.SelectZoomTab();", viewModel, StringComparison.Ordinal);

        // 打印工位没有主画面：不给这个 action，入口自然隐藏
        string printWorkstation = ReadProjectFile(Path.Combine("Workstations", "PrintWorkstationWindow.xaml.cs"));
        Assert.DoesNotContain("RequestZoomGuideEdit", printWorkstation, StringComparison.Ordinal);

        // 退出放大框编辑后自动回设置页
        string window = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.Contains("vm.OpenZoomGuideSettings()", window, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring", relativePath),
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

/// <summary>
/// 进放大取景框编辑态的真实窗口行为：框要切到"放大取景框"几何、小锁要让开、
/// 提示要换成"拖动框调整放大位置"，退出后恢复到主摄识别框那一套。
/// </summary>
[Collection("WPF render tests")]
public sealed class ZoomGuideEditingRenderTests
{
    [Fact]
    public void EnteringZoomGuideEdit_SwitchesGuideBoxAndHidesLock()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.UpdateLayout();

            var vm = Assert.IsType<MainViewModel>(window.DataContext);
            Assert.False(vm.IsEditingZoomGuide);
            Assert.True(vm.IsCameraBarcodeGuideLockVisible);

            vm.EnterZoomGuideEdit();
            window.UpdateLayout();

            Assert.True(vm.IsEditingZoomGuide);
            Assert.True(vm.IsPreviewGuideEditing);
            Assert.False(vm.IsCameraBarcodeGuideLockVisible);
            Assert.Equal(vm.ZoomGuideGeometry, vm.CurrentCameraBarcodeGuideGeometry);
            Assert.Contains("放大位置", vm.CameraBarcodeStatusText, StringComparison.Ordinal);
            Assert.False(vm.IsZoomingActive);

            vm.ExitZoomGuideEdit();
            window.UpdateLayout();

            Assert.False(vm.IsEditingZoomGuide);
            Assert.True(vm.IsCameraBarcodeGuideLockVisible);
            window.Close();
        });
    }

    /// <summary>
    /// 配置里存的是原生画面坐标：改了主摄旋转角度后，识别框与放大框都要按新角度换算到帧坐标，
    /// 否则框会指到另一块区域（90° 时宽高比例还会整个颠倒）。
    /// </summary>
    [Fact]
    public void MainCameraGuides_FollowTheConfiguredRotation()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            var vm = Assert.IsType<MainViewModel>(window.DataContext);
            vm.Config.CameraRotationDegrees = 90;

            // 原生坐标 (0.5, 0.4, +0.5, -1)：顺时针 90° 后宽高互换、偏移整体转过去。
            var expected = new CameraBarcodeGuideGeometry(0.4, 0.5, 1, 0.5);

            vm.Config.CameraBarcodeGuideWidthRatio = 0.5;
            vm.Config.CameraBarcodeGuideHeightRatio = 0.4;
            vm.Config.CameraBarcodeGuideOffsetX = 0.5;
            vm.Config.CameraBarcodeGuideOffsetY = -1;
            Assert.Equal(expected, vm.CurrentCameraBarcodeGuideGeometry);

            // 放大框除了跟着转，还要收成相机画面的长宽比（宽高比例相等）。
            vm.Config.ZoomGuideWidthRatio = 0.5;
            vm.Config.ZoomGuideHeightRatio = 0.5;
            vm.Config.ZoomGuideOffsetX = 0.5;
            vm.Config.ZoomGuideOffsetY = -1;
            Assert.Equal(new CameraBarcodeGuideGeometry(0.5, 0.5, 1, 0.5), vm.ZoomGuideGeometry);

            window.Close();
        });
    }

    /// <summary>
    /// 放大框必须和相机分辨率同长宽比：框不是画面比例时，放大裁的那块会比框多出一圈，
    /// 于是"框住的地方填满画面"就变成了"框住的地方没放大到"。
    /// </summary>
    [Fact]
    public void ZoomGuide_IsNormalizedToTheCameraFrameAspect()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            var vm = Assert.IsType<MainViewModel>(window.DataContext);
            vm.Config.CameraRotationDegrees = 0;
            vm.Config.ZoomGuideWidthRatio = 0.5;
            vm.Config.ZoomGuideHeightRatio = 0.3;
            vm.Config.ZoomGuideOffsetX = 0.5;
            vm.Config.ZoomGuideOffsetY = 0;

            CameraBarcodeGuideGeometry guide = vm.ZoomGuideGeometry;

            Assert.Equal(guide.WidthRatio, guide.HeightRatio, 6);
            // 框住的这块要与"放大实际裁的那一块"重合，不多不少。
            Rect frame = new(0, 0, 1280, 720);
            Rect box = CameraBarcodeGuideLayout.ToDisplayRect(guide, frame);
            Rect crop = ZoomCropPolicy.CreateCropRect(
                1280,
                720,
                ZoomCropPolicy.ResolveScale(1280, 720, box),
                box);
            Assert.Equal(crop.X, box.X, 3);
            Assert.Equal(crop.Y, box.Y, 3);
            Assert.Equal(crop.Width, box.Width, 3);
            Assert.Equal(crop.Height, box.Height, 3);

            window.Close();
        });
    }

    /// <summary>
    /// "调整放大位置"这一行只在有主画面的宿主里出现：打印工位的上下文没有这个 action，
    /// 入口必须收起来，点了也没有主画面可去。
    /// </summary>
    [Fact]
    public void SettingsPage_ShowsZoomGuideRowOnlyWithMainPreviewHost()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var withMainPreview = new SettingsWindow(
                new MainViewModel(),
                new AppConfig { DeploymentPreset = DeploymentPresets.RecordingWorkstation },
                12d,
                "12%");
            withMainPreview.SelectZoomTab();
            Layout(withMainPreview);
            var withMainRow = FindZoomGuideRow(withMainPreview);
            Assert.Equal(Visibility.Visible, withMainRow.Visibility);
            Assert.True(withMainPreview.CanAdjustZoomGuide);
            withMainPreview.Close();

            // 打印/查看端那类宿主只给一份最小上下文：没有 RequestZoomGuideEdit
            var noMainPreviewContext = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(DeploymentPresets.ViewerClient),
                ApplyAsync = _ => Task.FromResult(true)
            };
            var withoutMainPreview = new SettingsWindow(
                noMainPreviewContext,
                new AppConfig { DeploymentPreset = DeploymentPresets.ViewerClient },
                12d,
                "12%");
            Layout(withoutMainPreview);
            var withoutMainRow = FindZoomGuideRow(withoutMainPreview);
            // 这栏本身对查看端是隐藏的（CanRecordPcVideo=false），所以这里断言驱动显隐的属性
            Assert.False(withoutMainPreview.CanAdjustZoomGuide);
            withoutMainPreview.Close();
        });
    }

    private static Grid FindZoomGuideRow(SettingsWindow window)
    {
        var button = Assert.IsType<Button>(window.FindName("BtnAdjustZoomGuide"));
        return Assert.IsType<Grid>(button.Parent);
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(1200, 900));
        window.Arrange(new Rect(0, 0, 1200, 900));
        window.UpdateLayout();
    }

    /// <summary>与 App.xaml 一致的合并顺序，模板里的 StaticResource 才解析得到。</summary>
    private static void LoadAppResources()
    {
        var merged = new ResourceDictionary();
        foreach (string file in new[]
                 {
                     "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
                     "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
                     "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
                 })
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        if (Application.Current != null)
            Application.Current.Resources = merged;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            throw new Xunit.Sdk.XunitException($"放大取景框编辑态渲染失败：{detail}");
        }
    }
}
