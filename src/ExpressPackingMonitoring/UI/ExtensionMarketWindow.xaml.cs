using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using ExpressPackingMonitoring.UpdateCore;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace ExpressPackingMonitoring.UI;

public partial class ExtensionMarketWindow : Window
{
    private readonly ExtensionMarketClient _marketClient;
    private readonly ExtensionInstallationService _installationService;
    private readonly ExtensionPackageService _packageService;
    private readonly string? _initialExtensionId;
    private readonly ObservableCollection<ExtensionMarketDisplayItem> _items = new();
    private readonly ObservableCollection<ExtensionMarketScreenshotItem> _screenshots = new();
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _detailsCancellation;
    private CancellationTokenSource? _screenshotCancellation;
    private bool _localMutationBusy;
    private bool _isClosed;
    private ExtensionMarketSession? _session;
    private ExtensionMarketDetails? _selectedDetails;
    private ExtensionMarketRelease? _selectedRelease;

    internal ExtensionMarketWindow()
        : this(new ExtensionMarketClient(), new ExtensionInstallationService(), new ExtensionPackageService(), null)
    {
    }

    internal ExtensionMarketWindow(string initialExtensionId)
        : this(new ExtensionMarketClient(), new ExtensionInstallationService(), new ExtensionPackageService(), initialExtensionId)
    {
    }

    internal ExtensionMarketWindow(
        ExtensionMarketClient marketClient,
        ExtensionInstallationService installationService,
        ExtensionPackageService packageService,
        string? initialExtensionId = null)
    {
        InitializeComponent();
        Title = AppLanguage.Get("PackingProof 扩展市场");
        _marketClient = marketClient;
        _installationService = installationService;
        _packageService = packageService;
        _initialExtensionId = initialExtensionId;
        CatalogList.ItemsSource = _items;
        ScreenshotList.ItemsSource = _screenshots;
        Loaded += async (_, _) => await RefreshMarketAsync(showErrorDialog: false);
        Closed += (_, _) =>
        {
            _isClosed = true;
            _operationCancellation?.Cancel();
            _detailsCancellation?.Cancel();
            _screenshotCancellation?.Cancel();
        };
    }

    private bool CanUpdateUi => !_isClosed && IsLoaded && IsVisible;

