using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal static class ExtensionScanCapabilities
{
    internal const string OrderLookup = "order.lookup";
    internal const string RefundLookup = "refund.lookup";
    internal const string MeasurementCapture = "measurement.capture";

    internal static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderLookup,
        RefundLookup,
        MeasurementCapture
    };
}

internal enum ExtensionScanResultStatus
{
    InProgress,
    Found,
    NotFound,
    Completed,
    Unavailable,
    Unauthorized,
    RateLimited,
    Timeout,
    InvalidRequest
}

internal enum ExtensionScanDeliveryState
{
    Pending,
    Delivered,
    Completed,
    Expired
}

internal enum ExtensionScanSubmissionDisposition
{
    Accepted,
    Duplicate,
    StaleRevision,
    RevisionConflict,
    DeliveryNotFound,
    ExtensionMismatch,
    TaskMismatch,
    Expired
}

internal sealed record ExtensionScanEvent
{
    internal string TaskId { get; init; } = "";
    internal string OriginNodeId { get; init; } = "";
    internal string RecordingSessionId { get; init; } = "";
    internal string TrackingNumber { get; init; } = "";
    internal string RecordingMode { get; init; } = "";
    internal DateTimeOffset OccurredAtUtc { get; init; }
    internal DateTimeOffset SoftDeadlineUtc { get; init; }
    internal DateTimeOffset ExpiresAtUtc { get; init; }
    internal IReadOnlyList<string> RequestedCapabilities { get; init; } = [];
}

internal sealed record ExtensionScanTarget
{
    internal string ExtensionInstanceId { get; init; } = "";
    internal IReadOnlyList<string> Capabilities { get; init; } = [];
}

internal sealed record ExtensionScanDelivery
{
    internal string DeliveryId { get; init; } = "";
    internal string ExtensionInstanceId { get; init; } = "";
    internal ExtensionScanEvent ScanEvent { get; init; } = new();
    internal IReadOnlyList<string> RequestedCapabilities { get; init; } = [];
    internal ExtensionScanDeliveryState State { get; init; }
    internal int DeliveryAttempts { get; init; }
    internal DateTimeOffset? FirstDeliveredAtUtc { get; init; }
    internal DateTimeOffset? LastDeliveredAtUtc { get; init; }
    internal DateTimeOffset? NextDeliveryAtUtc { get; init; }
    internal long LatestRevision { get; init; }
    internal ExtensionScanResultStatus? LatestStatus { get; init; }
}

internal sealed record ExtensionScanSubmission
{
    internal string ExtensionInstanceId { get; init; } = "";
    internal string DeliveryId { get; init; } = "";
    internal string TaskId { get; init; } = "";
    internal long Revision { get; init; }
    internal ExtensionScanResultStatus Status { get; init; }
    internal string PayloadFingerprint { get; init; } = "";
    internal DateTimeOffset? RetryAfterUtc { get; init; }
}

internal sealed record ExtensionScanSubmissionResult(
    ExtensionScanSubmissionDisposition Disposition,
    ExtensionScanDelivery? Delivery = null);

internal sealed record ExtensionScanPublishResult(
    IReadOnlyList<ExtensionScanDelivery> Deliveries,
    IReadOnlyList<string> SkippedExtensionInstanceIds);

internal sealed class ExtensionScanTaskCapacityException(string message) : InvalidOperationException(message);

/// <summary>
/// Internal task state machine only. It deliberately has no HTTP, enrollment, database, or recording dependencies.
/// Public routes must not be added until extension-scoped authorization is available.
/// </summary>
internal sealed class ExtensionScanTaskBroker
{
    internal const int DefaultMaxActiveTasks = 256;
    internal const int DefaultMaxPendingDeliveriesPerExtension = 32;
    internal static readonly TimeSpan DefaultRedeliveryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromMinutes(2);

    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maxActiveTasks;
    private readonly int _maxPendingDeliveriesPerExtension;
    private readonly TimeSpan _redeliveryDelay;
    private readonly TimeSpan _completedRetention;
    private readonly Dictionary<string, EventState> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeliveryState> _deliveries = new(StringComparer.Ordinal);

