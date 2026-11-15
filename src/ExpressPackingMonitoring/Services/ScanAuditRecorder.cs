using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.Globalization;

namespace ExpressPackingMonitoring.Services;

internal sealed class ScanAuditRecorder : IDisposable
{
    internal const int MaxInMemoryEvents = 20_000;
    internal const int FlushBatchSize = 20;
    internal const string AbandonedOutcome = "abandoned";
    internal static readonly TimeSpan MaxFlushDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NonTerminalEventRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan RecentKeyLifetime = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly object _storeGate = new();
    private readonly Dictionary<string, ScanEventRecord> _events = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _persisted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecentDecision> _recentDecisions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _worker;
    private readonly string _databasePath;
    private readonly string _computerName;
    private readonly string _appVersion;
    private readonly string _runtimeSessionId;
    private readonly Func<DateTimeOffset> _clock;
    private ScanEventStore? _store;
    private string _pendingStartEventId = "";
    private string _activeEventId = "";
    private long _activeRecordId;
    private bool _disposed;

    /// <summary>单元测试注入点：在批量快照之后、写库之前制造更新竞态。</summary>
    internal Action? BeforePersistForTests { get; set; }

    internal ScanAuditRecorder(
        string databasePath,
        Func<DateTimeOffset>? clock = null,
        bool startWorker = true)
    {
        _databasePath = databasePath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _computerName = Environment.MachineName;
        _appVersion = AppVersion.Current;
        _runtimeSessionId = RuntimeLog.CurrentSessionId;
        _worker = startWorker
            ? Task.Run(() => WorkerLoopAsync(_cts.Token))
            : Task.CompletedTask;
    }

    internal string Begin(
        ScanInputSource source,
        string? rawCode,
        string? normalizedOrder,
        bool wasRecording,
        long priorRecordId)
    {
        if (_disposed)
            return "";

        string eventId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _clock();
        string occurredAt = FormatTimestamp(now);
        var record = new ScanEventRecord
        {
            EventId = eventId,
            OccurredAtUtc = occurredAt,
            LastOccurredAtUtc = occurredAt,
            UpdatedAtUtc = occurredAt,
            Source = source.ToStorageValue(),
            RawCode = Trim(rawCode, 512),
            NormalizedOrder = Trim(normalizedOrder, 512),
            ComputerName = _computerName,
            AppVersion = _appVersion,
            RuntimeSessionId = _runtimeSessionId,
            PriorRecordId = priorRecordId > 0 ? priorRecordId : null,
            WasRecording = wasRecording
        };

        lock (_gate)
        {
            if (_events.Count >= MaxInMemoryEvents)
            {
                RuntimeLog.Warn(
                    "ScanAudit",
                    $"In-memory scan event limit reached; event not retained source={record.Source}, order={record.NormalizedOrder}");
                // 唤醒后台任务清理已过期未完成事件，否则会一直卡在上限。
                SignalWorker();
                return "";
            }

            _events[eventId] = record;
            _dirty.Add(eventId);
        }
        SignalWorker();
        return eventId;
    }

