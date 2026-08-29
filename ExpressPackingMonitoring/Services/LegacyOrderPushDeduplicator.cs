using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class OrderBroadcastRequest
{
    public List<OrderInfo> Orders { get; set; } = [];
    public List<string> TargetNodeIds { get; set; } = [];
}

internal readonly record struct LegacyOrderPushExecution<TResult>(TResult Result, bool IsDuplicate);

internal sealed class LegacyOrderPushDeduplicator
{
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);

    private const int MaximumRememberedFingerprints = 2048;
    private readonly Dictionary<string, ExecutionEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    internal LegacyOrderPushDeduplicator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal LegacyOrderPushExecution<TResult> Execute<TResult>(
        string? sourceAddress,
        string scope,
        List<OrderInfo> items,
        Func<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);
        if (items.Any(item => item.IsTest))
            return new LegacyOrderPushExecution<TResult>(operation(), false);

        string fingerprint = CreateFingerprint(sourceAddress, scope, items);
        ExecutionEntry entry;
        bool ownsExecution;
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PruneExpired(now);
            if (_entries.TryGetValue(fingerprint, out ExecutionEntry? existing))
            {
                entry = existing;
                ownsExecution = false;
            }
            else
            {
                entry = new ExecutionEntry();
                _entries[fingerprint] = entry;
                ownsExecution = true;
            }
        }

        if (!ownsExecution)
        {
            ExecutionOutcome outcome = entry.Completion.Task.GetAwaiter().GetResult();
            outcome.Failure?.Throw();
            return new LegacyOrderPushExecution<TResult>((TResult)outcome.Result!, true);
        }

        try
        {
            TResult result = operation();
            lock (_sync)
            {
                entry.ExpiresAtUtc = _timeProvider.GetUtcNow() + DuplicateWindow;
                entry.Completion.TrySetResult(new ExecutionOutcome(result, null));
                TrimOverflow();
            }
            return new LegacyOrderPushExecution<TResult>(result, false);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(fingerprint, out ExecutionEntry? current)
                    && ReferenceEquals(current, entry))
                    _entries.Remove(fingerprint);
                entry.Completion.TrySetResult(new ExecutionOutcome(null, ExceptionDispatchInfo.Capture(ex)));
            }
            throw;
        }
    }

    internal static string CreateBroadcastScope(IEnumerable<string> targetNodeIds) =>
        $"broadcast:{string.Join(',', targetNodeIds
            .Select(nodeId => nodeId.Trim().ToUpperInvariant())
            .Order(StringComparer.Ordinal))}";

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (string fingerprint in _entries
            .Where(entry => entry.Value.Completion.Task.IsCompleted && entry.Value.ExpiresAtUtc <= now)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _entries.Remove(fingerprint);
        }
    }

    private void TrimOverflow()
    {
        if (_entries.Count <= MaximumRememberedFingerprints) return;
        foreach (string fingerprint in _entries
            .Where(entry => entry.Value.Completion.Task.IsCompleted)
            .OrderBy(entry => entry.Value.ExpiresAtUtc)
            .Take(_entries.Count - MaximumRememberedFingerprints)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _entries.Remove(fingerprint);
        }
    }

    private static string CreateFingerprint(
        string? sourceAddress,
        string scope,
        IReadOnlyList<OrderInfo> items)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceAddress = NormalizeSourceAddress(sourceAddress),
            scope = scope?.Trim() ?? "",
            orders = items.Select(item => new
            {
                trackingNumber = item.TrackingNumber?.Trim().ToUpperInvariant() ?? "",
                orderId = item.OrderId ?? "",
                buyerMessage = item.BuyerMessage ?? "",
                sellerMemo = item.SellerMemo ?? "",
                productInfo = item.ProductInfo ?? "",
                item.TotalItemCount,
                item.MergedOrderCount,
                providerId = item.ProviderId ?? "",
                item.HasRefund,
                item.IsPrintedRefund,
                refundStatus = item.RefundStatus ?? "",
                refundProductInfo = item.RefundProductInfo ?? ""
            })
        });
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static string NormalizeSourceAddress(string? value)
    {
        string address = value?.Trim() ?? "unknown";
        if (IPAddress.TryParse(address, out IPAddress? parsed) && parsed.IsIPv4MappedToIPv6)
            return parsed.MapToIPv4().ToString();
        return address;
    }

    private sealed class ExecutionEntry
    {
        internal TaskCompletionSource<ExecutionOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MaxValue;
    }

    private sealed record ExecutionOutcome(object? Result, ExceptionDispatchInfo? Failure);
}
