using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using Microsoft.Win32;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

public partial class VideoCompressionDialog : Window
{
    private readonly VideoCompressionSource _source;
    private readonly VideoCompressionService _service;
    private List<GpuEncoderOption> _codecOptions = [];
    private string _av1Encoder;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _encoderProbeCancellation;
    private bool _allowClose;
    private bool _isCompressing;
    private bool _cancelRequested;
    private bool _cancelConfirmationOpen;
    private bool _hasPendingSuccess;
    private bool _suppressTargetChange;
    private bool _isInitialized;
    private long _autoTargetBytes;
    private int? _selectedPresetPercent;

    internal VideoCompressionDialog(
        VideoCompressionSource source,
        VideoCompressionService? service = null,
        IReadOnlyCollection<string>? validatedEncoders = null)
    {
        InitializeComponent();
        DialogWindowChrome.UseCloseButtonOnly(this);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _service = service ?? new VideoCompressionService();
        _av1Encoder = VideoCompressionPolicy.ResolveValidatedAv1Encoder(
            validatedEncoders);
        TargetSizeSlider.Minimum = VideoCompressionPolicy.MinimumTargetSizeMb;
        TargetSizeSlider.Maximum = Math.Max(
            VideoCompressionPolicy.MinimumTargetSizeMb,
            Math.Min(
                VideoCompressionPolicy.MaximumTargetSizeMb,
                source.FileSizeBytes / (double)VideoCompressionPolicy.Mebibyte));

        OrderText.Text = string.IsNullOrWhiteSpace(source.OrderId)
            ? AppLanguage.Get("未识别面单")
            : source.OrderId.Trim();
        OriginalSizeText.Text = VideoCompressionPolicy.FormatFileSize(source.FileSizeBytes);
        DurationText.Text = FormatDuration(source.DurationSeconds);
        SourceCodecText.Text = FormatCodec(source.VideoCodec);
        OutputHintText.Text = AppLanguage.Get("原录像不会被修改");

        LoadCodecOptions();
        SelectDefaultCodec();
        _isInitialized = true;
        ApplyDefaultTarget();
    }

    internal string OutputPath { get; private set; } = "";

    internal long OutputSizeBytes { get; private set; }

    private VideoCompressionCodecMode SelectedCodecMode =>
        CodecModeBox.SelectedItem is GpuEncoderOption option
            ? option.Value switch
            {
                "av1" when _av1Encoder.Length > 0 => VideoCompressionCodecMode.Av1,
                "h265" => VideoCompressionCodecMode.H265,
                _ => VideoCompressionCodecMode.H264
            }
            : VideoCompressionCodecMode.H264;

    private void LoadCodecOptions()
    {
        _codecOptions =
        [
            new GpuEncoderOption
            {
                Value = "h264",
                DisplayName = GpuEncoderOption.BuildDisplayName("h264")
            },
            new GpuEncoderOption
            {
                Value = "h265",
                DisplayName = GpuEncoderOption.BuildDisplayName("h265")
            }
        ];
        if (_av1Encoder.Length > 0)
        {
            _codecOptions.Add(new GpuEncoderOption
            {
                Value = "av1",
                DisplayName = GpuEncoderOption.BuildDisplayName("av1")
            });
        }
        CodecModeBox.ItemsSource = _codecOptions;
    }

    private void SelectDefaultCodec()
    {
        VideoCompressionCodecMode mode =
            VideoCompressionPolicy.ResolveDefaultCodecMode(
                _source.VideoCodec,
                _av1Encoder.Length > 0);
        CodecModeBox.SelectedValue = mode switch
        {
            VideoCompressionCodecMode.Av1 => "av1",
            VideoCompressionCodecMode.H265 => "h265",
            _ => "h264"
        };
    }

    private void CodecModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
            return;