    private async Task RefreshMarketAsync(bool showErrorDialog = true)
    {
        if (_isClosed)
            return;

        SetBusy(true, "正在读取经过签名的市场目录");
        try
        {
            _operationCancellation?.Cancel();
            _operationCancellation = new CancellationTokenSource();
            _detailsCancellation?.Cancel();
            _screenshotCancellation?.Cancel();
            _screenshots.Clear();
            _selectedDetails = null;
            _selectedRelease = null;
            _session = null;
            PopulateDisplayItems([]);
            _session = await _marketClient.LoadCatalogAsync(_operationCancellation.Token);
            if (!CanUpdateUi)
                return;
            PopulateDisplayItems(_session.Catalog.Extensions);
            ShowMarketReadyStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!CanUpdateUi)
                return;
            SourceStatusText.Text = AppLanguage.Format(
                "Extension.Market.OfflineLocalAvailable",
                ex.Message);
            PopulateDisplayItems([]);
            if (showErrorDialog)
                AppDialog.Error(this, ex.Message, "扩展市场不可用");
        }
        finally
        {
            if (CanUpdateUi)
                SetBusy(false);
        }
    }

    private void PopulateDisplayItems(IReadOnlyCollection<ExtensionMarketCatalogItem> catalogItems)
    {
        string? selectedId = CatalogList.SelectedItem is ExtensionMarketDisplayItem selected
            ? selected.Item.Id
            : _initialExtensionId;
        IReadOnlyList<ExtensionMarketDisplayItem> next = BuildDisplayItems(
            catalogItems,
            _installationService.GetInstalled());
        _items.Clear();
        foreach (ExtensionMarketDisplayItem item in next) _items.Add(item);
        if (_items.Count == 0) return;
        CatalogList.SelectedItem = selectedId == null
            ? _items[0]
            : _items.FirstOrDefault(value => value.Item.Id == selectedId) ?? _items[0];
    }

    internal static IReadOnlyList<ExtensionMarketDisplayItem> BuildDisplayItems(
        IEnumerable<ExtensionMarketCatalogItem> catalogItems,
        IReadOnlyCollection<InstalledExtensionRecord> installedRecords)
    {
        IReadOnlyDictionary<string, InstalledExtensionRecord> installed = installedRecords
            .ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new List<ExtensionMarketDisplayItem>();
        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExtensionMarketCatalogItem item in catalogItems)
        {
            catalogIds.Add(item.Id);
            installed.TryGetValue(item.Id, out InstalledExtensionRecord? record);
            result.Add(new ExtensionMarketDisplayItem(item, record));
        }
        result.AddRange(installed.Values
            .Where(record => !catalogIds.Contains(record.Id))
            .OrderBy(record => record.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ExtensionMarketDisplayItem.CreateLocal));
        return result;
    }

    private async void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CanUpdateUi)
            return;
        if (CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected) return;
        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        _detailsCancellation = null;
        if (selected.IsLocalOnly)
        {
            _selectedDetails = null;
            _selectedRelease = null;
            _screenshots.Clear();
            _screenshotCancellation?.Cancel();
            ShowDetails(selected);
            return;
        }
        if (_session == null) return;

        var detailsCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _operationCancellation?.Token ?? CancellationToken.None);
        _detailsCancellation = detailsCancellation;
        CancellationToken cancellationToken = detailsCancellation.Token;
        ShowDetailsLoading(selected);
        bool restoreReadyStatus = false;
        try
        {
            _selectedDetails = await _marketClient.LoadDetailsAsync(
                _session,
                selected.Item,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanUpdateUi
                || !ReferenceEquals(_detailsCancellation, detailsCancellation))
                return;
            if (!ReferenceEquals(CatalogList.SelectedItem, selected)) return;
            _selectedRelease = ExtensionMarketVersionPolicy.SelectInstallableRelease(
                _selectedDetails,
                selected.Item.LatestVersion);
            ShowDetails(selected);
            restoreReadyStatus = await LoadScreenshotsAsync(
                _selectedDetails.Extension,
                selected.Item.Id,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!CanUpdateUi
                || !ReferenceEquals(_detailsCancellation, detailsCancellation)
                || !ReferenceEquals(CatalogList.SelectedItem, selected))
                return;
            _selectedDetails = null;
            _selectedRelease = null;
            InstallButton.IsEnabled = false;
            InstallOtherVersionButton.IsEnabled = false;
            SourceStatusText.Text = AppLanguage.Format("读取扩展详情失败：{0}", ex.Message);
            AppDialog.Error(
                this,
                AppLanguage.Format("读取扩展详情失败：{0}", ex.Message),
                "读取失败");
        }
        finally
        {
            if (ReferenceEquals(_detailsCancellation, detailsCancellation))
            {
                _detailsCancellation = null;
                detailsCancellation.Dispose();
                if (CanUpdateUi
                    && ReferenceEquals(CatalogList.SelectedItem, selected)
                    && restoreReadyStatus)
                    ShowMarketReadyStatus();
            }
        }
    }

    private void ShowDetailsLoading(ExtensionMarketDisplayItem selected)
    {
        _selectedDetails = null;
        _selectedRelease = null;
        _screenshotCancellation?.Cancel();
        _screenshotCancellation?.Dispose();
        _screenshotCancellation = null;
        _screenshots.Clear();
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        ExtensionNameRun.Text = selected.Name;
        ExtensionNameLink.NavigateUri = null;
        ExtensionNameLink.IsEnabled = false;
        PublisherNameRun.Text = selected.AuthorText;
        PublisherLink.NavigateUri = null;
        PublisherLink.IsEnabled = false;
        LatestVersionText.Text = string.IsNullOrWhiteSpace(selected.Item.LatestVersion)
            ? AppLanguage.Get("最新版本：暂无")
            : AppLanguage.Format("最新版本：{0}", selected.Item.LatestVersion);
        InstalledVersionText.Text = installed == null
            ? AppLanguage.Get("尚未安装")
            : AppLanguage.Format("已安装：{0}", installed.Version);
        CompatibilityText.Text = AppLanguage.Get("正在读取兼容信息");
        DescriptionText.Text = selected.Summary;
        RiskPanel.Visibility = Visibility.Collapsed;
        SourceTagText.Text = selected.SourceLabel;
        SourceTagText.Tag = selected.SourceLabel;
        SourceTagBorder.Tag = selected.SourceLabel;
        TypeTagText.Text = selected.TypeLabel;
        InstallButton.IsEnabled = false;
        InstallOtherVersionButton.IsEnabled = false;
        LaunchButton.Visibility = installed?.Type == "external-adapter"
            ? Visibility.Visible
            : Visibility.Collapsed;
        LaunchButton.IsEnabled = installed?.Type == "external-adapter"
            && !string.IsNullOrWhiteSpace(installed.LauncherPath);
        OpenFolderButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;
        SourceStatusText.Text = AppLanguage.Get("正在读取扩展详情");
    }

    private async Task<bool> LoadScreenshotsAsync(
        ExtensionMarketDescriptor extension,
        string extensionId,
        CancellationToken cancellationToken)
    {
        _screenshotCancellation?.Cancel();
        _screenshotCancellation?.Dispose();
        _screenshotCancellation = null;
        _screenshots.Clear();
        if (extension.Screenshots.Count == 0) return true;

        _screenshotCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _screenshotCancellation.Token;
        try
        {
            foreach (ExtensionMarketScreenshot screenshot in extension.Screenshots)
            {
                byte[] bytes = await _marketClient.DownloadScreenshotAsync(screenshot, token);
                token.ThrowIfCancellationRequested();
                if (!CanUpdateUi)
                    return false;
                if (CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected
                    || !string.Equals(selected.Item.Id, extensionId, StringComparison.Ordinal))
                    return false;
                BitmapImage image = await Task.Run(() => CreateScreenshotImage(bytes), token);
                token.ThrowIfCancellationRequested();
                _screenshots.Add(new ExtensionMarketScreenshotItem(image, screenshot.Alt));
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or NotSupportedException or FormatException or ArgumentException)
        {
            if (!CanUpdateUi)
                return false;
            SourceStatusText.Text = AppLanguage.Format("Extension.Market.PreviewLoadFailed", ex.Message);
            return false;
        }
    }

    internal static BitmapImage CreateScreenshotImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 900;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ShowDetails(ExtensionMarketDisplayItem selected)
    {
        if (selected.IsLocalOnly)
        {
            ShowLocalDetails(selected);
            return;
        }
        ExtensionMarketDetails details = _selectedDetails!;
        ExtensionNameRun.Text = details.Extension.Name;
        ExtensionNameLink.NavigateUri = TryCreateExternalUri(details.Extension.Homepage);
        ExtensionNameLink.IsEnabled = ExtensionNameLink.NavigateUri != null;
        PublisherNameRun.Text = details.Publisher.DisplayName;
        PublisherLink.NavigateUri = TryCreateExternalUri(details.Publisher.Homepage);
        PublisherLink.IsEnabled = PublisherLink.NavigateUri != null;
        LatestVersionText.Text = _selectedRelease == null
            ? "最新版本：暂无"
            : $"最新版本：{_selectedRelease.Version}";
        DescriptionText.Text = details.Extension.Description;
        bool external = details.Extension.Type == "external-adapter";
        bool closedSource = details.Extension.SourceAvailability == "closed-source";
        RiskPanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        RiskText.Text = closedSource
            ? AppLanguage.Get("⚠ 闭源外部程序。PackingProof 无法审查其内部行为，也无法限制它访问网络、文件或其他系统资源")
            : AppLanguage.Get("⚠ 外部程序由用户手动运行。PackingProof 不会自动启动，也无法限制它访问网络、文件或其他系统资源");
        UpdateActionState(selected);
    }

    private void ShowLocalDetails(ExtensionMarketDisplayItem selected)
    {
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        ExtensionNameRun.Text = selected.Name;
        ExtensionNameLink.NavigateUri = null;
        ExtensionNameLink.IsEnabled = false;
        PublisherNameRun.Text = AppLanguage.Get("本地安装");
        PublisherLink.NavigateUri = null;
        PublisherLink.IsEnabled = false;
        LatestVersionText.Text = installed == null
            ? AppLanguage.Get("最新版本：暂无")
            : AppLanguage.Format("本地版本：{0}", installed.Version);
        DescriptionText.Text = !string.IsNullOrWhiteSpace(installed?.Description)
            ? installed.Description
            : !string.IsNullOrWhiteSpace(installed?.Summary)
                ? installed.Summary
                : AppLanguage.Get("该扩展从本地 PPEXT 安装，未提供简介");
        bool external = installed?.Type == "external-adapter";
        RiskPanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        RiskText.Text = AppLanguage.Get(
            "本地扩展未经市场审核。PackingProof 无法审查其内部行为，也无法限制它访问网络、文件或其他系统资源");
        UpdateActionState(selected);
    }

    private void UpdateActionState(ExtensionMarketDisplayItem selected)
    {
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        bool canLaunch = installed?.Type == "external-adapter" && !string.IsNullOrWhiteSpace(installed.LauncherPath);
        if (selected.IsLocalOnly)
        {
            InstallButton.Visibility = Visibility.Collapsed;
            InstallOtherVersionButton.Visibility = Visibility.Collapsed;
            LaunchButton.Visibility = installed?.Type == "external-adapter"
                ? Visibility.Visible
                : Visibility.Collapsed;
            LaunchButton.IsEnabled = canLaunch;
            InstalledVersionText.Text = installed == null
                ? AppLanguage.Get("尚未安装")
                : AppLanguage.Format("已安装：{0}", installed.Version);
            CompatibilityText.Text = AppLanguage.Get("该扩展从本地 PPEXT 安装，不会从市场自动更新");
            SourceTagText.Text = selected.SourceLabel;
            SourceTagText.Tag = selected.SourceLabel;
            SourceTagBorder.Tag = selected.SourceLabel;
            TypeTagText.Text = selected.TypeLabel;
            OpenFolderButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;
            RemoveButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        bool compatible = _selectedRelease != null
            && ExtensionMarketVersionPolicy.IsAppCompatible(
                _selectedRelease.Compatibility.MinPackingProofVersion,
                AppVersion.Current);
        InstallButton.IsEnabled = _selectedRelease != null && compatible;
        InstallButton.Content = installed == null
            ? "安装"
            : installed.Version == _selectedRelease?.Version
                ? "重新安装"
                : "更新";
        InstallButton.Visibility = Visibility.Visible;
        InstallOtherVersionButton.Visibility = Visibility.Visible;
        LaunchButton.Visibility = installed?.Type == "external-adapter"
            ? Visibility.Visible
            : Visibility.Collapsed;
        LaunchButton.IsEnabled = canLaunch;
        InstalledVersionText.Text = installed == null
            ? "尚未安装"
            : $"已安装：{installed.Version}";
        CompatibilityText.Text = _selectedRelease == null
            ? AppLanguage.Get("没有可安装版本")
            : compatible
                ? AppLanguage.Format(
                    "需要 PackingProof {0} 或更高版本",
                    _selectedRelease.Compatibility.MinPackingProofVersion)
                : AppLanguage.Format(
                    "需要 PackingProof {0} 或更高版本，当前版本无法安装",
                    _selectedRelease.Compatibility.MinPackingProofVersion);
        SourceTagText.Text = selected.SourceLabel;
        SourceTagText.Tag = selected.SourceLabel;
        SourceTagBorder.Tag = selected.SourceLabel;
        TypeTagText.Text = selected.TypeLabel;
        ExtensionMarketDetails? selectedDetails = _selectedDetails;
        ExtensionMarketRelease? selectedRelease = _selectedRelease;
        InstallOtherVersionButton.IsEnabled = selectedDetails != null
            && selectedRelease != null
            && GetOtherAvailableReleases(selectedDetails, selectedRelease).Count > 0;
        OpenFolderButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRelease == null) return;
        await InstallReleaseAsync(_selectedRelease);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected) return;
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        if (installed != null && !_installationService.TryLaunch(installed, out string error))
            AppDialog.Error(this, $"启动扩展失败：{error}", "启动失败");
    }

    private void InstallOtherVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetails == null || _selectedRelease == null) return;
        IReadOnlyList<ExtensionMarketRelease> releases = GetOtherAvailableReleases(
            _selectedDetails,
            _selectedRelease);
        if (releases.Count == 0) return;

        InstalledExtensionRecord? installed = CatalogList.SelectedItem is ExtensionMarketDisplayItem selected
            ? _installationService.GetInstalled().FirstOrDefault(value => value.Id == selected.Item.Id)
            : null;
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = InstallOtherVersionButton,
            Background = (Brush)FindResource("PanelBackground"),
            BorderBrush = (Brush)FindResource("BorderDefault"),
            Foreground = (Brush)FindResource("TextPrimary")
        };
        foreach (ExtensionMarketRelease release in releases)
        {
            bool compatible = ExtensionMarketVersionPolicy.IsAppCompatible(
                release.Compatibility.MinPackingProofVersion,
                AppVersion.Current);
            string installedSuffix = installed?.Version == release.Version
                ? AppLanguage.Get("（已安装）")
                : "";
            var item = new MenuItem
            {
                Header = AppLanguage.Format(
                    "{0}{1}  ·  需要 PackingProof {2}+",
                    release.Version,
                    installedSuffix,
                    release.Compatibility.MinPackingProofVersion),
                IsEnabled = compatible,
                ToolTip = compatible ? null : AppLanguage.Get("当前 PackingProof 版本过低")
            };
            ExtensionMarketRelease selectedRelease = release;
            item.Click += async (_, _) => await InstallReleaseAsync(selectedRelease);
            menu.Items.Add(item);
        }

        InstallOtherVersionButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async Task InstallReleaseAsync(ExtensionMarketRelease release)
    {
        if (_selectedDetails == null
            || CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected) return;
        bool external = _selectedDetails.Extension.Type == "external-adapter";
        bool closedSource = _selectedDetails.Extension.SourceAvailability == "closed-source";
        if (external && !AppDialog.Confirm(
                this,
                closedSource
                    ? BrandText.Apply("这是闭源外部程序。PackingProof 无法审查其代码，也不会限制程序访问网络、文件或其他系统资源。确认下载并安装吗？")
                    : BrandText.Apply("这是需要用户手动运行的外部程序。PackingProof 不会自动启动，也无法限制程序访问系统资源。确认下载并安装吗？"),
                "安装外部扩展",
                AppDialogSeverity.Warning,
                "确认安装"))
            return;

        string packagePath = "";
        bool restoreReadyStatus = false;
        selected.SetDownloading(true);
        CatalogList.Items.Refresh();
        SetBusy(true, "正在下载扩展包", showProgress: true);
        try
        {
            SetLocalMutationBusy(true);
            _operationCancellation?.Cancel();
            _operationCancellation = new CancellationTokenSource();
            var progress = new Progress<ExtensionPackageProgress>(value =>
            {
                SourceStatusText.Text = value.Message;
                DownloadStatusText.Text = value.Message;
                DownloadProgressText.Text = FormatDownloadProgress(value.Received, value.Total);
                DownloadProgress.IsIndeterminate = value.Total <= 0;
                if (value.Total > 0)
                {
                    DownloadProgress.Maximum = value.Total;
                    DownloadProgress.TargetValue = Math.Min(value.Received, value.Total);
                }
            });
            packagePath = await _marketClient.DownloadPackageAsync(
                release,
                progress,
                _operationCancellation.Token);
            ExtensionInstallResult result = await Task.Run(() => _installationService.Install(
                packagePath,
                _selectedDetails.Extension.Name,
                _selectedDetails.Extension.Id,
                release.Version,
                _selectedDetails.Extension.Type,
                release.Sha256));
            ShowInstallResult(result);
            OpenInstalledExtensionDirectory(result.Record);
            RefreshInstalledState(result.Record.Id);
            restoreReadyStatus = true;
        }
        catch (OperationCanceledException)
        {
            restoreReadyStatus = true;
        }
        catch (Exception ex)
        {
            string detail = AppLanguage.TranslateForDisplay(
                ex.Message,
                "扩展安装失败，请查看日志了解详情");
            SourceStatusText.Text = AppLanguage.Format(
                "安装扩展失败：{0}",
                detail);
            AppDialog.Error(
                this,
                AppLanguage.Format(
                    "安装扩展失败：{0}",
                    detail),
                "安装失败");
        }
        finally
        {
            TryDeleteDownloadedPackage(packagePath);
            selected.SetDownloading(false);
            CatalogList.Items.Refresh();
            SetBusy(false);
            SetLocalMutationBusy(false);
            if (restoreReadyStatus) ShowMarketReadyStatus();
        }
    }

    private async void InstallLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = BrandMetadata.ProductName + " 扩展 (*.ppext)|*.ppext|油猴脚本 (*.user.js)|*.user.js",
            Multiselect = false,
            CheckFileExists = true,
            Title = "安装本地扩展"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileName.EndsWith(".user.js", StringComparison.OrdinalIgnoreCase))
        {
            ImportLegacyUserscript(dialog.FileName);
            return;
        }
        SetLocalMutationBusy(true);
        try
        {
            ExtensionPackageInspection inspection = _packageService.Inspect(dialog.FileName);
            string warning = inspection.Manifest.Type == "external-adapter"
                ? BrandText.Apply("该 PPEXT 未经过市场审核，并且包含需要用户手动运行的外部程序。PackingProof 无法限制程序访问系统资源")
                : "该 PPEXT 未经过市场审核。请只安装来自可信开发者的扩展";
            if (!AppDialog.Confirm(
                    this,
                    warning + "\n\n是否继续安装？",
                    "安装未经市场审核的扩展",
                    AppDialogSeverity.Warning,
                    "继续安装"))
                return;
            string displayName = string.IsNullOrWhiteSpace(inspection.Manifest.Name)
                ? inspection.Manifest.Id
                : inspection.Manifest.Name;
            ExtensionInstallResult result = await Task.Run(() =>
                _installationService.Install(dialog.FileName, displayName));
            ShowInstallResult(result);
            OpenInstalledExtensionDirectory(result.Record);
            RefreshInstalledState(result.Record.Id, selectInstalledItem: true);
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"安装本地扩展失败：{ex.Message}", "安装失败");
        }
        finally
        {
            SetLocalMutationBusy(false);
        }
    }

    private void ImportLegacyUserscript(string fileName)
    {
        try
        {
            var catalog = new UserscriptCatalog();
            UserscriptDescriptor descriptor = catalog.Import(fileName);
            if (descriptor.Warnings.Count > 0 && !AppDialog.Confirm(
                    this,
                    $"脚本存在以下维护或安全提示：\n· {string.Join("\n· ", descriptor.Warnings)}\n\n是否仍然导入？",
                    "导入未经市场审核的脚本",
                    AppDialogSeverity.Warning,
                    "确认导入"))
            {
                catalog.Remove(descriptor.Id);
                return;
            }
            AppDialog.Information(this, "脚本已导入，可在安装订单联动中选择", "导入成功");
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"导入脚本失败：{ex.Message}", "导入失败");
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected) return;
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        if (installed == null) return;

        IReadOnlyList<RunningExtensionProcess> runningProcesses = installed.Type == "external-adapter"
            ? ExtensionProcessManager.FindRunningProcesses(installed.InstallDirectory)
            : [];
        string processList = string.Join("、", runningProcesses
            .Take(3)
            .Select(process => $"{process.ProcessName}（PID {process.ProcessId}）"));
        if (runningProcesses.Count > 3) processList += $"等 {runningProcesses.Count} 个进程";
        string message = installed.Type == "userscript"
            ? BrandText.Apply("这会删除 PackingProof 保存的脚本源文件，但无法替你从浏览器脚本管理器中卸载已经安装的副本")
            : runningProcesses.Count > 0
                ? BrandText.Apply(
                    $"检测到扩展仍在运行：{processList}\n\n删除前需要终止这些程序。PackingProof 只会终止安装目录内的扩展程序，不会删除扩展自己保存的配置、凭据或业务数据")
                : BrandText.Apply("这只会删除 PackingProof 管理的安装目录，不会删除扩展自己保存的配置、凭据或业务数据");
        string confirmText = runningProcesses.Count > 0 ? "终止并删除" : "确认删除";
        if (!AppDialog.Confirm(this, message + "\n\n确定继续吗？", "删除扩展", AppDialogSeverity.Warning, confirmText))
            return;

        SetLocalMutationBusy(true);
        SetBusy(true, runningProcesses.Count > 0 ? "正在终止并删除扩展" : "正在删除扩展");
        try
        {
            if (runningProcesses.Count > 0)
            {
                (bool terminated, string error) = await Task.Run(() =>
                {
                    bool success = ExtensionProcessManager.TryTerminateProcesses(
                        installed.InstallDirectory,
                        runningProcesses,
                        out string terminationError);
                    return (success, terminationError);
                });
                if (!terminated)
                {
                    AppDialog.Error(this, $"无法终止正在运行的扩展程序：\n{error}\n\n请手动退出程序后重试", "无法删除扩展");
                    return;
                }
            }

            await Task.Run(() => _installationService.Remove(installed.Id));
            RefreshInstalledState(installed.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDialog.Error(
                this,
                $"扩展文件仍被其他程序占用，或当前账户没有删除权限。请退出扩展及相关程序后重试\n\n详细信息：{ex.Message}",
                "无法删除扩展");
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"删除扩展失败：{ex.Message}", "无法删除扩展");
        }
        finally
        {
            SetBusy(false);
            SetLocalMutationBusy(false);
            ShowMarketReadyStatus();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected) return;
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        if (installed != null) OpenInstalledExtensionLocation(installed);
    }

    private void ShowInstallResult(ExtensionInstallResult result)
    {
        string warnings = result.Warnings.Count == 0
            ? ""
            : $"\n\n{AppLanguage.Get("提示：")}\n· {string.Join("\n· ", result.Warnings.Select(AppLanguage.Translate))}";
        AppDialog.Information(
            this,
            result.Record.Type == "userscript"
                ? AppLanguage.Get("扩展脚本已导入，可在安装订单联动中选择") + warnings
                : AppLanguage.Get("外部扩展已安装。PackingProof 不会自动运行它，关闭此提示后将打开安装目录") + warnings,
            AppLanguage.Get("安装完成"));
    }

    private void OpenInstalledExtensionDirectory(InstalledExtensionRecord record)
    {
        if (record.Type != "external-adapter") return;
        OpenInstalledExtensionLocation(record);
    }

    private void OpenInstalledExtensionLocation(InstalledExtensionRecord record)
    {
        string locationPath = _installationService.GetInstalledLocationPath(record);
        FileLocationResult result = locationPath.Length == 0
            ? FileLocationResult.Invalid
            : WindowsShellFileLocator.Locate(locationPath);
        if (result is not (FileLocationResult.Selected or FileLocationResult.OpenedFolder))
            AppDialog.Error(this, "扩展已安装，但无法定位本地文件，请尝试重新安装", "无法打开目录");
    }

    private void RefreshInstalledState(string extensionId, bool selectInstalledItem = false)
    {
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == extensionId);
        ExtensionMarketDisplayItem? item = _items.FirstOrDefault(value =>
            string.Equals(value.Item.Id, extensionId, StringComparison.Ordinal));
        if (installed == null)
        {
            if (item?.IsLocalOnly == true)
            {
                bool wasSelected = ReferenceEquals(CatalogList.SelectedItem, item);
                _items.Remove(item);
                if (wasSelected) CatalogList.SelectedItem = _items.FirstOrDefault();
            }
            else
            {
                item?.UpdateInstalled(null);
            }
        }
        else if (item != null)
        {
            item.UpdateInstalled(installed);
        }
        else
        {
            item = ExtensionMarketDisplayItem.CreateLocal(installed);
            _items.Add(item);
        }
        CatalogList.Items.Refresh();
        if (selectInstalledItem && item != null) CatalogList.SelectedItem = item;
        if (CatalogList.SelectedItem is ExtensionMarketDisplayItem selected
            && string.Equals(selected.Item.Id, extensionId, StringComparison.Ordinal))
        {
            ShowDetails(selected);
        }
    }

    internal static IReadOnlyList<ExtensionMarketRelease> GetOtherAvailableReleases(
        ExtensionMarketDetails details,
        ExtensionMarketRelease latestRelease) =>
        details.Versions
            .Where(value => value.Status == "available"
                && value.Release.Version != latestRelease.Version)
            .Select(value => value.Release)
            .ToList();

    internal static string GetMarketReadyStatus(bool isCached) => isCached
        ? "网络市场暂不可用，正在显示最近一次已验签缓存"
        : "市场目录签名验证通过，下载时优先使用 Gitee";

    internal static string FormatDownloadProgress(long received, long total)
    {
        long safeReceived = Math.Max(0, received);
        if (total <= 0)
            return safeReceived > 0 ? FormatFileSize(safeReceived) : "";

        long safeTotal = Math.Max(0, total);
        double percentage = Math.Clamp(safeReceived * 100d / safeTotal, 0, 100);
        return $"{FormatFileSize(safeReceived)} / {FormatFileSize(safeTotal)} · {percentage:0}%";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size:0.0} {units[unitIndex]}";
    }

    private void SetBusy(bool busy, string? message = null, bool showProgress = false)
    {
        CatalogList.IsHitTestVisible = !busy;
        RefreshButton.IsEnabled = !busy;
        InstallButton.IsEnabled = false;
        InstallOtherVersionButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = showProgress;
        DownloadStatusText.Text = showProgress ? message ?? "正在下载扩展包" : "";
        DownloadProgressText.Text = "";
        if (message != null) SourceStatusText.Text = message;
        if (!busy
            && CatalogList.SelectedItem is ExtensionMarketDisplayItem selected
            && _selectedDetails != null)
            UpdateActionState(selected);
    }

    private void SetLocalMutationBusy(bool busy)
    {
        _localMutationBusy = busy;
        InstallLocalButton.IsEnabled = !_localMutationBusy;
    }

    private void ShowMarketReadyStatus()
    {
        if (_session != null)
            SourceStatusText.Text = GetMarketReadyStatus(_session.IsCached);
    }

    private static void TryDeleteDownloadedPackage(string path)
    {
        try { if (path.Length > 0 && File.Exists(path)) File.Delete(path); } catch { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshMarketAsync();

    private void ExternalLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            UpdateCheckService.OpenDownloadPage(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"无法打开链接：{ex.Message}", "打开链接失败");
        }
    }

    internal static Uri? TryCreateExternalUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class ExtensionMarketDisplayItem
{
    private InstalledExtensionRecord? _installed;
    private bool _isDownloading;

    internal ExtensionMarketDisplayItem(
        ExtensionMarketCatalogItem item,
        InstalledExtensionRecord? installed,
        bool localOnly = false)
    {
        Item = item;
        IsLocalOnly = localOnly;
        UpdateInstalled(installed);
    }

    internal ExtensionMarketCatalogItem Item { get; }
    internal bool IsLocalOnly { get; }
    public string Name => Item.Name;
    public string Summary => string.IsNullOrWhiteSpace(Item.Summary)
        ? AppLanguage.Get("本地安装的扩展")
        : Item.Summary;
    public string SourceLabel => IsLocalOnly
        ? AppLanguage.Get("本地")
        : Item.SourceAvailability == "closed-source" ? "闭源" : "开源";
    public string TypeLabel => Item.Type == "external-adapter" ? "适配器" : "脚本";
    public string AuthorText => IsLocalOnly
        ? AppLanguage.Get("本地安装")
        : string.IsNullOrWhiteSpace(Item.Publisher.DisplayName)
        ? Item.Publisher.Id
        : Item.Publisher.DisplayName;
    public string StatusText { get; private set; } = "未安装";

    internal static ExtensionMarketDisplayItem CreateLocal(InstalledExtensionRecord record)
    {
        string name = string.IsNullOrWhiteSpace(record.Name) ? record.Id : record.Name;
        var item = new ExtensionMarketCatalogItem
        {
            Id = record.Id,
            Name = name,
            Summary = record.Summary,
            Type = record.Type,
            SourceAvailability = "local",
            Publisher = new ExtensionMarketPublisher
            {
                Id = "local",
                DisplayName = AppLanguage.Get("本地安装")
            }
        };
        return new ExtensionMarketDisplayItem(item, record, localOnly: true);
    }

    internal void UpdateInstalled(InstalledExtensionRecord? installed)
    {
        _installed = installed;
        UpdateStatus();
    }

    internal void SetDownloading(bool downloading)
    {
        _isDownloading = downloading;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText = IsLocalOnly
            ? "已安装"
            : _isDownloading
            ? "下载中"
            : _installed == null
            ? "未安装"
            : !string.IsNullOrWhiteSpace(Item.LatestVersion)
                && _installed.Version != Item.LatestVersion
                    ? "待更新"
                    : "已安装";
    }
}

internal sealed record ExtensionMarketScreenshotItem(ImageSource Source, string Alt);
