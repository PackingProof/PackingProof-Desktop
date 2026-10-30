using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.LensControl;
using ExpressPackingMonitoring.Services.MediaFoundation;
using ExpressPackingMonitoring.ViewModels;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 镜头焦距控制的判定规则：面板可见性、启动恢复计划、滑条初值。
/// 设备交互（IAMCameraControl）没法在无硬件环境下单测，
/// 这里覆盖的是两边都要遵守的纯策略。
/// </summary>
public sealed class CameraLensPolicyTests
{
    /// <summary>汇博士 W7 一类带马达镜头的机位：变焦 + 全模式对焦。</summary>
    private static readonly LensRange ZoomRange = new(100, 400, 10, 100, LensControlFlags.Manual);

    private static readonly LensRange FocusRange = new(
        0,
        65000,
        65,
        32500,
        LensControlFlags.Auto | LensControlFlags.Manual);

    /// <summary>普通定焦摄像头：对焦只有自动，没有变焦 —— 不该出现镜头面板。</summary>
    private static readonly LensRange AutoOnlyFocusRange = new(0, 65000, 65, 32500, LensControlFlags.Auto);

    [Fact]
    public void PanelRequiresManualZoomOrManualFocus()
    {
        Assert.True(CameraLensPolicy.ShouldShowControls(ZoomRange, FocusRange));
        Assert.True(CameraLensPolicy.ShouldShowControls(ZoomRange, null));
        Assert.True(CameraLensPolicy.ShouldShowControls(null, FocusRange));
        Assert.False(CameraLensPolicy.ShouldShowControls(null, AutoOnlyFocusRange));
        Assert.False(CameraLensPolicy.ShouldShowControls(null, null));
    }

    [Fact]
    public void DegenerateRangesDoNotShowPanel()
    {
        var invertedZoom = new LensRange(400, 100, 10, 100, LensControlFlags.Manual);
        Assert.False(CameraLensPolicy.ShouldShowControls(invertedZoom, null));
    }

