using System.Windows;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring;

internal sealed record WorkstationPurposeSummary(
    string Preset,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Capabilities);

public partial class WorkstationSelectionWindow : Window
{
    private readonly string _currentPreset;
    private readonly bool _confirmCurrentPreset;
    private readonly bool _hostUnavailable;
    private readonly IReadOnlyList<RoleCapabilities> _availableRoles;
    private readonly ProductFeatures _productFeatures;
    private bool? _recordOnThisComputer;
    private bool? _useAsStorageHost;

    public string? SelectedPreset { get; private set; }

    public WorkstationSelectionWindow(
        string? currentPreset = null,
        bool confirmCurrentPreset = false,
        bool hostUnavailable = false,
        RuntimeEnvironment? runtimeEnvironment = null)
        : this(
            currentPreset,
            confirmCurrentPreset,
            hostUnavailable,
            ProductCapabilityRuntime.AvailableRoles(runtimeEnvironment),
            ProductCapabilityRuntime.CurrentFeatures)
    {
    }

    internal WorkstationSelectionWindow(
        string? currentPreset,
        bool confirmCurrentPreset,
        bool hostUnavailable,
        IReadOnlyList<RoleCapabilities> availableRoles,
        ProductFeatures productFeatures)
    {
        ArgumentNullException.ThrowIfNull(availableRoles);
        ArgumentNullException.ThrowIfNull(productFeatures);
        _currentPreset = DeploymentPresets.IsKnown(currentPreset)
            ? DeploymentPresets.Normalize(currentPreset)
            : "";
        _confirmCurrentPreset = confirmCurrentPreset;
        _hostUnavailable = hostUnavailable;
        _availableRoles = availableRoles
            .Where(role => !hostUnavailable || !role.IsHost)
            .DistinctBy(role => role.DeploymentPreset)
            .ToArray();
        _productFeatures = productFeatures;

        InitializeComponent();
        DialogWindowChrome.UseCloseButtonOnly(this);
        RestoreCurrentAnswers();
        ApplyAvailability();
        if (_hostUnavailable)
        {
            HostUnavailableNotice.Visibility = Visibility.Visible;
            StorageYesChoice.IsEnabled = false;
            StorageNoChoice.IsChecked = true;
            _useAsStorageHost = false;
        }
        UpdateResult();
    }

    private void ApplyAvailability()
    {
        bool canRecord = _availableRoles.Any(role =>
            TryResolveAnswers(role.DeploymentPreset, out bool record, out _)
            && record);
        bool cannotRecord = _availableRoles.Any(role =>
            TryResolveAnswers(role.DeploymentPreset, out bool record, out _)
            && !record);
        bool canStore = _availableRoles.Any(role =>
            TryResolveAnswers(role.DeploymentPreset, out _, out bool store)
            && store);
        bool cannotStore = _availableRoles.Any(role =>
            TryResolveAnswers(role.DeploymentPreset, out _, out bool store)
            && !store);

        RecordYesChoice.IsEnabled = canRecord;
        RecordNoChoice.IsEnabled = cannotRecord;
        StorageYesChoice.IsEnabled = canStore && !_hostUnavailable;
        StorageNoChoice.IsEnabled = cannotStore;
        ApplyChoiceCopy();

        if (_availableRoles is [RoleCapabilities only]
            && TryResolveAnswers(
                only.DeploymentPreset,
                out bool onlyRecord,
                out bool onlyStore))
        {
            _recordOnThisComputer = onlyRecord;
            _useAsStorageHost = onlyStore;
            RecordYesChoice.IsChecked = onlyRecord;
            RecordNoChoice.IsChecked = !onlyRecord;
            StorageYesChoice.IsChecked = onlyStore;
            StorageNoChoice.IsChecked = !onlyStore;
        }
    }

