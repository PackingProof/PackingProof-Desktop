using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using ExpressPackingMonitoring.Logging;
using System.Threading;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services
{
    /// <summary>
    /// Web 端导出单号的处理逻辑。
    /// 独立成类而不是塞进 WebServer：后者已是规模冻结的历史例外，只允许缩小。
    /// </summary>
    internal static class OrderNumberExportEndpoint
    {
        /// <summary>Excel 单文件的行数上限，防止一次导出把内存和响应撑爆。</summary>
        internal const int MaxRows = 200_000;

        internal sealed record Request(
            DateTime? StartDate,
            DateTime? EndDate,
            string Mode,
            string DeviceId = "",
            string SourceName = "",
            string SourceType = "",
            IReadOnlyList<string>? DeviceIds = null);

        internal sealed record Result(byte[] Content, string FileName, int RowCount);

        /// <summary>
        /// 解析查询参数。日期填反时交换，与上位机的处理保持一致，
        /// 避免返回空表让人以为没有数据。
        /// </summary>
        internal static Request ParseRequest(
            string? start,
            string? end,
            string? mode,
            string? deviceId = null,
            string? sourceName = null,
            string? sourceType = null,
            string? deviceIds = null)
        {
            DateTime? startDate = DateTime.TryParse(start, out DateTime parsedStart) ? parsedStart.Date : null;
            DateTime? endDate = DateTime.TryParse(end, out DateTime parsedEnd) ? parsedEnd.Date : null;
            if (startDate.HasValue && endDate.HasValue && startDate > endDate)
                (startDate, endDate) = (endDate, startDate);

            // 同名多设备合并成一项时界面传设备号集合，导出必须按同一批设备过滤，
            // 否则导出的单号与列表对不上。
            string[] parsedDeviceIds = (deviceIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(50)
                .ToArray();

            return new Request(
                startDate,
                endDate,
                RecordingModeFilter.Normalize(mode),
                deviceId?.Trim() ?? "",
                sourceName?.Trim() ?? "",
                sourceType?.Trim() ?? "",
                parsedDeviceIds);
        }

        /// <summary>
        /// 导出文件名。带上日期范围和类型，店员下载多份时不会互相覆盖。
        /// </summary>
        internal static string BuildFileName(Request request, DateTime now)
        {
            string range = request.StartDate.HasValue && request.EndDate.HasValue
                ? $"{request.StartDate.Value:yyyyMMdd}-{request.EndDate.Value:yyyyMMdd}"
                : request.StartDate.HasValue ? $"{request.StartDate.Value:yyyyMMdd}起"
                : request.EndDate.HasValue ? $"截至{request.EndDate.Value:yyyyMMdd}"
                : "全部";

            string modeText = RecordingModeFilter.ToDisplayText(request.Mode);
            string modePart = modeText.Length > 0 ? $"_{modeText}" : "";

            // 带上设备，导出多台设备时文件名能区分开。
            string deviceText = request.SourceName.Length > 0 ? request.SourceName
                : request.DeviceId.Length > 0 ? request.DeviceId
                : "";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                deviceText = deviceText.Replace(invalid, '_');
            string devicePart = deviceText.Length > 0 ? $"_{deviceText}" : "";

            return $"单号_{range}{modePart}{devicePart}_{now:yyyyMMdd_HHmmss}.xlsx";
        }

        /// <summary>
        /// 读取、整理并生成 Excel 字节流。
        /// 浏览器要直接下载，所以先写到临时文件再读回内存，
        /// 复用 OrderNumberExportService 的写盘逻辑而不是另写一份。
        /// </summary>
        internal static Result Export(
            VideoDatabase database,
            Request request,
            DateTime now,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(request);

            List<OrderNumberExportSource> sources = database.QueryOrderNumberExportSources(
                request.StartDate,
                request.EndDate,
                cancellationToken,
                progress: null,
                mode: request.Mode,
                deviceId: request.DeviceId,
                sourceName: request.SourceName,
                sourceType: request.SourceType,
                deviceIds: request.DeviceIds ?? Array.Empty<string>());

            IReadOnlyList<OrderNumberExportRow> rows = OrderNumberExportService.BuildRows(
                sources,
                cancellationToken,
                currentSourceDeviceNames: currentSourceDeviceNames);

            if (rows.Count > MaxRows)
            {
                throw new InvalidOperationException(
                    $"导出行数 {rows.Count} 超过上限 {MaxRows}，请缩小日期范围后重试");
            }

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"packingproof-order-export-{Guid.NewGuid():N}.xlsx");
            try
            {
                OrderNumberExportService.Export(tempPath, rows, cancellationToken);
                byte[] content = File.ReadAllBytes(tempPath);
                return new Result(content, BuildFileName(request, now), rows.Count);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件清理失败不影响已经生成好的导出内容。
                }
            }
        }

        /// <summary>
        /// 完整处理一次导出请求，包括写响应。
        /// HTTP 细节留在这里而不是 WebServer：后者是规模冻结的历史例外，
        /// 只允许缩小，不能再往里堆路由与协议代码。
        /// </summary>
        internal static void Handle(
            HttpListenerContext ctx,
            VideoDatabase database,
            Action<HttpListenerContext, int, object> sendJson,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null)
        {
            var qs = ctx.Request.QueryString;
            try
            {
                Request request = ParseRequest(
                    qs["start"], qs["end"], qs["mode"],
                    qs["deviceId"], qs["sourceName"], qs["sourceType"], qs["deviceIds"]);
                Result result = Export(
                    database,
                    request,
                    DateTime.Now,
                    currentSourceDeviceNames: currentSourceDeviceNames);

                if (result.RowCount == 0)
                {
                    sendJson(ctx, 404, new { error = "当前筛选条件下没有可导出的单号" });
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType =
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                ctx.Response.AddHeader("Content-Disposition", BuildContentDisposition(result.FileName));
                ctx.Response.ContentLength64 = result.Content.Length;
                ctx.Response.OutputStream.Write(result.Content, 0, result.Content.Length);
                ctx.Response.OutputStream.Close();
                RuntimeLog.Info("OrderExport", $"Web 导出单号 {result.RowCount} 条");
            }
            catch (InvalidOperationException ex)
            {
                sendJson(ctx, 400, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("OrderExport", "Web 导出单号失败", ex);
                sendJson(ctx, 500, new { error = "导出失败，请稍后重试" });
            }
        }

        /// <summary>
        /// 非 ASCII 文件名要用 RFC 5987 的 filename* 才能在浏览器里正确显示中文，
        /// 同时保留一个 ASCII 回退名给老浏览器。
        /// </summary>
        internal static string BuildContentDisposition(string fileName)
        {
            string encoded = Uri.EscapeDataString(fileName);
            return $"attachment; filename=\"order-numbers.xlsx\"; filename*=UTF-8''{encoded}";
        }
    }
}
