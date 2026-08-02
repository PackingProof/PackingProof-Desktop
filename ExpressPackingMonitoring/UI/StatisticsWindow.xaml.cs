using ExpressPackingMonitoring.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.UI
{
    public class ChartItem
    {
        public string DateLabel { get; set; } = "";
        public string DateSub { get; set; } = "";
        public string DateFull { get; set; } = "";
        public int Pieces { get; set; }
        public double BarRatio { get; set; }
        public string SizeText { get; set; } = "";
        public string TotalTime { get; set; } = "";
    }

    public class ChartAxisLabel
    {
        public string DateLabel { get; set; } = "";
        public string DateSub { get; set; } = "";
    }

    public partial class StatisticsWindow : Window, INotifyPropertyChanged
    {
        private readonly VideoDatabase _db;
        private bool _isInternalUpdating = false; // 防止日期切换时触发多次刷新
        private IReadOnlyList<DailyStat> _cachedHistory = Array.Empty<DailyStat>();
        private string _cachedGroupMode = "day";
        private RefreshRequest? _pendingRefresh;
        private bool _refreshLoopRunning;
        private bool _isClosed;
        private int _refreshRequestVersion;
        private readonly DispatcherTimer _axisLabelResizeTimer;

        public ObservableCollection<ChartItem> ChartData { get; } = new();
        public ObservableCollection<ChartAxisLabel> XAxisLabels { get; } = new();
        public ObservableCollection<string> YAxisLabels { get; } = new();

        private string _summaryPieces = "0 件";
        public string SummaryPieces { get => _summaryPieces; set { _summaryPieces = value; OnPropertyChanged(nameof(SummaryPieces)); } }

        private string _summarySize = "0 MB";
        public string SummarySize { get => _summarySize; set { _summarySize = value; OnPropertyChanged(nameof(SummarySize)); } }

        private string _summaryDuration = "0h 0m";
        public string SummaryDuration { get => _summaryDuration; set { _summaryDuration = value; OnPropertyChanged(nameof(SummaryDuration)); } }

        private string _summaryAvgTime = "00:00";
        public string SummaryAvgTime { get => _summaryAvgTime; set { _summaryAvgTime = value; OnPropertyChanged(nameof(SummaryAvgTime)); } }

        public StatisticsWindow(VideoDatabase db)
        {
            InitializeComponent();
            _db = db;
            this.DataContext = this;
            PresetCombo.SelectedIndex = 2;
            _axisLabelResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _axisLabelResizeTimer.Tick += (_, _) =>
            {
                _axisLabelResizeTimer.Stop();
                UpdateAxisLabels();
            };

            this.Loaded += (s, e) => {
                ApplyPreset("Last7");
                RequestDataRefresh();
            };
            this.Closed += (_, _) =>
            {
                _isClosed = true;
                _axisLabelResizeTimer.Stop();
            };
        }

        private void RequestDataRefresh()
        {
            if (!IsLoaded || _isClosed)
                return;

            DateTime start = PickerStart.SelectedDate ?? DateTime.Now.AddDays(-6);
            DateTime end = PickerEnd.SelectedDate ?? DateTime.Now;
            if (start > end)
                (start, end) = (end, start);
            string groupMode = (GroupCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "day";

            _pendingRefresh = new RefreshRequest(start, end, groupMode);
            _refreshRequestVersion++;
            if (!_refreshLoopRunning)
                _ = ProcessRefreshQueueAsync();
        }

        private async Task ProcessRefreshQueueAsync()
        {
            _refreshLoopRunning = true;
            try
            {
                while (!_isClosed && _pendingRefresh is RefreshRequest request)
                {
                    _pendingRefresh = null;
                    int requestVersion = _refreshRequestVersion;
                    List<DailyStat> history;
                    try
                    {
                        history = await Task.Run(() =>
                            _db.GetAggregatedStats(request.Start, request.End, request.GroupMode));
                    }
                    catch (Exception ex)
                    {
                        if (requestVersion == _refreshRequestVersion && !_isClosed)
                            AppDialog.ShowMessage(this, $"加载统计数据失败：{ex.Message}", "统计错误", AppDialogSeverity.Warning);
                        continue;
                    }

                    if (_isClosed || requestVersion != _refreshRequestVersion)
                        continue;

                    _cachedHistory = history;
                    _cachedGroupMode = request.GroupMode;
                    RenderChart();
                }
            }
            finally
            {
                _refreshLoopRunning = false;
                if (!_isClosed && _pendingRefresh.HasValue)
                    _ = ProcessRefreshQueueAsync();
            }
        }

        private void RenderChart()
        {
            ChartData.Clear();
            XAxisLabels.Clear();
            YAxisLabels.Clear();

            if (_cachedHistory.Count == 0)
            {
                ResetSummary();
                return;
            }

            // 1. 计算最大值用于 Y 轴缩放
            double maxVal = 0;
            foreach (var h in _cachedHistory)
            {
                double val = 0;
                if (ModePieces.IsChecked == true) val = h.TotalPieces;
                else if (ModeDuration.IsChecked == true) val = h.TotalDurationSec;
                else val = h.TotalBytes / 1024.0 / 1024.0; // MB
                if (val > maxVal) maxVal = val;
            }
            if (maxVal < 1) maxVal = 1;

            // 2. 生成 Y 轴刻度
            for (int i = 5; i >= 0; i--)
            {
                double tickVal = (maxVal / 5.0) * i;
                if (ModeDuration.IsChecked == true) YAxisLabels.Add(TimeSpan.FromSeconds(tickVal).ToString(@"hh\:mm"));
                else if (ModeSize.IsChecked == true) YAxisLabels.Add($"{tickVal:F0}M");
                else YAxisLabels.Add(tickVal.ToString("F0"));
            }

            // 3. 生成 X 轴数据
            int totalPieces = 0;
            long totalBytes = 0;
            double totalSec = 0;

            for (int i = 0; i < _cachedHistory.Count; i++)
            {
                var h = _cachedHistory[i];
                double currentVal = ModePieces.IsChecked == true ? h.TotalPieces :
                                    ModeDuration.IsChecked == true ? h.TotalDurationSec :
                                    h.TotalBytes / 1024.0 / 1024.0;

                totalPieces += h.TotalPieces;
                totalBytes += h.TotalBytes;
                totalSec += h.TotalDurationSec;

                // 【修复核心】：处理非日期格式的字符串 (W11, 2024-03等)
                (string dateLabel, string subLabel, string dateFull) = FormatChartDate(h.Date, _cachedGroupMode);

                ChartData.Add(new ChartItem
                {
                    DateFull = dateFull,
                    DateLabel = dateLabel,
                    DateSub = subLabel,
                    Pieces = h.TotalPieces,
                    TotalTime = TimeSpan.FromSeconds(h.TotalDurationSec).ToString(@"hh\:mm\:ss"),
                    SizeText = FormatSize(h.TotalBytes),
                    BarRatio = Math.Clamp(currentVal / maxVal, 0, 1)
                });
            }

            UpdateAxisLabels();

            // 4. 更新汇总
            SummaryPieces = $"{totalPieces} 件";
            SummarySize = FormatSize(totalBytes);
            SummaryDuration = $"{(int)totalSec / 3600}h {((int)totalSec % 3600) / 60}m";
            SummaryAvgTime = totalPieces > 0 ? TimeSpan.FromSeconds(totalSec / totalPieces).ToString(@"mm\:ss") : "00:00";
        }

        private void ApplyPreset(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return;

            _isInternalUpdating = true;
            (DateTime start, DateTime end) = GetPresetRange(tag, DateTime.Now);
            PickerStart.SelectedDate = start;
            PickerEnd.SelectedDate = end;
            _isInternalUpdating = false;
        }

        internal static (DateTime Start, DateTime End) GetPresetRange(string tag, DateTime now)
        {
            DateTime today = now.Date;
            return tag switch
            {
                "Week" => (today.AddDays(-((7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7)), today),
                "Month" => (new DateTime(today.Year, today.Month, 1), today),
                "Last7" => (today.AddDays(-6), today),
                "Last30" => (today.AddDays(-29), today),
                "LastYear" => (today.AddYears(-1).AddDays(1), today),
                "All" => (today.AddYears(-2), today),
                _ => (today.AddDays(-6), today)
            };
        }

        private void RangePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PresetCombo.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? string.Empty;
                ApplyPreset(tag);
                RequestDataRefresh();
            }
        }

        private void OnQueryFilterChanged(object sender, EventArgs e)
        {
            if (!_isInternalUpdating)
                RequestDataRefresh();
        }

        private void OnMetricChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInternalUpdating)
                RenderChart();
        }

        private void ChartPlot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded || _isClosed || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1)
                return;

            _axisLabelResizeTimer.Stop();
            _axisLabelResizeTimer.Start();
        }

        private void UpdateAxisLabels()
        {
            XAxisLabels.Clear();
            foreach (ChartAxisLabel label in BuildAxisLabels(ChartData, ChartPlot.ActualWidth))
                XAxisLabels.Add(label);
        }

        internal static IReadOnlyList<ChartAxisLabel> BuildAxisLabels(
            IReadOnlyList<ChartItem> chartData,
            double availableWidth)
        {
            if (chartData.Count == 0)
                return Array.Empty<ChartAxisLabel>();

            int labelCapacity = Math.Clamp((int)Math.Floor(Math.Max(0, availableWidth) / 110), 2, 8);
            int labelCount = Math.Min(chartData.Count, labelCapacity);
            var labels = new List<ChartAxisLabel>(labelCount);
            for (int position = 0; position < labelCount; position++)
            {
                int index = labelCount == 1
                    ? 0
                    : (int)Math.Round(position * (chartData.Count - 1.0) / (labelCount - 1));
                ChartItem item = chartData[index];
                labels.Add(new ChartAxisLabel
                {
                    DateLabel = item.DateLabel,
                    DateSub = item.DateSub
                });
            }

            return labels;
        }

        internal static (string Label, string SubLabel, string FullLabel) FormatChartDate(
            string value,
            string groupMode)
        {
            if (groupMode == "day" && DateTime.TryParse(value, out DateTime day))
            {
                string weekDay = GetChineseDayOfWeek(day);
                return (day.ToString("MM-dd"), weekDay, $"{day:yyyy年M月d日} {weekDay}");
            }

            if (groupMode == "week")
            {
                string[] parts = value.Split("-W", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                    return (parts[0], $"第{parts[1]}周", $"{parts[0]}年第{parts[1]}周");
            }

            if (groupMode == "month" && DateTime.TryParse($"{value}-01", out DateTime month))
                return (month.ToString("yyyy"), month.ToString("MM月"), month.ToString("yyyy年M月"));

            if (groupMode == "year" && int.TryParse(value, out int year))
                return ($"{year}年", "", $"{year}年");

            return (value, "", value);
        }

        private void ResetSummary()
        {
            SummaryPieces = "0 件"; SummarySize = "0 MB"; 
            SummaryDuration = "0h 0m"; SummaryAvgTime = "00:00";
        }

        private static string GetChineseDayOfWeek(DateTime dt) => dt.DayOfWeek switch {
            DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一", DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三", DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六", _ => ""
        };

        private string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
            return $"{bytes / 1024.0 / 1024:F1} MB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly record struct RefreshRequest(DateTime Start, DateTime End, string GroupMode);
    }
}