    private void ApplyChoiceCopy()
    {
        var recordingInputs = new List<string>();
        if (_productFeatures.Inputs.Camera)
            recordingInputs.Add(AppLanguage.Get("摄像头"));
        if (_productFeatures.Inputs.Microphone)
            recordingInputs.Add(AppLanguage.Get("麦克风"));
        if (_productFeatures.Inputs.VisionObb)
            recordingInputs.Add(AppLanguage.Get("面单识别"));
        if (_productFeatures.Inputs.Scanner)
            recordingInputs.Add(AppLanguage.Get("扫码枪"));
        if (_productFeatures.Inputs.CameraBarcode)
            recordingInputs.Add(AppLanguage.Get("摄像头扫码"));
        if (_productFeatures.OrderReceiver)
            recordingInputs.Add(AppLanguage.Get("订单联动"));
        if (_productFeatures.Tts)
            recordingInputs.Add(AppLanguage.Get("语音提醒"));
        RecordYesDescriptionText.Text = _productFeatures.PhotoCapture
            && !_productFeatures.VideoRecording
            ? AppLanguage.Get("拍照并保存到本机资料库")
            : recordingInputs.Count > 0
                ? AppLanguage.Format(
                    "启用{0}",
                    string.Join(AppLanguage.Get("、"), recordingInputs))
                : AppLanguage.Get("当前软件未开放本机录像");

        RecordNoDescriptionText.Text = _productFeatures.MediaLibrary
            ? AppLanguage.Get("不初始化本机录像设备，仍可保存其他设备录像或只连接主机查看")
            : AppLanguage.Get("当前软件未开放不录像模式");

        StorageYesTitleText.Text = _productFeatures.NasArchive
            ? AppLanguage.Get("要，作为备份主机（支持 NAS）")
            : AppLanguage.Get("要，长期保存在这台电脑");
        var storageFeatures = new List<string>();
        if (_productFeatures.NasArchive)
            storageFeatures.Add(AppLanguage.Get("可配置 NAS／网络共享备份"));
        if (_productFeatures.MobileBackupReceive)
            storageFeatures.Add(AppLanguage.Get("可接收手机录像"));
        if (_productFeatures.ComputerBackupReceive)
            storageFeatures.Add(AppLanguage.Get("可接收其他电脑上传的录像"));
        StorageYesDescriptionText.Text = storageFeatures.Count > 0
            ? AppLanguage.Format(
                "这台电脑负责长期保存录像，{0}",
                string.Join(AppLanguage.Get("、"), storageFeatures))
            : AppLanguage.Get("这台电脑负责长期保存录像");

        StorageNoDescriptionText.Text = _productFeatures.LanCollaboration
            ? AppLanguage.Get("本机录像时作为“录像从机”，录像先保存在本机，再上传给备份主机；不录像时作为“查看端”，连接主机查看录像")
            : AppLanguage.Get("当前软件未开放录像从机或查看端");
    }

    private void RestoreCurrentAnswers()
    {
        if (!TryResolveAnswers(_currentPreset, out bool record, out bool store))
            return;

        _recordOnThisComputer = record;
        _useAsStorageHost = store;
        RecordYesChoice.IsChecked = record;
        RecordNoChoice.IsChecked = !record;
        StorageYesChoice.IsChecked = store;
        StorageNoChoice.IsChecked = !store;
    }

    private void RecordingChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (RecordYesChoice == null || RecordNoChoice == null)
            return;

