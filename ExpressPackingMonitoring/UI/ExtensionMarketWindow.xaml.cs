using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI;

public partial class ExtensionMarketWindow : Window
{
    private readonly ExtensionMarketClient _marketClient;
    private readonly ExtensionInstallationService _installationService;
    private readonly ExtensionPackageService _packageService;
    private readonly string? _initialExtensionId;
    private readonly ObservableCollection<ExtensionMarketDisplayItem> _items = new();
    private CancellationTokenSource? _operationCancellation;
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
        _marketClient = marketClient;
        _installationService = installationService;
        _packageService = packageService;
        _initialExtensionId = initialExtensionId;
        CatalogList.ItemsSource = _items;
        Loaded += async (_, _) => await RefreshMarketAsync();
        Closed += (_, _) => _operationCancellation?.Cancel();
    }

    private async Task RefreshMarketAsync()
    {
        SetBusy(true, "正在读取经过签名的市场目录");
        try
        {
            _operationCancellation?.Cancel();
            _operationCancellation = new CancellationTokenSource();
            _session = await _marketClient.LoadCatalogAsync(_operationCancellation.Token);
            IReadOnlyDictionary<string, InstalledExtensionRecord> installed = _installationService
                .GetInstalled()
                .ToDictionary(value => value.Id, StringComparer.Ordinal);
            _items.Clear();
            foreach (ExtensionMarketCatalogItem item in _session.Catalog.Extensions)
            {
                installed.TryGetValue(item.Id, out InstalledExtensionRecord? record);
                _items.Add(new ExtensionMarketDisplayItem(item, record));
            }
            ShowMarketReadyStatus();
            if (_items.Count > 0)
            {
                CatalogList.SelectedItem = _initialExtensionId == null
                    ? _items[0]
                    : _items.FirstOrDefault(value => value.Item.Id == _initialExtensionId) ?? _items[0];
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SourceStatusText.Text = ex.Message;
            AppDialog.Error(this, ex.Message, "扩展市场不可用");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session == null || CatalogList.SelectedItem is not ExtensionMarketDisplayItem selected) return;
        bool restoreReadyStatus = false;
        SetBusy(true, "正在读取扩展详情");
        try
        {
            _selectedDetails = await _marketClient.LoadDetailsAsync(
                _session,
                selected.Item,
                _operationCancellation?.Token ?? CancellationToken.None);
            _selectedRelease = _selectedDetails.Versions
                .FirstOrDefault(value => value.Status == "available"
                    && value.Release.Version == selected.Item.LatestVersion)?.Release
                ?? _selectedDetails.Versions
                    .FirstOrDefault(value => value.Status == "available")?.Release;
            ShowDetails(selected);
            restoreReadyStatus = true;
        }
        catch (Exception ex)
        {
            _selectedDetails = null;
            _selectedRelease = null;
            InstallButton.IsEnabled = false;
            InstallOtherVersionButton.IsEnabled = false;
            SourceStatusText.Text = $"读取扩展详情失败：{ex.Message}";
            AppDialog.Error(this, $"读取扩展详情失败：{ex.Message}", "读取失败");
        }
        finally
        {
            SetBusy(false);
            if (restoreReadyStatus) ShowMarketReadyStatus();
        }
    }

    private void ShowDetails(ExtensionMarketDisplayItem selected)
    {
        ExtensionMarketDetails details = _selectedDetails!;
        ExtensionNameText.Text = details.Extension.Name;
        PublisherText.Text = $"作者：{details.Publisher.DisplayName}";
        LatestVersionText.Text = _selectedRelease == null
            ? "最新版本：暂无"
            : $"最新版本：{_selectedRelease.Version}";
        DescriptionText.Text = details.Extension.Description;
        bool external = details.Extension.Type == "external-adapter";
        bool closedSource = details.Extension.SourceAvailability == "closed-source";
        RiskPanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        RiskText.Text = closedSource
            ? "⚠ 闭源外部程序。PackingProof 无法审查其内部行为，也无法限制它访问网络、文件或其他系统资源"
            : "⚠ 外部程序由用户手动运行。PackingProof 不会自动启动，也无法限制它访问网络、文件或其他系统资源";
        UpdateActionState(selected);
    }

    private void UpdateActionState(ExtensionMarketDisplayItem selected)
    {
        InstalledExtensionRecord? installed = _installationService.GetInstalled()
            .FirstOrDefault(value => value.Id == selected.Item.Id);
        bool compatible = _selectedRelease != null
            && IsCompatible(_selectedRelease.Compatibility.MinPackingProofVersion);
        InstallButton.IsEnabled = _selectedRelease != null && compatible;
        InstallButton.Content = installed == null ? "安装" :
            installed.Version == _selectedRelease?.Version ? "重新安装" : "更新";
        InstalledVersionText.Text = installed == null
            ? "尚未安装"
            : $"已安装：{installed.Version}";
        CompatibilityText.Text = _selectedRelease == null
            ? "没有可安装版本"
            : compatible
                ? $"需要 PackingProof {_selectedRelease.Compatibility.MinPackingProofVersion} 或更高版本"
                : $"需要 PackingProof {_selectedRelease.Compatibility.MinPackingProofVersion} 或更高版本，当前版本无法安装";
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
            bool compatible = IsCompatible(release.Compatibility.MinPackingProofVersion);
            string installedSuffix = installed?.Version == release.Version ? "（已安装）" : "";
            var item = new MenuItem
            {
                Header = $"{release.Version}{installedSuffix}  ·  需要 PackingProof {release.Compatibility.MinPackingProofVersion}+",
                IsEnabled = compatible,
                ToolTip = compatible ? null : "当前 PackingProof 版本过低"
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
                    ? "这是闭源外部程序。PackingProof 无法审查其代码，也不会限制程序访问网络、文件或其他系统资源。确认下载并安装吗？"
                    : "这是需要用户手动运行的外部程序。PackingProof 不会自动启动，也无法限制程序访问系统资源。确认下载并安装吗？",
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
                    DownloadProgress.Value = Math.Min(value.Received, value.Total);
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
            SourceStatusText.Text = $"安装扩展失败：{ex.Message}";
            AppDialog.Error(this, $"安装扩展失败：{ex.Message}", "安装失败");
        }
        finally
        {
            TryDeleteDownloadedPackage(packagePath);
            selected.SetDownloading(false);
            CatalogList.Items.Refresh();
            SetBusy(false);
            if (restoreReadyStatus) ShowMarketReadyStatus();
        }
    }

    private async void InstallLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PackingProof 扩展 (*.ppext)|*.ppext|油猴脚本 (*.user.js)|*.user.js",
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
        try
        {
            ExtensionPackageInspection inspection = _packageService.Inspect(dialog.FileName);
            string warning = inspection.Manifest.Type == "external-adapter"
                ? "该 PPEXT 未经过市场审核，并且包含需要用户手动运行的外部程序。PackingProof 无法限制程序访问系统资源"
                : "该 PPEXT 未经过市场审核。请只安装来自可信开发者的扩展";
            if (!AppDialog.Confirm(
                    this,
                    warning + "\n\n是否继续安装？",
                    "安装未经市场审核的扩展",
                    AppDialogSeverity.Warning,
                    "继续安装"))
                return;
            ExtensionInstallResult result = await Task.Run(() =>
                _installationService.Install(dialog.FileName, inspection.Manifest.Id));
            ShowInstallResult(result);
            OpenInstalledExtensionDirectory(result.Record);
            RefreshInstalledState(result.Record.Id);
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"安装本地扩展失败：{ex.Message}", "安装失败");
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
            ? "这会删除 PackingProof 保存的脚本源文件，但无法替你从浏览器脚本管理器中卸载已经安装的副本"
            : runningProcesses.Count > 0
                ? $"检测到扩展仍在运行：{processList}\n\n删除前需要终止这些程序。PackingProof 只会终止安装目录内的扩展程序，不会删除扩展自己保存的配置、凭据或业务数据"
                : "这只会删除 PackingProof 管理的安装目录，不会删除扩展自己保存的配置、凭据或业务数据";
        string confirmText = runningProcesses.Count > 0 ? "终止并删除" : "确认删除";
        if (!AppDialog.Confirm(this, message + "\n\n确定继续吗？", "删除扩展", AppDialogSeverity.Warning, confirmText))
            return;

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
            : $"\n\n提示：\n· {string.Join("\n· ", result.Warnings)}";
        AppDialog.Information(
            this,
            result.Record.Type == "userscript"
                ? "扩展脚本已导入，可在安装订单联动中选择" + warnings
                : "外部扩展已安装。PackingProof 不会自动运行它，关闭此提示后将打开安装目录" + warnings,
            "安装完成");
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

    private void RefreshDisplayItems()
    {
        IReadOnlyDictionary<string, InstalledExtensionRecord> installed = _installationService.GetInstalled()
            .ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (ExtensionMarketDisplayItem item in _items)
        {
            installed.TryGetValue(item.Item.Id, out InstalledExtensionRecord? record);
            item.UpdateInstalled(record);
        }
        CatalogList.Items.Refresh();
    }

    private void RefreshInstalledState(string extensionId)
    {
        RefreshDisplayItems();
        if (_selectedDetails != null
            && CatalogList.SelectedItem is ExtensionMarketDisplayItem selected
            && string.Equals(selected.Item.Id, extensionId, StringComparison.Ordinal))
        {
            UpdateActionState(selected);
        }
    }

    private static bool IsCompatible(string minimumVersion)
    {
        if (!Version.TryParse(NormalizeVersion(minimumVersion), out Version? minimum)) return false;
        return Version.TryParse(NormalizeVersion(AppVersion.Current), out Version? current)
            && current >= minimum;
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        string[] parts = normalized.Split('.');
        return parts.Length switch
        {
            1 => normalized + ".0.0",
            2 => normalized + ".0",
            _ => normalized
        };
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
        InstallLocalButton.IsEnabled = !busy;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class ExtensionMarketDisplayItem
{
    private InstalledExtensionRecord? _installed;
    private bool _isDownloading;

    internal ExtensionMarketDisplayItem(ExtensionMarketCatalogItem item, InstalledExtensionRecord? installed)
    {
        Item = item;
        UpdateInstalled(installed);
    }

    internal ExtensionMarketCatalogItem Item { get; }
    public string Name => Item.Name;
    public string Summary => Item.Summary;
    public string Badge => Item.SourceAvailability == "closed-source"
        ? "闭源外部程序"
        : Item.Type == "external-adapter" ? "外部程序" : "";
    public string AuthorText => string.IsNullOrWhiteSpace(Item.Publisher.DisplayName)
        ? Item.Publisher.Id
        : Item.Publisher.DisplayName;
    public string StatusText { get; private set; } = "未安装";

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
        StatusText = _isDownloading
            ? "下载中"
            : _installed == null
            ? "未安装"
            : !string.IsNullOrWhiteSpace(Item.LatestVersion)
                && _installed.Version != Item.LatestVersion
                    ? "待更新"
                    : "已安装";
    }
}
