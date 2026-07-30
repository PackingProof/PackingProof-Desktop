using System.Windows;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring;

internal sealed record WorkstationPurposeSummary(
    string Preset,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Capabilities);

public partial class WorkstationSelectionWindow : Window
{
    private readonly string _currentPreset;
    private bool? _recordOnThisComputer;
    private bool? _useAsStorageHost;

    public string? SelectedPreset { get; private set; }

    public WorkstationSelectionWindow(string? currentPreset = null)
    {
        _currentPreset = DeploymentPresets.IsKnown(currentPreset)
            ? DeploymentPresets.Normalize(currentPreset)
            : "";

        InitializeComponent();
        RestoreCurrentAnswers();
        UpdateResult();
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
            _recordOnThisComputer.Value,
            _useAsStorageHost.Value);
        bool unchanged = string.Equals(
            summary.Preset,
            _currentPreset,
            StringComparison.OrdinalIgnoreCase);
        ResultStateText.Text = AppLanguage.Get(unchanged ? "当前用途" : "将切换到");
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
                DeploymentPresets.MobileBackupHost => "FluentStorageIcon",
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
        if (string.Equals(selected, _currentPreset, StringComparison.OrdinalIgnoreCase))
        {
            DialogResult = false;
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
        ResolvePreset(recordOnThisComputer, useAsStorageHost) switch
        {
            DeploymentPresets.RecordingHost => new(
                DeploymentPresets.RecordingHost,
                "电脑录像并保存在本机",
                "这台电脑既负责录像，也作为保存主机长期保管录像",
                [
                    "使用完整电脑录像能力",
                    "录像长期保存在本机",
                    "可接收并备份手机录像",
                    "可接收并备份其他录制电脑上传的录像",
                    "提供局域网录像回放"
                ]),
            DeploymentPresets.RecordingWorkstation => new(
                DeploymentPresets.RecordingWorkstation,
                "电脑录像并保存到其他电脑",
                "使用这台电脑录像，完成后安全上传到绑定的保存电脑",
                [
                    "使用完整电脑录像能力",
                    "录像先安全保存在本地缓存",
                    "上传到绑定的保存电脑并等待完整性确认",
                    "不接收或备份其他设备录像"
                ]),
            DeploymentPresets.MobileBackupHost => new(
                DeploymentPresets.MobileBackupHost,
                "只作为保存主机",
                "这台电脑不录像，专门接收并长期保存其他设备的录像",
                [
                    "本机不使用摄像头录像",
                    "接收并长期保存手机录像",
                    "接收并长期保存其他录制电脑上传的录像",
                    "提供局域网录像回放"
                ]),
            _ => new(
                DeploymentPresets.ViewerClient,
                "只连接主机查看",
                "这台电脑不录像也不保存录像，只连接现有主机使用",
                [
                    "本机不录像、不长期保存录像",
                    "连接现有主机查看录像",
                    "保留订单联动和测试订单能力",
                    "不接收其他设备录像"
                ])
        };
}