    [Fact]
    public void StartupPlan_NoStoredSettings_DoesNothing()
    {
        Assert.True(CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, null).IsEmpty);
        Assert.True(CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, new CameraLensSettings()).IsEmpty);
    }

    [Fact]
    public void StartupPlan_StoredZoom_IsAppliedAndClamped()
    {
        var stored = new CameraLensSettings { Zoom = 250 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.Equal(250, plan.Zoom);
        Assert.Null(plan.FocusAuto);
        Assert.False(plan.IsEmpty);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(99999, 400)]
    public void StartupPlan_ClampsZoomIntoDeviceRange(int stored, int expected)
    {
        var storedSettings = new CameraLensSettings { Zoom = stored };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, storedSettings);
        Assert.Equal(expected, plan.Zoom);
    }

    [Fact]
    public void StartupPlan_ManualFocus_RestoresModeAndPosition()
    {
        var stored = new CameraLensSettings { FocusAuto = false, Focus = 12000 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.False(plan.FocusAuto);
        Assert.Equal(12000, plan.FocusManual);
    }

    [Fact]
    public void StartupPlan_ManualFocusWithoutPosition_RestoresModeOnly()
    {
        var stored = new CameraLensSettings { FocusAuto = false };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.False(plan.FocusAuto);
        Assert.Null(plan.FocusManual);
    }

    [Fact]
    public void StartupPlan_AutoFocus_WritesNothing()
    {
        // 自动是设备默认：恢复它反而可能在启动时打断摄像头自检
        var stored = new CameraLensSettings { FocusAuto = true, Focus = 12000 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        Assert.Null(plan.FocusAuto);
        Assert.Null(plan.FocusManual);
    }

    [Fact]
    public void StartupPlan_IgnoresRangesDeviceDoesNotSupport()
    {
        // 摄像头只有对焦（或只有变焦）：另一项记忆再完整也不能下发
        var stored = new CameraLensSettings { Zoom = 250, FocusAuto = false, Focus = 12000 };

        CameraLensApplyPlan zoomOnly = CameraLensPolicy.BuildStartupPlan(ZoomRange, null, stored);
        Assert.Equal(250, zoomOnly.Zoom);
        Assert.Null(zoomOnly.FocusAuto);

        CameraLensApplyPlan autoFocusOnly = CameraLensPolicy.BuildStartupPlan(null, AutoOnlyFocusRange, stored);
        Assert.True(autoFocusOnly.IsEmpty);
    }

    [Fact]
    public void StartupPlan_SkipsValuesWhoseCurrentStateCannotBeRead()
    {
        var stored = new CameraLensSettings { Zoom = 250, FocusAuto = false, Focus = 12000 };
        CameraLensApplyPlan plan = CameraLensPolicy.BuildStartupPlan(ZoomRange, FocusRange, stored);

        CameraLensApplyPlan zoomUnknown = CameraLensPolicy.RequireReadableStateForStartup(
            plan,
            ZoomRange,
            FocusRange,
            currentZoom: null,
            currentFocus: (12000, false));
        Assert.Null(zoomUnknown.Zoom);
        Assert.False(zoomUnknown.FocusAuto);
        Assert.Equal(12000, zoomUnknown.FocusManual);

        CameraLensApplyPlan focusUnknown = CameraLensPolicy.RequireReadableStateForStartup(
            plan,
            ZoomRange,
            FocusRange,
            currentZoom: 250,
            currentFocus: null);
        Assert.Equal(250, focusUnknown.Zoom);
        Assert.Null(focusUnknown.FocusAuto);
        Assert.Null(focusUnknown.FocusManual);
    }

    [Fact]
    public void StartupRestore_RefreshesUiFromDeviceAfterWriting()
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var controller = new CountingLensController();
        var stored = new CameraLensSettings { Zoom = 320, FocusAuto = false, Focus = 12000 };

        MainViewModel.LensUiSnapshot snapshot = viewModel.ApplyLensStartupPlan(controller, stored);

        Assert.Equal(1, controller.ZoomWrites);
        Assert.Equal(1, controller.FocusWrites);
        Assert.Equal(320, snapshot.ZoomValue);
        Assert.Equal((12000, false), snapshot.FocusState);
    }

    [Fact]
    public void ZoomDisplayValue_PrefersDeviceThenStoredThenDefault()
    {
        Assert.Equal(320, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, 320, CameraLensSettings.Unset));
        Assert.Equal(250, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, null, 250));
        Assert.Equal(100, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, null, CameraLensSettings.Unset));
        Assert.Equal(400, CameraLensPolicy.ResolveZoomDisplayValue(ZoomRange, 99999, CameraLensSettings.Unset));
    }

    [Fact]
    public void FocusDisplayValue_PrefersDeviceThenStoredThenDefault()
    {
        Assert.Equal(1000, CameraLensPolicy.ResolveFocusDisplayValue(FocusRange, 1000, CameraLensSettings.Unset));
        Assert.Equal(2000, CameraLensPolicy.ResolveFocusDisplayValue(FocusRange, null, 2000));
        Assert.Equal(32500, CameraLensPolicy.ResolveFocusDisplayValue(FocusRange, null, CameraLensSettings.Unset));
    }

    [Fact]
    public void ManualFocusRequiresManualCapableRange()
    {
        Assert.True(CameraLensPolicy.SupportsManualFocus(FocusRange));
        Assert.False(CameraLensPolicy.SupportsManualFocus(AutoOnlyFocusRange));
        Assert.False(CameraLensPolicy.SupportsManualFocus(null));
    }

    [Fact]
    public void LensControls_RequireReadableCurrentDeviceState()
    {
        Assert.True(CameraLensPolicy.CanOperateZoom(ZoomRange, 250));
        Assert.False(CameraLensPolicy.CanOperateZoom(ZoomRange, null));
        Assert.True(CameraLensPolicy.CanOperateFocus(FocusRange, (1000, true)));
        Assert.False(CameraLensPolicy.CanOperateFocus(FocusRange, null));
    }

    [Fact]
    public void LensPanel_UsesExplicitFocusModeButtons()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"LensFocusAutoButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LensFocusManualButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding SetLensFocusAutoCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding SetLensFocusManualCommand}\"",
            xaml,
            StringComparison.Ordinal);
        int focusButtonsStart = xaml.IndexOf("x:Name=\"LensFocusAutoButton\"", StringComparison.Ordinal);
        int focusButtonsEnd = xaml.IndexOf("</Grid>", focusButtonsStart, StringComparison.Ordinal);
        Assert.True(focusButtonsStart >= 0 && focusButtonsEnd > focusButtonsStart);
        string focusButtons = xaml[focusButtonsStart..focusButtonsEnd];
        Assert.Contains("Grid.Column=\"1\"", focusButtons, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,0,4,0\"", focusButtons, StringComparison.Ordinal);
        Assert.Contains("Margin=\"4,0,0,0\"", focusButtons, StringComparison.Ordinal);
        int farLabelIndex = xaml.IndexOf("x:Name=\"LensFocusFarLabel\"", StringComparison.Ordinal);
        int nearLabelIndex = xaml.IndexOf("x:Name=\"LensFocusNearLabel\"", StringComparison.Ordinal);
        Assert.True(farLabelIndex >= 0 && nearLabelIndex > farLabelIndex);
        Assert.Contains(
            "Text=\"{loc:Localized 远}\"",
            xaml[farLabelIndex..(farLabelIndex + 180)],
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{loc:Localized 近}\"",
            xaml[nearLabelIndex..(nearLabelIndex + 180)],
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding IsLensZoomControlEnabled, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding IsLensFocusControlEnabled, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsChecked=\"{Binding LensFocusAuto",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LensPanel_DoesNotWriteWithoutReadableDeviceState()
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var controller = new CountingLensController();
        SetPrivateField(viewModel, "_lensControlAvailable", true);
        SetPrivateField(viewModel, "_lensZoomControlEnabled", false);
        SetPrivateField(viewModel, "_lensFocusControlEnabled", false);
        SetPrivateField(viewModel, "_lensController", controller);

        viewModel.LensZoomValue = 320;
        viewModel.LensFocusValue = 12000;
        viewModel.LensFocusAuto = false;

        viewModel.ToggleLensPanel();
        Assert.True(viewModel.IsLensPanelOpen);
        viewModel.ToggleLensPanel();

        Assert.False(viewModel.IsLensPanelOpen);
        Assert.Equal(0, controller.ZoomWrites);
        Assert.Equal(0, controller.FocusWrites);
    }

    [Fact]
    public async Task RefreshingLensPanel_ReenablesControlsAfterTransientReadFailure()
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var controller = new CountingLensController();
        SetPrivateField(viewModel, "_lensController", controller);
        SetPrivateField(viewModel, "_lensZoomControlEnabled", false);
        SetPrivateField(viewModel, "_lensFocusControlEnabled", false);
        SetPrivateField(viewModel, "_lensStored", new CameraLensSettings());

        await viewModel.RefreshLensControlStateAsync();

        Assert.True(viewModel.IsLensZoomControlEnabled);
        Assert.True(viewModel.IsLensFocusControlEnabled);
        Assert.Equal("", viewModel.LensStatusText);
    }

    [Fact]
    public async Task RefreshingLensPanel_DoesNotOverwriteANewerZoomOperation()
    {
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        using var readStarted = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var controller = new CountingLensController
        {
            ZoomReadStarted = readStarted,
            ZoomReadRelease = releaseRead
        };
        SetPrivateField(viewModel, "_lensController", controller);
        SetPrivateField(viewModel, "_lensZoomControlEnabled", true);
        SetPrivateField(viewModel, "_lensFocusControlEnabled", false);
        SetPrivateField(viewModel, "_lensZoomValue", 200d);
        SetPrivateField(viewModel, "_lensStored", new CameraLensSettings());
        SetPrivateField(viewModel, "_lensZoomApplyVersion", 1L);

        Task refresh = viewModel.RefreshLensControlStateAsync();
        Assert.True(readStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        viewModel.LensZoomValue = 350;
        releaseRead.Set();
        await refresh;

        Assert.Equal(350, viewModel.LensZoomValue);
        Assert.True(viewModel.IsLensZoomControlEnabled);
        Assert.True(viewModel.IsLensFocusControlEnabled);
    }

    [Theory]
    [InlineData(0, 0, 40, LensControlFlags.Manual, 40, LensControlFlags.Manual, 1, true)]
    [InlineData(0, 0, 40, LensControlFlags.Manual, 40, LensControlFlags.Auto, 1, false)]
    [InlineData(0, 0, 40, LensControlFlags.Manual, 35, LensControlFlags.Manual, 1, false)]
    [InlineData(0, 0, 32500, LensControlFlags.Auto, 123, LensControlFlags.Auto, 65, true)]
    [InlineData(unchecked((int)0x80004005), 0, 40, LensControlFlags.Manual, 40, LensControlFlags.Manual, 1, false)]
    [InlineData(0, unchecked((int)0x80004005), 40, LensControlFlags.Manual, 40, LensControlFlags.Manual, 1, false)]
    public void LensWriteVerifier_RequiresRequestedModeAndValue(
        int setResult,
        int readResult,
        int requestedValue,
        int requestedFlags,
        int readValue,
        int readFlags,
        int step,
        bool expected)
    {
        Assert.Equal(
            expected,
            LensControlWriteVerifier.IsApplied(
                setResult,
                readResult,
                requestedValue,
                requestedFlags,
                readValue,
                readFlags,
                step));
    }

    [Fact]
    public void RawCameraControl_ReleasesComInterfaceOnce()
    {
        int releaseCount = 0;
        ReleaseDelegate release = _ =>
        {
            Interlocked.Increment(ref releaseCount);
            return 0;
        };

        IntPtr vtable = Marshal.AllocHGlobal(6 * IntPtr.Size);
        IntPtr instance = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            for (int index = 0; index < 6; index++)
            {
                Marshal.WriteIntPtr(vtable, index * IntPtr.Size, IntPtr.Zero);
            }

            IntPtr releaseAddress = Marshal.GetFunctionPointerForDelegate(release);
            Marshal.WriteIntPtr(vtable, 2 * IntPtr.Size, releaseAddress);
            Marshal.WriteIntPtr(instance, vtable);

            var control = new RawCameraControl(instance);
            control.Dispose();
            control.Dispose();

            Assert.Equal(1, releaseCount);
            GC.KeepAlive(release);
        }
        finally
        {
            Marshal.FreeHGlobal(instance);
            Marshal.FreeHGlobal(vtable);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReleaseDelegate(IntPtr self);

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Repository file not found: {Path.Combine(relativeParts)}");
    }

    private static void SetPrivateField<T>(MainViewModel viewModel, string name, T value)
    {
        FieldInfo field = typeof(MainViewModel).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainViewModel).FullName, name);
        field.SetValue(viewModel, value);
    }

    private sealed class CountingLensController : ILensController
    {
        private int _zoom = 250;
        private int _focus = 1000;
        private bool _focusAuto = true;

        public ManualResetEventSlim? ZoomReadStarted { get; init; }
        public ManualResetEventSlim? ZoomReadRelease { get; init; }

        public LensRange? ZoomRange => CameraLensPolicyTests.ZoomRange;

        public LensRange? FocusRange => CameraLensPolicyTests.FocusRange;

        public bool IsUsable => true;

        public int ZoomWrites { get; private set; }

        public int FocusWrites { get; private set; }

        public int? TryReadZoom()
        {
            ZoomReadStarted?.Set();
            ZoomReadRelease?.Wait();
            return _zoom;
        }

        public (int Value, bool Auto)? TryReadFocusState() => (_focus, _focusAuto);

        public bool TrySetZoom(int value)
        {
            ZoomWrites++;
            _zoom = value;
            return true;
        }

        public bool TrySetFocusManual(int value)
        {
            FocusWrites++;
            _focus = value;
            _focusAuto = false;
            return true;
        }

        public bool TrySetFocusAuto()
        {
            _focusAuto = true;
            return true;
        }
    }
}
