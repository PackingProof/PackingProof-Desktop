using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using ExpressPackingMonitoring.Logging;
using System.Threading;
using System.Threading.Tasks;
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
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null,
            string localDeviceName = "")
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
                currentSourceDeviceNames: currentSourceDeviceNames,
                localDeviceName: localDeviceName);

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
        ///
        /// 支持两种用法：
        /// - GET /api/videos/export-order-numbers?...：老的单次下载（浏览器直接拿文件）
        /// - POST /api/videos/export-order-numbers/tasks?...：起一个导出任务，浏览器轮询
        ///   /tasks/{id} 拿进度、可 /cancel 取消、完成后从 /tasks/{id}/file 下载
        /// </summary>
        internal static void Handle(
            HttpListenerContext ctx,
            VideoDatabase database,
            Action<HttpListenerContext, int, object> sendJson,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null,
            string localDeviceName = "")
        {
            string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            string method = ctx.Request.HttpMethod ?? "GET";
            const string tasksPrefix = "/api/videos/export-order-numbers/tasks";
            try
            {
                if (path.StartsWith(tasksPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    HandleTaskRequest(
                        ctx,
                        method,
                        path[tasksPrefix.Length..],
                        database,
                        sendJson,
                        currentSourceDeviceNames,
                        localDeviceName);
                    return;
                }

                HandleDirectDownload(ctx, database, sendJson, currentSourceDeviceNames, localDeviceName);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("OrderExport", "Web 导出单号失败", ex);
                TrySendJson(sendJson, ctx, 500, new { error = "导出失败，请稍后重试" });
            }
        }

        private static void HandleDirectDownload(
            HttpListenerContext ctx,
            VideoDatabase database,
            Action<HttpListenerContext, int, object> sendJson,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames,
            string localDeviceName)
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
                    currentSourceDeviceNames: currentSourceDeviceNames,
                    localDeviceName: localDeviceName);

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

        private static void HandleTaskRequest(
            HttpListenerContext ctx,
            string method,
            string taskPath,
            VideoDatabase database,
            Action<HttpListenerContext, int, object> sendJson,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames,
            string localDeviceName)
        {
            string trimmed = taskPath.Trim('/');
            if (string.IsNullOrEmpty(trimmed))
            {
                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    sendJson(ctx, 405, new { error = "导出任务只支持 POST" });
                    return;
                }

                var qs = ctx.Request.QueryString;
                Request request = ParseRequest(
                    qs["start"], qs["end"], qs["mode"],
                    qs["deviceId"], qs["sourceName"], qs["sourceType"], qs["deviceIds"]);
                string startedTaskId = StartTask(
                    database,
                    request,
                    currentSourceDeviceNames,
                    localDeviceName);
                sendJson(ctx, 200, new { success = true, taskId = startedTaskId });
                return;
            }

            bool wantsCancel = trimmed.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase);
            bool wantsFile = trimmed.EndsWith("/file", StringComparison.OrdinalIgnoreCase);
            string taskId = trimmed
                .Replace("/cancel", "", StringComparison.OrdinalIgnoreCase)
                .Replace("/file", "", StringComparison.OrdinalIgnoreCase)
                .Trim('/');

            if (wantsCancel)
            {
                bool cancelled = CancelTask(taskId);
                sendJson(ctx, cancelled ? 200 : 409, new { success = cancelled });
                return;
            }

            if (wantsFile)
            {
                if (!TryGetDownload(taskId, out byte[] content, out string fileName))
                {
                    sendJson(ctx, 409, new { error = "导出还没完成" });
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType =
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                ctx.Response.AddHeader("Content-Disposition", BuildContentDisposition(fileName));
                ctx.Response.ContentLength64 = content.Length;
                ctx.Response.OutputStream.Write(content, 0, content.Length);
                ctx.Response.OutputStream.Close();
                RuntimeLog.Info("OrderExport", $"Web 导出单号 {content.Length} 字节，任务 {taskId} 已下载");
                return;
            }

            TaskSnapshot? snapshot = GetSnapshot(taskId);
            if (snapshot == null)
            {
                sendJson(ctx, 404, new { error = "导出任务不存在或已过期" });
                return;
            }

            sendJson(ctx, 200, new { success = true, task = snapshot });
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

        /// <summary>
        /// 浏览器轮询用的任务快照。阶段与进度来自主程序端同一套 OrderNumberExportProgress，
        /// 这样 Web 端和上位机的进度语义一致。
        /// </summary>
        internal sealed record TaskSnapshot(
            string TaskId,
            string State,
            string Stage,
            int Processed,
            int Total,
            string Message,
            bool CanDownload,
            int RowCount,
            string FileName,
            string Error);

        /// <summary>导出任务保留时长：下载或取消后还要留一会儿，避免浏览器晚一步轮询就拿不到。</summary>
        private static readonly TimeSpan TaskRetention = TimeSpan.FromMinutes(10);

        private static readonly ConcurrentDictionary<string, ExportTaskState> Tasks = new();

        private sealed class ExportTaskState
        {
            private readonly object _sync = new();
            private readonly CancellationTokenSource _cancellation = new();

            internal ExportTaskState(string taskId) => TaskId = taskId;

            internal string TaskId { get; }

            private string _state = "running";
            private string _stage = "";
            private int _processed;
            private int _total;
            private string _message = "正在准备导出";
            private int _rowCount;
            private string _fileName = "";
            private string _error = "";
            private byte[]? _content;
            private DateTime _touch = DateTime.UtcNow;
            private bool _cancelled;

            internal bool IsFinished => _state != "running";

            internal DateTime LastTouched
            {
                get { lock (_sync) return _touch; }
            }

            internal void Report(OrderNumberExportProgress progress)
            {
                lock (_sync)
                {
                    _stage = progress.Stage.ToString();
                    _message = progress.Message;
                    if (progress.Total > 0)
                    {
                        _total = progress.Total;
                        _processed = Math.Clamp(progress.Processed, 0, progress.Total);
                    }
                }
            }

            internal TaskSnapshot ToSnapshot()
            {
                lock (_sync)
                {
                    return new TaskSnapshot(
                        TaskId,
                        _state,
                        _stage,
                        _processed,
                        _total,
                        _message,
                        _content != null,
                        _rowCount,
                        _fileName,
                        _error);
                }
            }

            internal bool TryGetDownload(out byte[] content, out string fileName)
            {
                lock (_sync)
                {
                    content = _content ?? Array.Empty<byte>();
                    fileName = _fileName;
                    bool ready = _content != null && _state == "succeeded";
                    if (ready)
                        _touch = DateTime.UtcNow;
                    return ready;
                }
            }

            /// <summary>取消：正在跑就标记取消并通知，已经结束的任务不允许取消。</summary>
            internal bool TryCancel()
            {
                lock (_sync)
                {
                    if (_state != "running")
                        return false;
                    _cancelled = true;
                    _state = "cancelled";
                    _message = "已取消导出";
                    _touch = DateTime.UtcNow;
                }

                try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            }

            internal void MarkCancelledIfRequested()
            {
                lock (_sync)
                {
                    if (_cancelled || _state == "running")
                    {
                        _state = "cancelled";
                        _message = "已取消导出";
                        _touch = DateTime.UtcNow;
                    }
                }
            }

            internal void Run(
                VideoDatabase database,
                Request request,
                IReadOnlyDictionary<string, string>? currentSourceDeviceNames,
                string localDeviceName)
            {
                try
                {
                    var progress = new TaskProgress(this);
                    Result result = Export(
                        database,
                        request,
                        DateTime.Now,
                        _cancellation.Token,
                        currentSourceDeviceNames,
                        localDeviceName);
                    lock (_sync)
                    {
                        if (_cancelled)
                        {
                            _state = "cancelled";
                            _message = "已取消导出";
                        }
                        else
                        {
                            _state = "succeeded";
                            _stage = OrderNumberExportStage.Finalizing.ToString();
                            _processed = result.RowCount;
                            _total = result.RowCount;
                            _message = $"导出完成，共 {result.RowCount} 条";
                            _rowCount = result.RowCount;
                            _fileName = result.FileName;
                            _content = result.Content;
                        }
                        _touch = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException)
                {
                    MarkCancelledIfRequested();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("OrderExport", $"Web 导出单号任务失败 taskId={TaskId}", ex);
                    lock (_sync)
                    {
                        _state = "failed";
                        _error = ex is InvalidOperationException ? ex.Message : "导出失败，请稍后重试";
                        _message = _error;
                        _touch = DateTime.UtcNow;
                    }
                }
                finally
                {
                    try { _cancellation.Dispose(); } catch { }
                }
            }
        }

        private sealed class TaskProgress(ExportTaskState owner) : IProgress<OrderNumberExportProgress>
        {
            public void Report(OrderNumberExportProgress value) => owner.Report(value);
        }

        /// <summary>起一个后台导出任务，返回任务号；进度、取消、下载都按这个号查。</summary>
        internal static string StartTask(
            VideoDatabase database,
            Request request,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null,
            string localDeviceName = "")
        {
            PruneExpiredTasks();
            var state = new ExportTaskState(Guid.NewGuid().ToString("N"));
            Tasks[state.TaskId] = state;
            _ = Task.Run(() => state.Run(database, request, currentSourceDeviceNames, localDeviceName));
            return state.TaskId;
        }

        internal static TaskSnapshot? GetSnapshot(string taskId)
        {
            PruneExpiredTasks();
            return Tasks.TryGetValue(taskId ?? "", out ExportTaskState? state) ? state.ToSnapshot() : null;
        }

        internal static bool CancelTask(string taskId)
        {
            if (!Tasks.TryGetValue(taskId ?? "", out ExportTaskState? state))
                return false;
            return state.TryCancel();
        }

        internal static bool TryGetDownload(string taskId, out byte[] content, out string fileName)
        {
            content = Array.Empty<byte>();
            fileName = "";
            if (!Tasks.TryGetValue(taskId ?? "", out ExportTaskState? state))
                return false;
            return state.TryGetDownload(out content, out fileName);
        }

        private static void PruneExpiredTasks()
        {
            DateTime cutoff = DateTime.UtcNow - TaskRetention;
            foreach (KeyValuePair<string, ExportTaskState> entry in Tasks)
            {
                if (entry.Value.LastTouched < cutoff)
                    Tasks.TryRemove(entry.Key, out _);
            }
        }

        /// <summary>响应已经写出去以后再报错时不能再抛一次，否则会把连接卡住。</summary>
        private static void TrySendJson(
            Action<HttpListenerContext, int, object> sendJson,
            HttpListenerContext ctx,
            int statusCode,
            object payload)
        {
            try { sendJson(ctx, statusCode, payload); }
            catch { }
        }
    }
}
