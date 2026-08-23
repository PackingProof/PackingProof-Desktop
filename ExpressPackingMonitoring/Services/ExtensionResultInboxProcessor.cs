using System.IO;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal enum ExtensionResultProcessingDisposition
{
    Empty,
    Applied,
    RetryScheduled,
    DeadLettered
}

internal sealed record ExtensionResultProcessingOutcome(
    ExtensionResultProcessingDisposition Disposition,
    long? InboxId = null,
    string Error = "");

/// <summary>
/// Claims one durable result and routes it to the existing measurement or order business model.
/// Business callbacks are deliberately replayable: a crash after a partial side effect must not
/// turn a durable Inbox duplicate into a silently lost UI/database update.
/// </summary>
internal sealed class ExtensionResultInboxProcessor
{
    internal const int DefaultMaxAttempts = 5;
    internal static readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromMinutes(2);

    private readonly ExtensionResultInboxStore _inbox;
    private readonly ExtensionMeasurementResultApplier _measurementApplier;
    private readonly ExtensionOrderResultApplier _orderApplier;
    private readonly Action<ExtensionResultInboxItem, ExtensionOrderMergeResult> _orderResultApplied;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;

    internal ExtensionResultInboxProcessor(
        ExtensionResultInboxStore inbox,
        ExtensionMeasurementResultApplier measurementApplier,
        ExtensionOrderResultApplier orderApplier,
        Action<ExtensionResultInboxItem, ExtensionOrderMergeResult> orderResultApplied,
        TimeProvider? timeProvider = null,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maxRetryDelay = null)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _measurementApplier = measurementApplier ?? throw new ArgumentNullException(nameof(measurementApplier));
        _orderApplier = orderApplier ?? throw new ArgumentNullException(nameof(orderApplier));
        _orderResultApplied = orderResultApplied ?? throw new ArgumentNullException(nameof(orderResultApplied));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxAttempts = maxAttempts > 0
            ? maxAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _initialRetryDelay = initialRetryDelay ?? DefaultInitialRetryDelay;
        _maxRetryDelay = maxRetryDelay ?? DefaultMaxRetryDelay;
        if (_initialRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialRetryDelay));
        if (_maxRetryDelay < _initialRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(maxRetryDelay));
    }

    internal ExtensionResultProcessingOutcome ProcessNext()
    {
        ExtensionResultInboxItem? item = _inbox.ClaimNext();
        if (item == null)
            return new ExtensionResultProcessingOutcome(ExtensionResultProcessingDisposition.Empty);

        try
        {
            switch (item.Capability)
            {
                case ExtensionScanCapabilities.MeasurementCapture:
                    _measurementApplier.Apply(item);
                    break;
                case ExtensionScanCapabilities.OrderLookup:
                case ExtensionScanCapabilities.RefundLookup:
                    ExtensionOrderMergeResult merged = _orderApplier.Apply(item);
                    _orderResultApplied(item, merged);
                    break;
                default:
                    throw new InvalidDataException("扩展结果能力无法路由");
            }

            if (!_inbox.MarkApplied(item.Id))
                throw new InvalidOperationException("扩展结果已离开 Applying 状态");
            return new ExtensionResultProcessingOutcome(
                ExtensionResultProcessingDisposition.Applied,
                item.Id);
        }
        catch (Exception ex)
        {
            string error = $"{ex.GetType().Name}: {ex.Message}";
            if (item.AttemptCount >= _maxAttempts)
            {
                if (!_inbox.MarkDeadLetter(item.Id, error))
                    throw new InvalidOperationException("扩展结果无法转入死信状态", ex);
                return new ExtensionResultProcessingOutcome(
                    ExtensionResultProcessingDisposition.DeadLettered,
                    item.Id,
                    error);
            }

            DateTimeOffset retryAt = _timeProvider.GetUtcNow() + GetRetryDelay(item.AttemptCount);
            if (!_inbox.MarkFailed(item.Id, error, retryAt))
                throw new InvalidOperationException("扩展结果无法安排重试", ex);
            return new ExtensionResultProcessingOutcome(
                ExtensionResultProcessingDisposition.RetryScheduled,
                item.Id,
                error);
        }
    }

    private TimeSpan GetRetryDelay(int attemptCount)
    {
        int exponent = Math.Clamp(attemptCount - 1, 0, 20);
        double milliseconds = _initialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, _maxRetryDelay.TotalMilliseconds));
    }
}