    internal void RecordDecision(
        string eventId,
        string action,
        string reason,
        string outcome,
        long priorRecordId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return;

        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;

            string priorRecordText = priorRecordId > 0
                ? priorRecordId.ToString(CultureInfo.InvariantCulture)
                : "";
            string duplicateKey = string.Join(
                '|',
                current.Source,
                current.NormalizedOrder,
                action,
                reason,
                outcome,
                priorRecordText);
            if (string.Equals(outcome, "ignored", StringComparison.Ordinal)
                && _recentDecisions.TryGetValue(duplicateKey, out RecentDecision? recent)
                && recent != null
                && now - recent.LastOccurredAt <= DuplicateWindow
                && _events.TryGetValue(recent.EventId, out ScanEventRecord? aggregate))
            {
                _events[recent.EventId] = aggregate with
                {
                    LastOccurredAtUtc = nowText,
                    UpdatedAtUtc = nowText,
                    RepeatCount = aggregate.RepeatCount + 1
                };
                _dirty.Add(recent.EventId);
                _events.Remove(eventId);
                _dirty.Remove(eventId);
                _recentDecisions[duplicateKey] = recent with { LastOccurredAt = now };
                SignalWorker();
                return;
            }

            ScanEventRecord updated = current with
            {
                Action = Trim(action, 64),
                Reason = Trim(reason, 128),
                Outcome = Trim(outcome, 64),
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText,
                PriorRecordId = priorRecordId > 0 ? priorRecordId : current.PriorRecordId
            };
            _events[eventId] = updated;
            _dirty.Add(eventId);
            _recentDecisions[duplicateKey] = new RecentDecision(eventId, now);
        }
        SignalWorker();
    }

    internal void PrepareStart(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return;
        lock (_gate)
        {
            if (_events.ContainsKey(eventId))
                _pendingStartEventId = eventId;
        }
    }

    internal void RecordRecordingStarted(
        long recordId,
        string sourceSessionId,
        string? filePath)
    {
        if (recordId <= 0)
            return;

        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            string eventId = _pendingStartEventId;
            _pendingStartEventId = "";
            if (string.IsNullOrWhiteSpace(eventId)
                || !_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;

            _events[eventId] = current with
            {
                Outcome = "started",
                StartedRecordId = recordId,
                SourceSessionId = Trim(sourceSessionId, 128),
                FileName = Trim(Path.GetFileName(filePath), 512),
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(eventId);
            _activeEventId = eventId;
            _activeRecordId = recordId;
        }
        SignalWorker();
    }

    internal void RecordPendingStartFailed(string reason, string? detail = null)
    {
        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            string eventId = _pendingStartEventId;
            _pendingStartEventId = "";
            if (string.IsNullOrWhiteSpace(eventId)
                || !_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;

            _events[eventId] = current with
            {
                Outcome = "start_failed",
                Detail = Trim(detail ?? reason, 1000),
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(eventId);
        }
        SignalWorker();
    }

    internal void RecordEventFailed(string eventId, string reason, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return;

        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;
            _events[eventId] = current with
            {
                Outcome = "start_failed",
                Reason = Trim(reason, 128),
                Detail = Trim(detail ?? "", 1000),
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(eventId);
            if (string.Equals(_pendingStartEventId, eventId, StringComparison.Ordinal))
                _pendingStartEventId = "";
        }
        SignalWorker();
    }

    internal void RecordStopFromInput(string eventId, long recordId, string reason)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return;

        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;
            _events[eventId] = current with
            {
                Outcome = "stopped",
                Reason = Trim(reason, 128),
                PriorRecordId = recordId > 0 ? recordId : current.PriorRecordId,
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(eventId);
        }
        SignalWorker();
    }

    internal void RecordStopRequested(string origin)
    {
        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_activeEventId)
                || !_events.TryGetValue(_activeEventId, out ScanEventRecord? current))
                return;
            if (!string.IsNullOrWhiteSpace(current.StopOrigin))
                return;
            _events[_activeEventId] = current with
            {
                Outcome = "stopping",
                StopOrigin = Trim(origin, 64),
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(_activeEventId);
        }
        SignalWorker();
    }

    internal void RecordRecordingFinished(
        long recordId,
        string stopOrigin,
        string outcome,
        double durationSeconds,
        bool manualStop,
        bool shortRecording,
        string? detail = null)
    {
        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            string eventId = _activeRecordId == recordId && !string.IsNullOrWhiteSpace(_activeEventId)
                ? _activeEventId
                : _events
                    .Where(pair => pair.Value.StartedRecordId == recordId)
                    .Select(pair => pair.Key)
                    .FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(eventId)
                || !_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;

            _events[eventId] = current with
            {
                Outcome = Trim(outcome, 64),
                // 停止请求已经写入稳定原因码时不能被本地化文案覆盖，
                // 否则同一字段会随界面语言变化，统计和导出无法按原因聚合。
                StopOrigin = string.IsNullOrWhiteSpace(current.StopOrigin)
                    ? Trim(stopOrigin, 64)
                    : current.StopOrigin,
                Detail = Trim(detail ?? "", 1000),
                DurationMs = Math.Max(
                    0,
                    (long)Math.Round(
                        durationSeconds * 1000,
                        MidpointRounding.AwayFromZero)),
                ManualStop = manualStop,
                ShortRecording = shortRecording,
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(eventId);
            if (_activeRecordId == recordId)
            {
                _activeEventId = "";
                _activeRecordId = 0;
            }
        }
        SignalWorker();
    }

    internal void RecordRecordingFailed(long recordId, string reason, string? detail = null)
    {
        DateTimeOffset now = _clock();
        string nowText = FormatTimestamp(now);
        lock (_gate)
        {
            string eventId = _activeRecordId == recordId && !string.IsNullOrWhiteSpace(_activeEventId)
                ? _activeEventId
                : _events
                    .Where(pair => pair.Value.StartedRecordId == recordId)
                    .Select(pair => pair.Key)
                    .FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(eventId)
                || !_events.TryGetValue(eventId, out ScanEventRecord? current))
                return;
            _events[eventId] = current with
            {
                Outcome = "failed",
                Reason = Trim(reason, 128),
                Detail = Trim(detail ?? "", 1000),
                LastOccurredAtUtc = nowText,
                UpdatedAtUtc = nowText
            };
            _dirty.Add(eventId);
            if (_activeRecordId == recordId)
            {
                _activeEventId = "";
                _activeRecordId = 0;
            }
        }
        SignalWorker();
    }

    internal Task FlushForTestsAsync(CancellationToken cancellationToken = default) =>
        FlushDirtyAsync(cancellationToken);

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                await WaitForFlushWindowAsync(cancellationToken).ConfigureAwait(false);
                await FlushDirtyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("ScanAudit", "Background scan audit worker stopped", ex);
        }
    }

    private async Task WaitForFlushWindowAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxFlushDelay);
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_dirty.Count == 0 || _dirty.Count >= FlushBatchSize)
                        return;
                }

                await _wake.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // 本批最多等待 MaxFlushDelay；超时令牌会同时取消等待，不能留下孤儿 waiter。
        }
    }

    private async Task FlushDirtyAsync(CancellationToken cancellationToken)
    {
        List<ScanEventRecord> batch;
        int abandonedCount;
        lock (_gate)
        {
            // 没有新事件时也要清理过期的非终态事件，否则内存上限会被它们占满。
            abandonedCount = PrunePersistedEvents();
            if (_dirty.Count == 0)
                return;

            batch = _dirty
                .Select(id => _events.TryGetValue(id, out ScanEventRecord? record) ? record : null)
                .Where(record => record != null)
                .Cast<ScanEventRecord>()
                .ToArray()
                .ToList();
            _dirty.Clear();
            PruneRecentDecisions(_clock());
        }

        if (abandonedCount > 0)
        {
            RuntimeLog.Info(
                "ScanAudit",
                $"Marked {abandonedCount} stale scan audit events as abandoned");
        }

        if (batch.Count == 0)
            return;

        try
        {
            BeforePersistForTests?.Invoke();
            ScanEventStore store = EnsureStore();
            await Task.Run(() => store.UpsertBatch(batch), cancellationToken).ConfigureAwait(false);
            int abandonedAfterWrite;
            lock (_gate)
            {
                foreach (ScanEventRecord record in batch)
                    _persisted.Add(record.EventId);
                abandonedAfterWrite = PrunePersistedEvents();
            }
            if (abandonedAfterWrite > 0)
                SignalWorker();
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                foreach (ScanEventRecord record in batch)
                    _dirty.Add(record.EventId);
            }
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                foreach (ScanEventRecord record in batch)
                    _dirty.Add(record.EventId);
            }
            RuntimeLog.Warn("ScanAudit", $"Scan audit flush failed: {ex.Message}");
        }
    }

    private ScanEventStore EnsureStore()
    {
        lock (_storeGate)
        {
            return _store ??= new ScanEventStore(_databasePath);
        }
    }

    private void PruneRecentDecisions(DateTimeOffset now)
    {
        foreach (string key in _recentDecisions
                     .Where(pair => now - pair.Value.LastOccurredAt > RecentKeyLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _recentDecisions.Remove(key);
        }
    }

    private int PrunePersistedEvents()
    {
        DateTimeOffset now = _clock();
        PruneRecentDecisions(now);
        var recentEventIds = _recentDecisions.Values
            .Select(decision => decision.EventId)
            .ToHashSet(StringComparer.Ordinal);
        int abandoned = 0;
        foreach ((string eventId, ScanEventRecord record) in _events.ToArray())
        {
            if (eventId == _activeEventId
                || eventId == _pendingStartEventId
                || recentEventIds.Contains(eventId)
                || !_persisted.Contains(eventId)
                // 写库期间又更新过的事件不能在这里删，否则会丢掉最新的终态。
                || _dirty.Contains(eventId))
            {
                continue;
            }

            if (!IsTerminalOutcome(record.Outcome))
            {
                if (!IsStale(record, now))
                    continue;

                _events[eventId] = record with
                {
                    Outcome = AbandonedOutcome,
                    Detail = string.IsNullOrWhiteSpace(record.Detail)
                        ? "事件超过保留时间仍未结束"
                        : Trim($"{record.Detail}；事件超过保留时间仍未结束", 1000),
                    UpdatedAtUtc = FormatTimestamp(now)
                };
                _dirty.Add(eventId);
                abandoned++;
                continue;
            }

            _events.Remove(eventId);
            _dirty.Remove(eventId);
            _persisted.Remove(eventId);
        }

        return abandoned;
    }

    private static bool IsStale(ScanEventRecord record, DateTimeOffset now)
    {
        bool parsed = TryParseTimestamp(record.LastOccurredAtUtc, out DateTimeOffset lastActivity)
            || TryParseTimestamp(record.OccurredAtUtc, out lastActivity);
        return parsed && now - lastActivity >= NonTerminalEventRetention;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static bool IsTerminalOutcome(string outcome) => outcome is
        "ignored" or
        "command" or
        "start_failed" or
        "stopped" or
        "completed" or
        "discarded" or
        "failed" or
        AbandonedOutcome;

    private void SignalWorker()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // A pending wake-up already exists.
            }
        }
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string Trim(string? value, int maxLength)
    {
        string text = value?.Trim() ?? "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            Task.Run(() => FlushDirtyAsync(CancellationToken.None))
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("ScanAudit", $"Final scan audit flush failed: {ex.Message}");
        }

        _cts.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
        _wake.Dispose();
        _cts.Dispose();

        lock (_storeGate)
        {
            _store?.Dispose();
            _store = null;
        }
    }

    private sealed record RecentDecision(string EventId, DateTimeOffset LastOccurredAt);
}

internal static class ScanStopOrigin
{
    internal const string ManualButton = "manual_button";
    internal const string SameCode = "same_code";
    internal const string StopCommand = "stop_command";
    internal const string Switch = "scan_switch";
    internal const string ApplicationExit = "app_exit";
    internal const string StorageLow = "storage_low";
    internal const string CameraStall = "camera_stall";
    internal const string CameraReconnect = "camera_reconnect";
    internal const string StaticTimeout = "static_timeout";
    internal const string DurationTimeout = "duration_timeout";
}
