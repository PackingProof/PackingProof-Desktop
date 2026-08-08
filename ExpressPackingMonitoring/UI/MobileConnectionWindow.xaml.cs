using System.Windows;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

public partial class MobileConnectionWindow : Window
{
    private readonly string _url;
    private readonly bool _containsAccessKey;
    private string _mobileAppDownloadUrl = MobileAppUpdatePolicyProvider.ReleasesUrl;

    public bool OpenSettingsRequested { get; private set; }

    public MobileConnectionWindow(
        string url,
        bool accessProtected,
        string unavailableMessage = "",
        bool canOpenSettings = true)
    {
        InitializeComponent();
        _url = url?.Trim() ?? "";
        _containsAccessKey = MobileConnectionService.ContainsAccessKey(_url);

        bool isReady = !string.IsNullOrWhiteSpace(_url)
            && string.IsNullOrWhiteSpace(unavailableMessage);
        ReadyPanel.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = isReady ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        OpenSettingsButton.Visibility = !isReady && canOpenSettings ? Visibility.Visible : Visibility.Collapsed;

        if (isReady)
        {
            AccessUrlTextBox.Text = _url;
            QrCodeImage.Source = MobileConnectionService.CreateQrBitmap(_url);
            SecurityNotice.Visibility = accessProtected || _containsAccessKey
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            UnavailableText.Text = string.IsNullOrWhiteSpace(unavailableMessage)
                ? "局域网服务尚未准备完成，请稍后重试"
                : unavailableMessage;
        }

        UpdateMobileAppDownload(MobileAppUpdatePolicyProvider.Shared.LatestRelease);
        Loaded += MobileConnectionWindow_Loaded;
        Loaded += (_, _) =>
            (isReady ? CopyButton : OpenMobileAppDownloadButton).Focus();
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
        _mobileAppDownloadUrl = release?.DownloadUrl
            ?? MobileAppUpdatePolicyProvider.ReleasesUrl;
        MobileAppQrCodeImage.Source =
            MobileConnectionService.CreateQrBitmap(_mobileAppDownloadUrl);
        MobileAppVersionText.Text = release == null
            ? AppLanguage.Get("Android 版 · 扫码打开下载页")
            : AppLanguage.Format("Android 最新版 v{0}", release.Version);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(_url, true);
            CopyButton.Content = _containsAccessKey ? "已复制 · 请勿转发" : "已复制";
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"复制网址失败：{ex.Message}", "手机/电脑连接");
        }
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

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
