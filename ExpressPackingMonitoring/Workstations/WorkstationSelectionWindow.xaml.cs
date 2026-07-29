using System.Windows;

namespace ExpressPackingMonitoring;

public partial class WorkstationSelectionWindow : Window
{
    public string? SelectedPreset { get; private set; }

    public WorkstationSelectionWindow()
    {
        InitializeComponent();
    }

    private void RecordingHost_Click(object sender, RoutedEventArgs e)
    {
        SelectedPreset = Config.DeploymentPresets.RecordingHost;
        DialogResult = true;
    }

    private void RecordingWorkstation_Click(object sender, RoutedEventArgs e)
    {
        SelectedPreset = Config.DeploymentPresets.RecordingWorkstation;
        DialogResult = true;
    }

    private void ViewerClient_Click(object sender, RoutedEventArgs e)
    {
        SelectedPreset = Config.DeploymentPresets.ViewerClient;
        DialogResult = true;
    }

    private void MobileBackupHost_Click(object sender, RoutedEventArgs e)
    {
        SelectedPreset = Config.DeploymentPresets.MobileBackupHost;
        DialogResult = true;
    }
}