    internal ExtensionScanTaskBroker(
        TimeProvider? timeProvider = null,
        int maxActiveTasks = DefaultMaxActiveTasks,
        int maxPendingDeliveriesPerExtension = DefaultMaxPendingDeliveriesPerExtension,
        TimeSpan? redeliveryDelay = null,
        TimeSpan? completedRetention = null)
    {
        if (maxActiveTasks <= 0) throw new ArgumentOutOfRangeException(nameof(maxActiveTasks));
        if (maxPendingDeliveriesPerExtension <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingDeliveriesPerExtension));

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxActiveTasks = maxActiveTasks;
        _maxPendingDeliveriesPerExtension = maxPendingDeliveriesPerExtension;
        _redeliveryDelay = redeliveryDelay ?? DefaultRedeliveryDelay;
        _completedRetention = completedRetention ?? DefaultCompletedRetention;
        if (_redeliveryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(redeliveryDelay));
        if (_completedRetention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(completedRetention));
    }

    internal ExtensionScanPublishResult Publish(
        ExtensionScanEvent scanEvent,
        IEnumerable<ExtensionScanTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(scanEvent);
        ArgumentNullException.ThrowIfNull(targets);
        ExtensionScanEvent normalizedEvent = NormalizeEvent(scanEvent);
        ExtensionScanTarget[] normalizedTargets = targets
            .Select(NormalizeTarget)
            .GroupBy(target => target.ExtensionInstanceId, StringComparer.Ordinal)
            .Select(group => MergeTargets(group.Key, group))
            .ToArray();

        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PruneCore(now);
            if (_events.ContainsKey(normalizedEvent.TaskId))
                throw new InvalidOperationException("扫码任务 ID 已存在");
            if (CountActiveEvents(now) >= _maxActiveTasks)
                throw new ExtensionScanTaskCapacityException("活动扫码任务数量已达到上限");

            var created = new List<ExtensionScanDelivery>();
            var skipped = new List<string>();
            foreach (ExtensionScanTarget target in normalizedTargets)
            {
                string[] matchedCapabilities = normalizedEvent.RequestedCapabilities
                    .Intersect(target.Capabilities, StringComparer.Ordinal)
                    .ToArray();
                if (matchedCapabilities.Length == 0)
                    continue;
                if (CountPendingDeliveries(target.ExtensionInstanceId, now)
                    >= _maxPendingDeliveriesPerExtension)
                {
                    skipped.Add(target.ExtensionInstanceId);
                    continue;
                }

                var state = new DeliveryState
                {
                    DeliveryId = Guid.NewGuid().ToString("N"),
                    TaskId = normalizedEvent.TaskId,
                    ExtensionInstanceId = target.ExtensionInstanceId,
                    RequestedCapabilities = matchedCapabilities
                };
                _deliveries.Add(state.DeliveryId, state);
                created.Add(ToSnapshot(state, normalizedEvent, now));
            }

            if (created.Count > 0)
            {
                _events.Add(normalizedEvent.TaskId, new EventState
                {
                    ScanEvent = normalizedEvent,
                    DeliveryIds = created.Select(delivery => delivery.DeliveryId).ToArray()
                });
            }

            return new ExtensionScanPublishResult(created, skipped);
        }
    }

    internal ExtensionScanDelivery? Poll(string extensionInstanceId)
    {
        string normalizedExtensionId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID");
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PruneCore(now);
            DeliveryState? state = _deliveries.Values
                .Where(delivery => string.Equals(
                    delivery.ExtensionInstanceId,
                    normalizedExtensionId,
                    StringComparison.Ordinal))
                .Where(delivery => !delivery.Completed && !delivery.Expired)
                .Where(delivery => delivery.NextDeliveryAtUtc == null
                    || now >= delivery.NextDeliveryAtUtc.Value)
                .OrderBy(delivery => _events[delivery.TaskId].ScanEvent.OccurredAtUtc)
                .ThenBy(delivery => delivery.DeliveryId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (state == null) return null;

            state.DeliveryAttempts++;
            state.FirstDeliveredAtUtc ??= now;
            state.LastDeliveredAtUtc = now;
            state.NextDeliveryAtUtc = now + _redeliveryDelay;
            return ToSnapshot(state, _events[state.TaskId].ScanEvent, now);
        }
    }

    internal ExtensionScanSubmissionResult Submit(ExtensionScanSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        string extensionId = NormalizeIdentifier(submission.ExtensionInstanceId, "扩展实例 ID");
        string deliveryId = NormalizeIdentifier(submission.DeliveryId, "投递 ID");
        string taskId = NormalizeIdentifier(submission.TaskId, "任务 ID");
        if (submission.Revision <= 0) throw new InvalidDataException("响应修订号必须大于 0");
        string fingerprint = submission.PayloadFingerprint?.Trim() ?? "";
        if (fingerprint.Length is < 16 or > 128 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("响应内容指纹格式无效");

        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!_deliveries.TryGetValue(deliveryId, out DeliveryState? state))
                return new(ExtensionScanSubmissionDisposition.DeliveryNotFound);
            if (!string.Equals(state.ExtensionInstanceId, extensionId, StringComparison.Ordinal))
                return new(ExtensionScanSubmissionDisposition.ExtensionMismatch);
            if (!string.Equals(state.TaskId, taskId, StringComparison.Ordinal))
                return new(ExtensionScanSubmissionDisposition.TaskMismatch);

            ExtensionScanEvent scanEvent = _events[state.TaskId].ScanEvent;
            if (submission.Revision == state.LatestRevision)
            {
                ExtensionScanSubmissionDisposition duplicateDisposition = string.Equals(
                    state.LatestPayloadFingerprint,
                    fingerprint,
                    StringComparison.OrdinalIgnoreCase)
                        ? ExtensionScanSubmissionDisposition.Duplicate
                        : ExtensionScanSubmissionDisposition.RevisionConflict;
                return new(duplicateDisposition, ToSnapshot(state, scanEvent, now));
            }
            if (submission.Revision < state.LatestRevision)
                return new(ExtensionScanSubmissionDisposition.StaleRevision, ToSnapshot(state, scanEvent, now));
            if (state.Completed)
                return new(ExtensionScanSubmissionDisposition.StaleRevision, ToSnapshot(state, scanEvent, now));
            if (state.Expired || now > scanEvent.ExpiresAtUtc)
            {
                state.Expired = true;
                return new(ExtensionScanSubmissionDisposition.Expired, ToSnapshot(state, scanEvent, now));
            }

            if (submission.RetryAfterUtc is { } retryAfterUtc)
            {
                if (retryAfterUtc <= now || retryAfterUtc > scanEvent.ExpiresAtUtc)
                    throw new InvalidDataException("重试时间必须晚于当前时间且不能超过任务有效期");
                if (submission.Status is not (
                    ExtensionScanResultStatus.InProgress
                    or ExtensionScanResultStatus.Unavailable
                    or ExtensionScanResultStatus.RateLimited))
                {
                    throw new InvalidDataException("最终响应不能设置重试时间");
                }
            }

            state.LatestRevision = submission.Revision;
            state.LatestStatus = submission.Status;
            state.LatestPayloadFingerprint = fingerprint.ToLowerInvariant();
            if (submission.RetryAfterUtc is { } acceptedRetryAfterUtc)
                state.NextDeliveryAtUtc = acceptedRetryAfterUtc;
            else if (!IsFinal(submission.Status))
                state.NextDeliveryAtUtc = now + _redeliveryDelay;
            if (IsFinal(submission.Status))
            {
                state.Completed = true;
                state.CompletedAtUtc = now;
            }
            return new(ExtensionScanSubmissionDisposition.Accepted, ToSnapshot(state, scanEvent, now));
        }
    }

    internal IReadOnlyList<ExtensionScanDelivery> GetSnapshot()
    {
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PruneCore(now);
            return _deliveries.Values
                .Where(delivery => _events.ContainsKey(delivery.TaskId))
                .Select(delivery => ToSnapshot(delivery, _events[delivery.TaskId].ScanEvent, now))
                .OrderBy(delivery => delivery.ScanEvent.OccurredAtUtc)
                .ThenBy(delivery => delivery.DeliveryId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private int CountActiveEvents(DateTimeOffset now) => _events.Values.Count(state =>
        state.ScanEvent.ExpiresAtUtc >= now
        && state.DeliveryIds.Any(deliveryId =>
            _deliveries.TryGetValue(deliveryId, out DeliveryState? delivery)
            && !delivery.Completed
            && !delivery.Expired));

    private int CountPendingDeliveries(string extensionInstanceId, DateTimeOffset now) =>
        _deliveries.Values.Count(delivery =>
            string.Equals(delivery.ExtensionInstanceId, extensionInstanceId, StringComparison.Ordinal)
            && !delivery.Completed
            && !delivery.Expired
            && _events.TryGetValue(delivery.TaskId, out EventState? state)
            && state.ScanEvent.ExpiresAtUtc >= now);

    private void PruneCore(DateTimeOffset now)
    {
        foreach (DeliveryState delivery in _deliveries.Values)
        {
            if (_events.TryGetValue(delivery.TaskId, out EventState? state)
                && now > state.ScanEvent.ExpiresAtUtc)
            {
                delivery.Expired = !delivery.Completed;
            }
        }

        string[] eventIdsToRemove = _events.Values
            .Where(state => now > state.ScanEvent.ExpiresAtUtc + _completedRetention)
            .Select(state => state.ScanEvent.TaskId)
            .ToArray();
        foreach (string eventId in eventIdsToRemove)
        {
            if (!_events.Remove(eventId, out EventState? state)) continue;
            foreach (string deliveryId in state.DeliveryIds)
                _deliveries.Remove(deliveryId);
        }
    }

    private static bool IsFinal(ExtensionScanResultStatus status) => status is not (
        ExtensionScanResultStatus.InProgress
        or ExtensionScanResultStatus.Unavailable
        or ExtensionScanResultStatus.RateLimited);

    private static ExtensionScanEvent NormalizeEvent(ExtensionScanEvent scanEvent)
    {
        string taskId = NormalizeIdentifier(scanEvent.TaskId, "任务 ID");
        string originNodeId = NormalizeIdentifier(scanEvent.OriginNodeId, "来源节点 ID");
        string sessionId = NormalizeIdentifier(scanEvent.RecordingSessionId, "录像会话 ID");
        string trackingNumber = scanEvent.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
        if (trackingNumber.Length is < 1 or > 128 || trackingNumber.Any(char.IsControl))
            throw new InvalidDataException("快递单号格式无效");
        string mode = scanEvent.RecordingMode?.Trim() ?? "";
        if (mode.Length is < 1 or > 32 || mode.Any(char.IsControl))
            throw new InvalidDataException("录像模式格式无效");
        if (scanEvent.OccurredAtUtc == default
            || scanEvent.SoftDeadlineUtc < scanEvent.OccurredAtUtc
            || scanEvent.ExpiresAtUtc < scanEvent.SoftDeadlineUtc
            || scanEvent.ExpiresAtUtc - scanEvent.OccurredAtUtc > TimeSpan.FromMinutes(2))
        {
            throw new InvalidDataException("扫码任务时间范围无效");
        }
        string[] capabilities = NormalizeCapabilities(scanEvent.RequestedCapabilities);
        if (capabilities.Length == 0) throw new InvalidDataException("扫码任务必须申请至少一种能力");
        return scanEvent with
        {
            TaskId = taskId,
            OriginNodeId = originNodeId,
            RecordingSessionId = sessionId,
            TrackingNumber = trackingNumber,
            RecordingMode = mode,
            RequestedCapabilities = capabilities
        };
    }

    private static ExtensionScanTarget NormalizeTarget(ExtensionScanTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target with
        {
            ExtensionInstanceId = NormalizeIdentifier(target.ExtensionInstanceId, "扩展实例 ID"),
            Capabilities = NormalizeCapabilities(target.Capabilities)
        };
    }

    private static ExtensionScanTarget MergeTargets(
        string extensionInstanceId,
        IEnumerable<ExtensionScanTarget> targets) => new()
    {
        ExtensionInstanceId = extensionInstanceId,
        Capabilities = targets.SelectMany(target => target.Capabilities)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray()
    };

    private static string[] NormalizeCapabilities(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(ExtensionScanCapabilities.Supported.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        string result = value?.Trim() ?? "";
        if (!IdentifierPattern.IsMatch(result)) throw new InvalidDataException($"{fieldName}格式无效");
        return result;
    }

    private static ExtensionScanDelivery ToSnapshot(
        DeliveryState state,
        ExtensionScanEvent scanEvent,
        DateTimeOffset now) => new()
    {
        DeliveryId = state.DeliveryId,
        ExtensionInstanceId = state.ExtensionInstanceId,
        ScanEvent = scanEvent,
        RequestedCapabilities = state.RequestedCapabilities,
        State = state.Completed
            ? ExtensionScanDeliveryState.Completed
            : state.Expired || now > scanEvent.ExpiresAtUtc
                ? ExtensionScanDeliveryState.Expired
                : state.DeliveryAttempts > 0
                    ? ExtensionScanDeliveryState.Delivered
                    : ExtensionScanDeliveryState.Pending,
        DeliveryAttempts = state.DeliveryAttempts,
        FirstDeliveredAtUtc = state.FirstDeliveredAtUtc,
        LastDeliveredAtUtc = state.LastDeliveredAtUtc,
        NextDeliveryAtUtc = state.NextDeliveryAtUtc,
        LatestRevision = state.LatestRevision,
        LatestStatus = state.LatestStatus
    };

    private sealed class EventState
    {
        internal ExtensionScanEvent ScanEvent { get; init; } = new();
        internal IReadOnlyList<string> DeliveryIds { get; init; } = [];
    }

    private sealed class DeliveryState
    {
        internal string DeliveryId { get; init; } = "";
        internal string TaskId { get; init; } = "";
        internal string ExtensionInstanceId { get; init; } = "";
        internal IReadOnlyList<string> RequestedCapabilities { get; init; } = [];
        internal int DeliveryAttempts { get; set; }
        internal DateTimeOffset? FirstDeliveredAtUtc { get; set; }
        internal DateTimeOffset? LastDeliveredAtUtc { get; set; }
        internal DateTimeOffset? NextDeliveryAtUtc { get; set; }
        internal long LatestRevision { get; set; }
        internal ExtensionScanResultStatus? LatestStatus { get; set; }
        internal string LatestPayloadFingerprint { get; set; } = "";
        internal bool Completed { get; set; }
        internal DateTimeOffset? CompletedAtUtc { get; set; }
        internal bool Expired { get; set; }
    }
}
