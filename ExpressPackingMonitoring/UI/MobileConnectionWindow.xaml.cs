using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

public sealed record MobileConnectionRepairResult(
    bool IsReady,
    string Url,
    bool AccessProtected,
    string UnavailableMessage);

public partial class MobileConnectionWindow : Window
{
    private const string TestFlightJoinUrl = "https://testflight.apple.com/join/5QKpJuBG";
    private string _url;
    private bool _containsAccessKey;
    private bool _accessProtected;
    private readonly Func<Task<MobileConnectionRepairResult>>? _repairLanAccessAsync;
    private bool _repairingLanAccess;
    private string _mobileAppDownloadUrl = MobileAppUpdatePolicyProvider.ReleasesUrl;

    public bool OpenSettingsRequested { get; private set; }

    public MobileConnectionWindow(
        string url,
        bool accessProtected,
        string unavailableMessage = "",
        bool canOpenSettings = true,
        Func<Task<MobileConnectionRepairResult>>? repairLanAccessAsync = null)
    {
        InitializeComponent();
        _url = "";
        _accessProtected = accessProtected;
        _repairLanAccessAsync = repairLanAccessAsync;
        ApplyConnectionState(url, accessProtected, unavailableMessage, canOpenSettings);

        UpdateMobileAppDownload(MobileAppUpdatePolicyProvider.Shared.LatestRelease);
        TestFlightQrCodeImage.Source = MobileConnectionService.CreateQrBitmap(TestFlightJoinUrl);
        Loaded += MobileConnectionWindow_Loaded;
        Loaded += (_, _) => ResolveInitialFocus().Focus();
    }

    private void ApplyConnectionState(
        string url,
        bool accessProtected,
        string unavailableMessage,
        bool canOpenSettings)
    {
        _url = url?.Trim() ?? "";
        _accessProtected = accessProtected;
        _containsAccessKey = MobileConnectionService.ContainsAccessKey(_url);
        bool isReady = !string.IsNullOrWhiteSpace(_url)
            && string.IsNullOrWhiteSpace(unavailableMessage);

        ReadyPanel.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = isReady ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        RepairLanButton.Visibility = !isReady && _repairLanAccessAsync != null
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenSettingsButton.Visibility = !isReady && canOpenSettings
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (isReady)
        {
            AccessUrlTextBox.Text = _url;
            QrCodeImage.Source = MobileConnectionService.CreateQrBitmap(_url);
            SecurityNotice.Visibility = _accessProtected || _containsAccessKey
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        UnavailableText.Text = string.IsNullOrWhiteSpace(unavailableMessage)
            ? "局域网服务尚未准备完成，请稍后重试"
            : unavailableMessage;
    }

    private Control ResolveInitialFocus()
    {
        if (ReadyPanel.Visibility == Visibility.Visible)
            return CopyButton;
        if (RepairLanButton.Visibility == Visibility.Visible)
            return RepairLanButton;
        if (OpenSettingsButton.Visibility == Visibility.Visible)
            return OpenSettingsButton;
        return OpenMobileAppDownloadButton;
    }

    private async void MobileConnectionWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            MobileAppReleaseInfo release =
                await MobileAppUpdatePolicyProvider.Shared.CheckLatestAsync();
            if (IsLoaded)
                UpdateMobileAppDownload(release);
        }
        catch
        {
            // The stable releases page is already available in the QR code.
        }
    }

    private void UpdateMobileAppDownload(MobileAppReleaseInfo? release)
    {
        _mobileAppDownloadUrl = MobileAppUpdatePolicyProvider.ReleasesUrl;
        MobileAppQrCodeImage.Source =
            MobileConnectionService.CreateQrBitmap(_mobileAppDownloadUrl);
        MobileAppVersionText.Text = release == null
            ? AppLanguage.Get("Android 版 · 扫码打开下载页")
            : AppLanguage.Format("Android 最新版 v{0}", release.Version);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!ClipboardHelper.TrySetDataObject(_url, out Exception error))
        {
            AppDialog.Error(this, $"复制网址失败：{error.Message}", "手机/电脑连接");
            return;
        }

        CopyButton.Content = _containsAccessKey ? "已复制 · 请勿转发" : "已复制";
    }

    private void CopyMobileAppUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!ClipboardHelper.TrySetDataObject(_mobileAppDownloadUrl, out Exception error))
        {
            AppDialog.Error(this, $"复制网址失败：{error.Message}", "手机/电脑连接");
            return;
        }

        CopyMobileAppUrlButton.Content = "已复制";
    }

    private void CopyTestFlightUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!ClipboardHelper.TrySetDataObject(TestFlightJoinUrl, out Exception error))
        {
            AppDialog.Error(this, $"复制网址失败：{error.Message}", "手机/电脑连接");
            return;
        }

        CopyTestFlightUrlButton.Content = "已复制";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (WorkstationNetwork.TryOpenUrl(_url, out string error))
            return;

        AppDialog.Error(this, $"打开网页失败：{error}", "手机/电脑连接");
    }

    private void OpenMobileAppDownload_Click(object sender, RoutedEventArgs e)
    {
        if (WorkstationNetwork.TryOpenUrl(_mobileAppDownloadUrl, out string error))
            return;

        AppDialog.Error(
            this,
            AppLanguage.Format("打开手机 App 下载页失败：{0}", error),
            AppLanguage.Get("手机/电脑连接"));
    }

    private void OpenTestFlight_Click(object sender, RoutedEventArgs e)
    {
        if (WorkstationNetwork.TryOpenUrl(TestFlightJoinUrl, out string error))
            return;

        AppDialog.Error(
            this,
            AppLanguage.Format("打开 TestFlight 加入页失败：{0}", error),
            AppLanguage.Get("手机/电脑连接"));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = true;
        Close();
    }

    private async void RepairLan_Click(object sender, RoutedEventArgs e)
    {
        if (_repairingLanAccess || _repairLanAccessAsync == null)
            return;

        _repairingLanAccess = true;
        RepairLanButton.IsEnabled = false;
        RepairLanButton.Content = "正在修复…";
        UnavailableText.Text = "正在修复局域网，Windows 可能会请求管理员授权";
        try
        {
            MobileConnectionRepairResult result = await _repairLanAccessAsync();
            ApplyConnectionState(
                result.Url,
                result.AccessProtected,
                result.UnavailableMessage,
                canOpenSettings: OpenSettingsRequested || OpenSettingsButton.Visibility == Visibility.Visible);
            if (!result.IsReady)
            {
                RepairLanButton.Content = "重新修复";
                RepairLanButton.IsEnabled = true;
            }
            else
            {
                CopyButton.Focus();
            }
        }
        catch
        {
            ApplyConnectionState(
                "",
                _accessProtected,
                WebServer.GetLanAccessFailureUserMessage(repairAttempted: true),
                canOpenSettings: OpenSettingsButton.Visibility == Visibility.Visible);
            RepairLanButton.Content = "重新修复";
            RepairLanButton.IsEnabled = true;
        }
        finally
        {
            _repairingLanAccess = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
