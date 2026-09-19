using ExpressPackingMonitoring.Data;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal sealed record OrderNumberExportRow(
    string TrackingNumber,
    string SourceOrderIds,
    string Mode,
    DateTime FirstRecordingTime,
    string SourceDevices);

internal enum OrderNumberExportStage
{
    Reading,
    Organizing,
    Writing,
    Finalizing
}

internal sealed record OrderNumberExportProgress(
    OrderNumberExportStage Stage,
    int Processed,
    int Total,
    string Message,
    bool IsIndeterminate = false);

internal static class OrderNumberExportService
{
    private sealed class ExcelRow
    {
        [ExcelColumn(Name = "快递单号", Index = 0, Width = 20)]
        public string TrackingNumber { get; init; } = "";

        [ExcelColumn(Name = "平台订单号", Index = 1, Width = 20)]
        public string SourceOrderIds { get; init; } = "";

        [ExcelColumn(Name = "发退货", Index = 2, Width = 12)]
        public string Mode { get; init; } = "";

        [ExcelColumn(Name = "首次录像时间", Index = 3, Width = 20)]
        public string FirstRecordingTime { get; init; } = "";

        [ExcelColumn(Name = "来源设备", Index = 4, Width = 16)]
        public string SourceDevices { get; init; } = "";
    }

    internal static IReadOnlyList<OrderNumberExportRow> BuildRows(
        IEnumerable<OrderNumberExportSource> sources,
        CancellationToken cancellationToken = default,
        IProgress<OrderNumberExportProgress>? progress = null,
        IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null,
        string localDeviceName = "")
    {
        IReadOnlyList<OrderNumberExportSource> sourceList = sources as IReadOnlyList<OrderNumberExportSource>
            ?? sources.ToList();
        var groups = new Dictionary<string, ExportGroup>(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new OrderNumberExportProgress(
            OrderNumberExportStage.Organizing,
            0,
            sourceList.Count,
            "正在整理单号数据"));

        for (int index = 0; index < sourceList.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OrderNumberExportSource source = sourceList[index];
            string trackingNumber = source.TrackingNumber?.Trim() ?? "";
            if (trackingNumber.Length > 0)
            {
                string mode = source.Mode?.Trim() ?? "";
                string key = trackingNumber + "\u001f" + mode;
                if (!groups.TryGetValue(key, out ExportGroup? group))
                {
                    group = new ExportGroup(trackingNumber, mode, source.StartTime);
                    groups.Add(key, group);
                }
                group.Add(source, currentSourceDeviceNames, localDeviceName);
            }

            int processed = index + 1;
            if (processed == sourceList.Count || processed % 100 == 0)
            {
                progress?.Report(new OrderNumberExportProgress(
                    OrderNumberExportStage.Organizing,
                    processed,
                    sourceList.Count,
                    "正在整理单号数据"));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return groups.Values
            .Select(group => group.ToRow())
            .OrderByDescending(row => row.FirstRecordingTime)
            .ThenBy(row => row.TrackingNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static void Export(
        string targetPath,
        IReadOnlyList<OrderNumberExportRow> rows,
        CancellationToken cancellationToken = default,
        IProgress<OrderNumberExportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(rows);

        string? directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("导出路径无效", nameof(targetPath));

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.tmp.xlsx");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OrderNumberExportProgress(
                OrderNumberExportStage.Writing,
                0,
                rows.Count,
                "正在生成 Excel 文件"));
            IEnumerable<ExcelRow> data = EnumerateExcelRows(rows, cancellationToken, progress);
            MiniExcel.SaveAs(temporaryPath, data, sheetName: "单号", excelType: ExcelType.XLSX);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OrderNumberExportProgress(
                OrderNumberExportStage.Finalizing,
                rows.Count,
                rows.Count,
                "正在完成文件",
                IsIndeterminate: true));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // 临时文件清理失败不覆盖原始导出异常。
            }
            throw;
        }
    }

    private static IEnumerable<ExcelRow> EnumerateExcelRows(
        IReadOnlyList<OrderNumberExportRow> rows,
        CancellationToken cancellationToken,
        IProgress<OrderNumberExportProgress>? progress)
    {
        for (int index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OrderNumberExportRow row = rows[index];
            yield return new ExcelRow
            {
                TrackingNumber = row.TrackingNumber,
                SourceOrderIds = row.SourceOrderIds,
                Mode = row.Mode,
                FirstRecordingTime = row.FirstRecordingTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SourceDevices = row.SourceDevices
            };

            int processed = index + 1;
            if (processed == rows.Count || processed % 100 == 0)
            {
                progress?.Report(new OrderNumberExportProgress(
                    OrderNumberExportStage.Writing,
                    processed,
                    rows.Count,
                    "正在生成 Excel 文件"));
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new OrderNumberExportProgress(
                OrderNumberExportStage.Finalizing,
                rows.Count,
                rows.Count,
                "正在完成文件",
                IsIndeterminate: true));
        }
    }

    /// <summary>
    /// 来源设备列。记录里存的是写入当时的名字快照，设备改名后同一台设备会同时冒出老名字和
    /// 新名字，所以先按设备号取主机当前分配的名字，取不到再退回快照名。
    /// </summary>
    private static string GetSourceDevice(
        OrderNumberExportSource source,
        IReadOnlyDictionary<string, string>? currentSourceDeviceNames,
        string localDeviceName)
    {
        string name = RecordingSourceNameLookup.Resolve(
            currentSourceDeviceNames,
            source.SourceDeviceId,
            source.SourceDeviceName);
        if (name.Length > 0)
            return name;
        if (string.Equals(source.SourceType, "external", StringComparison.OrdinalIgnoreCase))
            return "外部设备";

        // 导出的表格会发给别的电脑，"本机"这种相对说法没有意义，写这台电脑的实际名字。
        string localName = localDeviceName?.Trim() ?? "";
        return localName.Length > 0 ? localName : Environment.MachineName;
    }

    private sealed class ExportGroup
    {
        private readonly SortedSet<string> _sourceOrderIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _sourceDevices = new(StringComparer.OrdinalIgnoreCase);

        internal ExportGroup(string trackingNumber, string mode, DateTime firstRecordingTime)
        {
            TrackingNumber = trackingNumber;
            Mode = mode;
            FirstRecordingTime = firstRecordingTime;
        }

        internal string TrackingNumber { get; }
        internal string Mode { get; }
        internal DateTime FirstRecordingTime { get; private set; }

        internal void Add(
            OrderNumberExportSource source,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames,
            string localDeviceName)
        {
            if (source.StartTime < FirstRecordingTime)
                FirstRecordingTime = source.StartTime;
            AddIfNotEmpty(_sourceOrderIds, source.SourceOrderId);
            AddIfNotEmpty(_sourceDevices, GetSourceDevice(source, currentSourceDeviceNames, localDeviceName));
        }

        internal OrderNumberExportRow ToRow() => new(
            TrackingNumber,
            string.Join("、", _sourceOrderIds),
            Mode,
            FirstRecordingTime,
            string.Join("、", _sourceDevices));

        private static void AddIfNotEmpty(ISet<string> target, string? value)
        {
            string normalized = value?.Trim() ?? "";
            if (normalized.Length > 0)
                target.Add(normalized);
        }
    }
}
