using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using Microsoft.Data.Sqlite;
using MiniExcelLibs;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class OrderNumberExportServiceTests
{
    [Fact]
    public void QueryAndBuildRows_ExcludesDeletedAndSeparatesBusinessModes()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using (var database = new VideoDatabase(databasePath))
            {
                using (var connection = new SqliteConnection($"Data Source={databasePath}"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                    INSERT INTO OrderInfoRecords
                        (TrackingNumber, SourceOrderId, PushTime, CreatedAt, UpdatedAt)
                    VALUES
                        ('001234567890123456', 'PLATFORM-001', '2026-08-01 09:00:00', '2026-08-01 09:00:00', '2026-08-01 09:00:00');

                    INSERT INTO VideoRecords
                        (OrderId, TrackingNumber, SourceOrderId, Mode, FilePath, StartTime, IsDeleted, SourceType, SourceDeviceName)
                    VALUES
                        ('001234567890123456', '', '', '发货', 'first.mp4', '2026-08-01 10:00:00', 0, 'pc', '工作台A'),
                        ('legacy', '001234567890123456', 'PLATFORM-002', '退货', 'second.mp4', '2026-08-01 11:00:00', 0, 'external', '手机B'),
                        ('DELETED-001', 'DELETED-001', '', '发货', 'deleted.mp4', '2026-08-01 12:00:00', 1, 'pc', ''),
                        ('OUTSIDE-001', 'OUTSIDE-001', '', '发货', 'outside.mp4', '2026-08-02 10:00:00', 0, 'pc', '');";
                    command.ExecuteNonQuery();
                }

                var queryProgress = new List<OrderNumberExportProgress>();
                List<OrderNumberExportSource> exportSources = database.QueryOrderNumberExportSources(
                    new DateTime(2026, 8, 1),
                    new DateTime(2026, 8, 1),
                    TestContext.Current.CancellationToken,
                    new InlineProgress<OrderNumberExportProgress>(queryProgress.Add));
                IReadOnlyList<OrderNumberExportRow> rows = OrderNumberExportService.BuildRows(
                    exportSources,
                    TestContext.Current.CancellationToken);

                Assert.Equal(2, queryProgress[^1].Processed);
                Assert.Equal(2, queryProgress[^1].Total);
                Assert.All(queryProgress, value => Assert.Equal(OrderNumberExportStage.Reading, value.Stage));
                Assert.Equal(2, rows.Count);
                OrderNumberExportRow shipping = Assert.Single(rows, row => row.Mode == "发货");
                Assert.Equal("001234567890123456", shipping.TrackingNumber);
                Assert.Equal("PLATFORM-001", shipping.SourceOrderIds);
                Assert.Equal(new DateTime(2026, 8, 1, 10, 0, 0), shipping.FirstRecordingTime);
                Assert.Equal("工作台A", shipping.SourceDevices);

                OrderNumberExportRow returnOrder = Assert.Single(rows, row => row.Mode == "退货");
                Assert.Equal("001234567890123456", returnOrder.TrackingNumber);
                Assert.Equal("PLATFORM-002", returnOrder.SourceOrderIds);
                Assert.Equal(new DateTime(2026, 8, 1, 11, 0, 0), returnOrder.FirstRecordingTime);
                Assert.Equal("手机B", returnOrder.SourceDevices);
            }
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_WritesLongTrackingNumberAsText()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "orders.xlsx");
            OrderNumberExportService.Export(path,
            [
                new OrderNumberExportRow(
                    "001234567890123456789",
                    "PLATFORM-001",
                    "发货",
                    new DateTime(2026, 8, 1, 10, 0, 0),
                    "工作台A")
            ], TestContext.Current.CancellationToken);

            Dictionary<string, object> row = Assert.Single(
                MiniExcel.Query(path, sheetName: "单号", useHeaderRow: true)
                    .Cast<IDictionary<string, object>>()
                    .Select(item => item.ToDictionary(pair => pair.Key, pair => pair.Value)));
            Assert.Equal("001234567890123456789", row["快递单号"]?.ToString());
            Assert.Equal("PLATFORM-001", row["平台订单号"]?.ToString());

            using ZipArchive archive = ZipFile.OpenRead(path);
            ZipArchiveEntry worksheet = Assert.Single(
                archive.Entries,
                entry => entry.FullName == "xl/worksheets/sheet1.xml");
            using Stream worksheetStream = worksheet.Open();
            XDocument document = XDocument.Load(worksheetStream);
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Dictionary<int, double> widths = document
                .Descendants(spreadsheet + "col")
                .ToDictionary(
                    element => int.Parse(element.Attribute("min")!.Value, CultureInfo.InvariantCulture),
                    element => double.Parse(element.Attribute("width")!.Value, CultureInfo.InvariantCulture));
            Assert.InRange(widths[1], 20, 21);
            Assert.InRange(widths[2], 20, 21);
            Assert.InRange(widths[4], 20, 21);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_CancellationPreservesExistingTargetAndRemovesTemporaryFile()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "orders.xlsx");
            File.WriteAllText(path, "existing-content");
            OrderNumberExportRow[] rows = Enumerable.Range(1, 300)
                .Select(index => new OrderNumberExportRow(
                    $"TRACK-{index:0000}",
                    "",
                    "发货",
                    new DateTime(2026, 8, 1, 10, 0, 0),
                    "工作台A"))
                .ToArray();
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<OrderNumberExportProgress>(value =>
            {
                if (value.Stage == OrderNumberExportStage.Writing && value.Processed >= 100)
                    cancellation.Cancel();
            });

            Assert.ThrowsAny<OperationCanceledException>(() =>
                OrderNumberExportService.Export(path, rows, cancellation.Token, progress));

            Assert.Equal("existing-content", File.ReadAllText(path));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(directory),
                file => Path.GetFileName(file).Contains(".tmp.xlsx", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildRows_ReportsProgressAndHonorsCancellation()
    {
        OrderNumberExportSource[] sources = Enumerable.Range(1, 250)
            .Select(index => new OrderNumberExportSource(
                $"TRACK-{index:0000}",
                "",
                "发货",
                new DateTime(2026, 8, 1).AddMinutes(index),
                "pc",
                "工作台A"))
            .ToArray();
        var values = new List<OrderNumberExportProgress>();
        var progress = new InlineProgress<OrderNumberExportProgress>(values.Add);

        IReadOnlyList<OrderNumberExportRow> rows = OrderNumberExportService.BuildRows(
            sources,
            TestContext.Current.CancellationToken,
            progress: progress);

        Assert.Equal(250, rows.Count);
        Assert.Equal(250, values[^1].Processed);
        Assert.All(values, value => Assert.Equal(OrderNumberExportStage.Organizing, value.Stage));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            OrderNumberExportService.BuildRows(sources, cancellation.Token));
    }

    [Fact]
    public void Export_NineThousandRowsReportsWritingAndFinalizingProgress()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "large-orders.xlsx");
            OrderNumberExportRow[] rows = Enumerable.Range(1, 9000)
                .Select(index => new OrderNumberExportRow(
                    $"TRACK-{index:000000}",
                    $"ORDER-{index:000000}",
                    index % 2 == 0 ? "发货" : "退货",
                    new DateTime(2026, 8, 1).AddSeconds(index),
                    "工作台A"))
                .ToArray();
            var values = new List<OrderNumberExportProgress>();

            OrderNumberExportService.Export(
                path,
                rows,
                TestContext.Current.CancellationToken,
                new InlineProgress<OrderNumberExportProgress>(values.Add));

            Assert.True(File.Exists(path));
            OrderNumberExportProgress[] writing = values
                .Where(value => value.Stage == OrderNumberExportStage.Writing)
                .ToArray();
            Assert.NotEmpty(writing);
            Assert.Equal(9000, writing.Max(value => value.Processed));
            Assert.True(writing.Zip(writing.Skip(1), (left, right) => left.Processed <= right.Processed).All(value => value));
            Assert.Contains(values, value =>
                value.Stage == OrderNumberExportStage.Finalizing && value.IsIndeterminate);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, null, "全部")]
    [InlineData("2026-08-01", null, "20260801起")]
    [InlineData(null, "2026-08-31", "截至20260831")]
    [InlineData("2026-08-01", "2026-08-31", "20260801-20260831")]
    public void BuildOrderExportRangeName_UsesSelectedDateBounds(
        string? startText,
        string? endText,
        string expected)
    {
        DateTime? start = startText == null ? null : DateTime.Parse(startText);
        DateTime? end = endText == null ? null : DateTime.Parse(endText);

        Assert.Equal(expected, PlaybackWindow.BuildOrderExportRangeName(start, end));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "OrderNumberExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