        if (_autoTargetBytes > 0
            && VideoCompressionPolicy.MbToBytes(TargetSizeSlider.Value)
                == _autoTargetBytes)
        {
            ApplyDefaultTarget();
        }
        else
        {
            UpdateEstimate();
        }
    }

    private void TargetPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rawPercent }
            || !double.TryParse(
                rawPercent,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double percent)
            || _source.FileSizeBytes <= 0)
        {
            return;
        }

        double targetMb = Math.Max(
            VideoCompressionPolicy.MinimumTargetSizeMb,
            Math.Round(
                _source.FileSizeBytes / (double)VideoCompressionPolicy.Mebibyte
                * percent
                / 100d,
                1,
                MidpointRounding.AwayFromZero));
        _selectedPresetPercent = (int)percent;
        SetTargetValue(targetMb, isAutomatic: false);
    }

    private void TargetSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || _suppressTargetChange)
            return;

        _autoTargetBytes = 0;
        _selectedPresetPercent = null;
        UpdateEstimate();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCompressing || !TryReadTarget(out long targetBytes, out double targetMb))
            return;

        if (SelectedCodecMode == VideoCompressionCodecMode.Av1)
        {
            var probeCancellation = new CancellationTokenSource();
            _encoderProbeCancellation = probeCancellation;
            ConfigurationPanel.IsEnabled = false;
            StartButton.IsEnabled = false;
            bool encoderAvailable;
            try
            {
                encoderAvailable = await _service.IsEncoderAvailableAsync(
                    _av1Encoder,
                    probeCancellation.Token);
            }
            catch (OperationCanceledException)
                when (probeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "VideoCompression",
                    $"AV1 编码器预探测失败 encoder={_av1Encoder}, error={ex.Message}");
                ShowError(AppLanguage.Get(
                    "当前 FFmpeg 不可用已验证的 AV1 编码器，请改用 H.265"));
                return;
            }
            finally
            {
                ConfigurationPanel.IsEnabled = true;
                StartButton.IsEnabled =
                    ConfigurationPanel.Visibility == Visibility.Visible;
                if (ReferenceEquals(_encoderProbeCancellation, probeCancellation))
                    _encoderProbeCancellation = null;
                probeCancellation.Dispose();
            }
            if (!IsVisible || _allowClose)
                return;
            if (!TryReadTarget(out targetBytes, out targetMb))
                return;
            if (!encoderAvailable)
            {
                _av1Encoder = "";
                _codecOptions = _codecOptions
                    .Where(option => option.Value != "av1")
                    .ToList();
                CodecModeBox.ItemsSource = _codecOptions;
                CodecModeBox.SelectedValue = "h265";
                ShowError(AppLanguage.Get(
                    "当前 FFmpeg 不可用已验证的 AV1 编码器，请改用 H.265"));
                return;
            }
        }

        string initialDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(initialDirectory))
            initialDirectory = Path.GetDirectoryName(_source.FilePath) ?? "";
        var saveDialog = new SaveFileDialog
        {
            Title = AppLanguage.Get("保存导出压缩版本"),
            Filter = AppLanguage.Get("MP4 视频 (*.mp4)|*.mp4"),
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = VideoCompressionPolicy.BuildDefaultFileName(
                _source.OrderId,
                _source.FileName,
                SelectedCodecMode,
                DateTime.Now),
            InitialDirectory = initialDirectory
        };
        if (saveDialog.ShowDialog(this) != true)
            return;

        string outputPath = saveDialog.FileName;
        if (string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(_source.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            ShowError(AppLanguage.Get("导出压缩版本必须保存为另一个 MP4 文件"));
            return;
        }

        _cancellation = new CancellationTokenSource();
        _isCompressing = true;
        _cancelRequested = false;
        _hasPendingSuccess = false;
        ShowProgress(outputPath, targetMb);
        var progress = new Progress<VideoCompressionProgress>(UpdateProgress);

        try
        {
            VideoCompressionResult result = await _service.CompressAsync(
                new VideoCompressionRequest(
                    _source,
                    outputPath,
                    targetBytes,
                    SelectedCodecMode,
                    _av1Encoder),
                progress,
                _cancellation.Token);
            if (_cancelRequested)
            {
                _allowClose = true;
                Close();
                return;
            }
            OutputPath = result.OutputPath;
            OutputSizeBytes = result.FileSizeBytes;
            _hasPendingSuccess = true;
            if (!_cancelConfirmationOpen)
                CompleteSuccessfulExport();
        }
        catch (OperationCanceledException)
        {
            _allowClose = true;
            Close();
        }
        catch (VideoCompressionException ex)
        {
            if (_cancelRequested)
            {
                _allowClose = true;
                Close();
                return;
            }
            RuntimeLog.Warn(
                "VideoCompression",
                $"压缩失败 kind={ex.Kind}, message={ex.Message}");
            ShowConfiguration();
            ShowError(GetFailureText(ex.Kind));
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("VideoCompression", "压缩副本界面异常", ex);
            ShowConfiguration();
            ShowError(AppLanguage.Get("导出失败，请稍后重试"));
        }
        finally
        {
            _isCompressing = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isCompressing)
        {
            _allowClose = true;
            DialogResult = false;
            return;
        }

        bool confirmed;
        _cancelConfirmationOpen = true;
        try
        {
            confirmed = AppDialog.Confirm(
                this,
                AppLanguage.Get("确定取消本次导出吗？临时文件会自动清理"),
                AppLanguage.Get("取消导出"),
                AppDialogSeverity.Information,
                confirmText: AppLanguage.Get("取消导出"),
                cancelText: AppLanguage.Get("继续导出"),
                isDangerous: false);
        }
        finally
        {
            _cancelConfirmationOpen = false;
        }

        if (!confirmed)
        {
            if (_hasPendingSuccess)
                CompleteSuccessfulExport();
            return;
        }

        if (_hasPendingSuccess)
        {
            _allowClose = true;
            DialogResult = false;
            return;
        }

        RequestCancellation();
    }

    private void CompleteSuccessfulExport()
    {
        if (_cancelRequested)
        {
            _allowClose = true;
            DialogResult = false;
            return;
        }

        _allowClose = true;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        CancelButton_Click(CancelButton, new RoutedEventArgs());
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _encoderProbeCancellation?.Cancel();
        if (_allowClose)
            return;

        if (_isCompressing)
        {
            e.Cancel = true;
            CancelButton_Click(CancelButton, new RoutedEventArgs());
            return;
        }

    }

    private void RequestCancellation()
    {
        if (_cancellation == null || _cancellation.IsCancellationRequested)
            return;

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = AppLanguage.Get("正在取消");
        ProgressSummaryText.Text = AppLanguage.Get("正在取消导出");
        ProgressDetailText.Text = AppLanguage.Get("正在清理临时文件，请稍候");
        CompressionProgressBar.IsIndeterminate = true;
        _cancellation.Cancel();
    }

    private void ApplyDefaultTarget()
    {
        _selectedPresetPercent = null;
        long targetBytes = VideoCompressionPolicy.GetDefaultTargetBytes(
            _source.FileSizeBytes,
            SelectedCodecMode);
        SetTargetValue(
            targetBytes / (double)VideoCompressionPolicy.Mebibyte,
            isAutomatic: true);
    }

    private void SetTargetValue(double targetMb, bool isAutomatic)
    {
        _suppressTargetChange = true;
        TargetSizeSlider.Value = Math.Clamp(
            targetMb,
            TargetSizeSlider.Minimum,
            TargetSizeSlider.Maximum);
        _suppressTargetChange = false;
        _autoTargetBytes = isAutomatic
            ? VideoCompressionPolicy.MbToBytes(targetMb)
            : 0;
        UpdateEstimate();
    }

    private bool TryReadTarget(out long targetBytes, out double targetMb)
    {
        targetBytes = 0;
        targetMb = TargetSizeSlider.Value;
        if (!VideoCompressionPolicy.IsTargetWithinSupportedRange(targetMb))
        {
            ShowError(AppLanguage.Format(
                "目标大小必须在 {0} 到 {1} MB 之间",
                VideoCompressionPolicy.MinimumTargetSizeMb,
                VideoCompressionPolicy.MaximumTargetSizeMb));
            return false;
        }

        targetBytes = VideoCompressionPolicy.MbToBytes(targetMb);
        if (!VideoCompressionPolicy.TryValidateTarget(
                _source.FileSizeBytes,
                targetBytes,
                _source.DurationSeconds,
                out VideoCompressionFailureKind failure))
        {
            ShowError(GetFailureText(failure));
            return false;
        }

        HideError();
        return true;
    }

    private void UpdateEstimate()
    {
        HideError();
        double targetMb = TargetSizeSlider.Value;
        if (!VideoCompressionPolicy.IsTargetWithinSupportedRange(targetMb))
        {
            UpdatePresetHighlight(null);
            EstimatedSizeText.Text = "—";
            EstimateDetailText.Text = AppLanguage.Format(
                "请输入 {0} 到 {1} MB",
                VideoCompressionPolicy.MinimumTargetSizeMb,
                VideoCompressionPolicy.MaximumTargetSizeMb);
            StartButton.IsEnabled = false;
            return;
        }

        long targetBytes = VideoCompressionPolicy.MbToBytes(targetMb);
        UpdatePresetHighlight(targetBytes);
        if (!VideoCompressionPolicy.TryValidateTarget(
                _source.FileSizeBytes,
                targetBytes,
                _source.DurationSeconds,
                out VideoCompressionFailureKind failure))
        {
            EstimatedSizeText.Text = VideoCompressionPolicy.FormatFileSize(targetBytes);
            EstimateDetailText.Text = GetFailureText(failure);
            StartButton.IsEnabled = false;
            return;
        }

        int reduction = VideoCompressionPolicy.CalculateReductionPercent(
            _source.FileSizeBytes,
            targetBytes);
        double seconds = VideoCompressionPolicy.EstimateSeconds(
            _source.DurationSeconds,
            SelectedCodecMode,
            _av1Encoder);
        EstimatedSizeText.Text = AppLanguage.Format(
            "约 {0}",
            VideoCompressionPolicy.FormatFileSize(targetBytes));
        EstimateDetailText.Text =
            AppLanguage.Format("比原文件减少 {0}", $"{reduction}%")
            + " · "
            + AppLanguage.Format("预计耗时：约 {0}", FormatEstimate(seconds));
        StartButton.IsEnabled = true;
    }

    private void UpdatePresetHighlight(long? targetBytes)
    {
        int? activePercent = _selectedPresetPercent
            ?? (targetBytes.HasValue
                ? VideoCompressionPolicy.ResolvePresetPercent(
                    _source.FileSizeBytes,
                    targetBytes.Value)
                : null);
        Style normal = (Style)FindResource("PresetButtonStyle");
        Style active = (Style)FindResource("PresetButtonActiveStyle");
        ApplyStyle(TargetPreset30Button, 30);
        ApplyStyle(TargetPreset50Button, 50);
        ApplyStyle(TargetPreset70Button, 70);

        void ApplyStyle(Button button, int percent)
        {
            Style style = activePercent == percent ? active : normal;
            if (!ReferenceEquals(button.Style, style))
                button.Style = style;
        }
    }

    private void ShowProgress(string outputPath, double targetMb)
    {
        ConfigurationPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        CancelButton.Content = AppLanguage.Get("取消导出");
        DialogTitleText.Text = AppLanguage.Get("正在导出压缩版本");
        DialogSubtitleText.Text = AppLanguage.Get("正在处理录像，请稍候");
        OutputHintText.Text = Path.GetFileName(outputPath);
        ProgressSummaryText.Text = AppLanguage.Get("正在准备压缩");
        ProgressDetailText.Text = AppLanguage.Format(
            "目标约 {0} MB",
            targetMb.ToString("0.#", CultureInfo.CurrentCulture));
        CompressionProgressBar.TargetValue = 0;
        CompressionProgressBar.IsIndeterminate = true;
        HideError();
    }

    private void ShowConfiguration()
    {
        ConfigurationPanel.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible;
        StartButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        CancelButton.Content = AppLanguage.Get("取消");
        DialogTitleText.Text = AppLanguage.Get("导出压缩版本");
        DialogSubtitleText.Text = AppLanguage.Get("生成较小的新文件，不修改原录像");
        OutputHintText.Text = AppLanguage.Get("原录像不会被修改");
    }

    private void UpdateProgress(VideoCompressionProgress value)
    {
        CompressionProgressBar.IsIndeterminate = value.Stage == VideoCompressionStage.Preparing;
        if (!CompressionProgressBar.IsIndeterminate)
            CompressionProgressBar.TargetValue = Math.Clamp(value.Percent, 0, 100);

        ProgressSummaryText.Text = value.Stage switch
        {
            VideoCompressionStage.Preparing => AppLanguage.Get("正在准备压缩"),
            VideoCompressionStage.VerifyingSize => AppLanguage.Get("正在确认输出大小"),
            VideoCompressionStage.Retrying => AppLanguage.Get("正在重新压缩以缩小文件"),
            VideoCompressionStage.Publishing => AppLanguage.Get("正在保存导出压缩版本"),
            _ => AppLanguage.Format("正在压缩 {0}%", Math.Clamp(value.Percent, 0, 100))
        };
        if (value.Stage == VideoCompressionStage.Preparing)
            ProgressDetailText.Text = AppLanguage.Get("正在读取录像并计算压缩参数");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        ErrorText.BringIntoView();
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private static string GetFailureText(VideoCompressionFailureKind kind) =>
        kind switch
        {
            VideoCompressionFailureKind.SourceMissing =>
                AppLanguage.Get("录像文件不存在或当前不可访问"),
            VideoCompressionFailureKind.SourceChanged =>
                AppLanguage.Get("录像仍在写入或文件中途发生变化，请等录像完成后重试"),
            VideoCompressionFailureKind.Av1Unavailable =>
                AppLanguage.Get("没有已验证可用的 AV1 编码器，请改用 H.265"),
            VideoCompressionFailureKind.SourceTooSmall =>
                AppLanguage.Get("当前文件已很小，无需压缩"),
            VideoCompressionFailureKind.TargetTooLarge =>
                AppLanguage.Get("目标大小不能超过原文件"),
            VideoCompressionFailureKind.TargetTooSmall =>
                AppLanguage.Get("目标大小太小，无法生成可播放的视频"),
            VideoCompressionFailureKind.FfmpegUnavailable =>
                AppLanguage.Get("未找到视频处理组件 ffmpeg.exe"),
            VideoCompressionFailureKind.SizeUnreachable =>
                AppLanguage.Get("目标大小太小，已停止压缩"),
            VideoCompressionFailureKind.TranscodeStalled =>
                AppLanguage.Get("视频处理长时间没有进展，已停止压缩"),
            VideoCompressionFailureKind.TranscodeTimeout =>
                AppLanguage.Get("视频处理超时，已停止压缩"),
            VideoCompressionFailureKind.InvalidOutput =>
                AppLanguage.Get("导出压缩版本必须保存为另一个 MP4 文件"),
            _ => AppLanguage.Get("视频处理失败")
        };

    private static string FormatCodec(string codec) =>
        VideoCompressionPolicy.NormalizeCodec(codec) switch
        {
            "h264" => "H.264",
            "h265" => "H.265",
            "" => AppLanguage.Get("编码未知"),
            string value => value.ToUpperInvariant()
        };

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
            return "—";

        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatEstimate(double seconds)
    {
        int rounded = Math.Max(1, (int)Math.Ceiling(seconds));
        int minutes = rounded / 60;
        int remainingSeconds = rounded % 60;
        if (minutes <= 0)
            return AppLanguage.Format("{0} 秒", remainingSeconds);
        if (remainingSeconds == 0)
            return AppLanguage.Format("{0} 分钟", minutes);
        return AppLanguage.Format("{0} 分 {1} 秒", minutes, remainingSeconds);
    }
}
