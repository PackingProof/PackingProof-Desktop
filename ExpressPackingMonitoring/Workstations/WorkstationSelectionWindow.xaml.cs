using System.Windows;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring;

public partial class WorkstationSelectionWindow : Window
{
    private bool _recordOnThisComputer;
    public string? SelectedPreset { get; private set; }

    public WorkstationSelectionWindow()
    {
        InitializeComponent();
    }

    private void RecordOnThisComputer_Click(object sender, RoutedEventArgs e)
    {
        ShowStorageStep(recordOnThisComputer: true);
    }

    private void DoNotRecordOnThisComputer_Click(object sender, RoutedEventArgs e)
    {
        ShowStorageStep(recordOnThisComputer: false);
    }

    private void UseAsStorageHost_Click(object sender, RoutedEventArgs e)
    {
        CompleteSelection(useAsStorageHost: true);
    }

    private void DoNotUseAsStorageHost_Click(object sender, RoutedEventArgs e)
    {
        CompleteSelection(useAsStorageHost: false);
    }

    private void ShowStorageStep(bool recordOnThisComputer)
    {
        _recordOnThisComputer = recordOnThisComputer;
        RecordingStep.Visibility = Visibility.Collapsed;
        StorageStep.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        StepText.Text = AppLanguage.Get("第 2 步，共 2 步");
        QuestionText.Text = AppLanguage.Get("是否让这台电脑作为保存主机？");
        QuestionHintText.Text = AppLanguage.Get("保存主机可以接收并长期保存手机或其他电脑上传的录像");
    }

    private void CompleteSelection(bool useAsStorageHost)
    {
        SelectedPreset = ResolvePreset(_recordOnThisComputer, useAsStorageHost);
        DialogResult = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        StorageStep.Visibility = Visibility.Collapsed;
        RecordingStep.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        StepText.Text = AppLanguage.Get("第 1 步，共 2 步");
        QuestionText.Text = AppLanguage.Get("是否使用这台电脑录像？");
        QuestionHintText.Text = AppLanguage.Get("选择是否启用本机摄像头、麦克风、条码识别和扫码枪");
    }

    internal static string ResolvePreset(bool recordOnThisComputer, bool useAsStorageHost) =>
        (recordOnThisComputer, useAsStorageHost) switch
        {
            (true, true) => Config.DeploymentPresets.RecordingHost,
            (true, false) => Config.DeploymentPresets.RecordingWorkstation,
            (false, true) => Config.DeploymentPresets.MobileBackupHost,
            _ => Config.DeploymentPresets.ViewerClient
        };
}