        _recordOnThisComputer = RecordYesChoice.IsChecked == true;
        UpdateResult();
    }

    private void StorageChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (StorageYesChoice == null || StorageNoChoice == null)
            return;

        _useAsStorageHost = StorageYesChoice.IsChecked == true;
        UpdateResult();
    }

    private void UpdateResult()
    {
        if (ResultTitleText == null || ConfirmPurposeButton == null)
            return;

        bool complete = _recordOnThisComputer.HasValue && _useAsStorageHost.HasValue;
        if (complete)
        {
            string selected = ResolvePreset(
                _recordOnThisComputer.GetValueOrDefault(),
                _useAsStorageHost.GetValueOrDefault());
            complete = _availableRoles.Any(role =>
                string.Equals(
                    role.DeploymentPreset,
                    selected,
                    StringComparison.OrdinalIgnoreCase));
        }
        ConfirmPurposeButton.IsEnabled = complete;
        if (!complete)
        {
            ResultStateText.Text = AppLanguage.Get("选择结果");
            ResultTitleText.Text = AppLanguage.Get("请完成上面两个选择");
            ResultDescriptionText.Text = AppLanguage.Get("完成后会在这里显示最终用途和支持的能力");
            ResultCapabilitiesList.ItemsSource = null;
            ResultIcon.Data = (System.Windows.Media.Geometry)FindResource("FluentCheckIcon");
            return;
        }

        WorkstationPurposeSummary summary = GetPurposeSummary(
            _recordOnThisComputer.GetValueOrDefault(),
            _useAsStorageHost.GetValueOrDefault(),
            _productFeatures);
        bool unchanged = string.Equals(
            summary.Preset,
            _currentPreset,
            StringComparison.OrdinalIgnoreCase);
        ResultStateText.Text = AppLanguage.Get(
            unchanged
                ? "当前用途"
                : string.IsNullOrWhiteSpace(_currentPreset)
                    ? "将使用"
                    : "将切换到");
        ResultTitleText.Text = AppLanguage.Get(summary.DisplayName);
        ResultDescriptionText.Text = AppLanguage.Get(summary.Description);
        ResultCapabilitiesList.ItemsSource = summary.Capabilities
            .Select(AppLanguage.Get)
            .ToArray();
        ResultIcon.Data = (System.Windows.Media.Geometry)FindResource(
            summary.Preset switch
            {
                DeploymentPresets.RecordingHost => "FluentVideoIcon",
                DeploymentPresets.RecordingWorkstation => "FluentWifiIcon",
                DeploymentPresets.MobileBackupHost => "FluentDatabaseIcon",
                _ => "FluentPlayIcon"
            });
    }

    private void ConfirmPurpose_Click(object sender, RoutedEventArgs e)
    {
        if (!_recordOnThisComputer.HasValue || !_useAsStorageHost.HasValue)
            return;

        string selected = ResolvePreset(
            _recordOnThisComputer.Value,
            _useAsStorageHost.Value);
        if (_availableRoles.All(role =>
                !string.Equals(
                    role.DeploymentPreset,
                    selected,
                    StringComparison.OrdinalIgnoreCase)))
            return;
        if (string.Equals(selected, _currentPreset, StringComparison.OrdinalIgnoreCase))
        {
            if (!_confirmCurrentPreset)
            {
                DialogResult = false;
                return;
            }

            SelectedPreset = selected;
            DialogResult = true;
            return;
        }

        SelectedPreset = selected;
        DialogResult = true;
    }

    internal static string ResolvePreset(bool recordOnThisComputer, bool useAsStorageHost) =>
        (recordOnThisComputer, useAsStorageHost) switch
        {
            (true, true) => DeploymentPresets.RecordingHost,
            (true, false) => DeploymentPresets.RecordingWorkstation,
            (false, true) => DeploymentPresets.MobileBackupHost,
            _ => DeploymentPresets.ViewerClient
        };

    internal static bool TryResolveAnswers(
        string? preset,
        out bool recordOnThisComputer,
        out bool useAsStorageHost)
    {
        switch (DeploymentPresets.Normalize(preset))
        {
            case DeploymentPresets.RecordingHost:
                recordOnThisComputer = true;
                useAsStorageHost = true;
                return true;
            case DeploymentPresets.RecordingWorkstation:
                recordOnThisComputer = true;
                useAsStorageHost = false;
                return true;
            case DeploymentPresets.MobileBackupHost:
                recordOnThisComputer = false;
                useAsStorageHost = true;
                return true;
            case DeploymentPresets.ViewerClient:
                recordOnThisComputer = false;
                useAsStorageHost = false;
                return true;
            default:
                recordOnThisComputer = false;
                useAsStorageHost = false;
                return false;
        }
    }

    internal static WorkstationPurposeSummary GetPurposeSummary(
        bool recordOnThisComputer,
        bool useAsStorageHost) =>
        GetPurposeSummary(
            recordOnThisComputer,
            useAsStorageHost,
            ProductCapabilityRuntime.CurrentFeatures);

    internal static WorkstationPurposeSummary GetPurposeSummary(
        bool recordOnThisComputer,
        bool useAsStorageHost,
        ProductFeatures brandFeatures)
    {
        ArgumentNullException.ThrowIfNull(brandFeatures);
        string preset = ResolvePreset(recordOnThisComputer, useAsStorageHost);
        EffectiveProductCapabilities capabilities = ProductCapabilityRuntime.ForPreset(
            brandFeatures,
            preset,
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);
        ProductFeatures features = capabilities.Features;
        bool localPhotoOnly =
            preset == DeploymentPresets.RecordingHost
            && features.PhotoCapture
            && !features.VideoRecording;
        return preset switch
        {
            DeploymentPresets.RecordingHost => new(
                DeploymentPresets.RecordingHost,
                localPhotoOnly
                    ? AppLanguage.Get("本地拍照取证")
                    : AppLanguage.Get("录像主机"),
                localPhotoOnly
                    ? AppLanguage.Get("拍照并保存到本机资料库")
                    : features.NasArchive
                        ? AppLanguage.Get("本机录像并长期保存，支持备份到 NAS／网络共享文件夹")
                        : AppLanguage.Get("本机录像并长期保存"),
                BuildCapabilityList(
                    features.VideoRecording ? AppLanguage.Get("使用完整电脑录像能力") : null,
                    features.PhotoCapture ? AppLanguage.Get("可使用摄像头拍照") : null,
                    features.MediaLibrary
                        ? localPhotoOnly
                            ? AppLanguage.Get("照片与录像统一按单号检索")
                            : AppLanguage.Get("录像长期保存在本机")
                        : null,
                    features.NasArchive ? AppLanguage.Get("支持备份到 NAS／网络共享文件夹") : null,
                    features.MobileBackupReceive ? AppLanguage.Get("可接收并备份手机录像") : null,
                    features.ComputerBackupReceive
                        ? AppLanguage.Get("可接收并备份其他录制电脑上传的录像")
                        : null,
                    features.RemoteMediaAccess ? AppLanguage.Get("提供局域网录像回放") : null)),
            DeploymentPresets.RecordingWorkstation => new(
                DeploymentPresets.RecordingWorkstation,
                AppLanguage.Get("录像从机"),
                AppLanguage.Get("本机录像，自动上传到备份主机"),
                BuildCapabilityList(
                    features.VideoRecording ? AppLanguage.Get("使用完整电脑录像能力") : null,
                    AppLanguage.Get("录像先安全保存在本地缓存"),
                    features.LanCollaboration
                        ? AppLanguage.Get("上传到绑定的保存电脑并等待完整性确认")
                        : null,
                    AppLanguage.Get("不接收或备份其他设备录像"))),
            DeploymentPresets.MobileBackupHost => new(
                DeploymentPresets.MobileBackupHost,
                AppLanguage.Get("备份主机"),
                features.NasArchive
                    ? AppLanguage.Get("接收并长期保存手机或其他电脑的录像，支持备份到 NAS／网络共享文件夹")
                    : AppLanguage.Get("接收并长期保存手机或其他电脑的录像"),
                BuildCapabilityList(
                    AppLanguage.Get("本机不使用摄像头录像"),
                    features.NasArchive ? AppLanguage.Get("支持备份到 NAS／网络共享文件夹") : null,
                    features.MobileBackupReceive ? AppLanguage.Get("接收并长期保存手机录像") : null,
                    features.ComputerBackupReceive
                        ? AppLanguage.Get("接收并长期保存其他录制电脑上传的录像")
                        : null,
                    features.RemoteMediaAccess ? AppLanguage.Get("提供局域网录像回放") : null)),
            _ => new(
                DeploymentPresets.ViewerClient,
                AppLanguage.Get("录像查看端"),
                AppLanguage.Get("连接备份主机，查看和回放录像"),
                BuildCapabilityList(
                    AppLanguage.Get("本机不录像、不长期保存录像"),
                    AppLanguage.Get("连接现有主机查看录像"),
                    features.OrderReceiver ? AppLanguage.Get("保留订单联动和测试订单能力") : null,
                    AppLanguage.Get("不接收其他设备录像")))
        };
    }

    private static IReadOnlyList<string> BuildCapabilityList(
        params string?[] values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
}
