using System.Security.Cryptography;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class OrderBroadcastRequest
{
    public List<OrderInfo> Orders { get; set; } = [];
    public List<string> TargetNodeIds { get; set; } = [];
}

internal sealed class LegacyOrderPushDeduplicator
{
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);

    private const int MaximumRememberedFingerprints = 2048;
    private readonly Dictionary<string, FingerprintState> _fingerprints = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    internal LegacyOrderPushDeduplicator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal LegacyOrderPushDeduplication Begin(List<OrderInfo> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var accepted = new List<OrderInfo>(items.Count);
        var reservations = new List<FingerprintReservation>(items.Count);

        lock (_sync)
        {
            PruneExpired(now);
            foreach (OrderInfo item in items)
            {
                if (item.IsTest)
                {
                    accepted.Add(item);
                    continue;
                }

                string fingerprint = CreateFingerprint(item);
                if (_fingerprints.TryGetValue(fingerprint, out FingerprintState? existing)
                    && (existing.InFlight || existing.ExpiresAtUtc > now))
                    continue;

                Guid token = Guid.NewGuid();
                _fingerprints[fingerprint] = new FingerprintState(token, DateTimeOffset.MaxValue, true);
                reservations.Add(new FingerprintReservation(fingerprint, token));
                accepted.Add(item);
            }
        }

        return new LegacyOrderPushDeduplication(this, accepted, reservations);
    }

    private void Complete(IReadOnlyList<FingerprintReservation> reservations)
    {
        DateTimeOffset expiresAtUtc = _timeProvider.GetUtcNow() + DuplicateWindow;
        lock (_sync)
        {
            foreach (FingerprintReservation reservation in reservations)
            {
                if (_fingerprints.TryGetValue(reservation.Fingerprint, out FingerprintState? state)
                    && state.Token == reservation.Token)
                    _fingerprints[reservation.Fingerprint] = state with { ExpiresAtUtc = expiresAtUtc, InFlight = false };
            }
            TrimOverflow();
        }
    }

    private void Rollback(IReadOnlyList<FingerprintReservation> reservations)
    {
        lock (_sync)
        {
            foreach (FingerprintReservation reservation in reservations)
            {
                if (_fingerprints.TryGetValue(reservation.Fingerprint, out FingerprintState? state)
                    && state.Token == reservation.Token
                    && state.InFlight)
                    _fingerprints.Remove(reservation.Fingerprint);
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (string fingerprint in _fingerprints
            .Where(entry => !entry.Value.InFlight && entry.Value.ExpiresAtUtc <= now)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _fingerprints.Remove(fingerprint);
        }
    }

    private void TrimOverflow()
    {
        if (_fingerprints.Count <= MaximumRememberedFingerprints) return;
        foreach (string fingerprint in _fingerprints
            .Where(entry => !entry.Value.InFlight)
            .OrderBy(entry => entry.Value.ExpiresAtUtc)
            .Take(_fingerprints.Count - MaximumRememberedFingerprints)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _fingerprints.Remove(fingerprint);
        }
    }

    private static string CreateFingerprint(OrderInfo item)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
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
        });
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private sealed record FingerprintState(Guid Token, DateTimeOffset ExpiresAtUtc, bool InFlight);
    internal sealed record FingerprintReservation(string Fingerprint, Guid Token);

    internal sealed class LegacyOrderPushDeduplication : IDisposable
    {
        private readonly LegacyOrderPushDeduplicator _owner;
        private readonly IReadOnlyList<FingerprintReservation> _reservations;
        private bool _completed;

        internal LegacyOrderPushDeduplication(
            LegacyOrderPushDeduplicator owner,
            List<OrderInfo> acceptedItems,
            IReadOnlyList<FingerprintReservation> reservations)
        {
            _owner = owner;
            AcceptedItems = acceptedItems;
            _reservations = reservations;
        }

        internal List<OrderInfo> AcceptedItems { get; }

        internal void Complete()
        {
            if (_completed) return;
            _owner.Complete(_reservations);
            _completed = true;
        }

        public void Dispose()
        {
            if (!_completed) _owner.Rollback(_reservations);
        }
    }
}
