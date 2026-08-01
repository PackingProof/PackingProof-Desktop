namespace ExpressPackingMonitoring.Services;

internal enum LanRequestCategory
{
    General,
    Enrollment,
    Heartbeat,
    BackupTransfer,
    MediaStream,
    ClipWork
}

internal sealed class LanRequestRateLimiter
{
    private sealed class ClientState
    {
        public DateTimeOffset LastSeen { get; set; }
        public int ActiveRequests { get; set; }
        public Dictionary<LanRequestCategory, int> ActiveByCategory { get; } = [];
        public Dictionary<LanRequestCategory, WindowCounter> Windows { get; } = [];
    }

    private sealed class WindowCounter
    {
        public DateTimeOffset StartedAt { get; set; }
        public int Count { get; set; }
    }

    private sealed class Lease(LanRequestRateLimiter owner, string clientKey, LanRequestCategory category)
        : IDisposable
    {
        private LanRequestRateLimiter? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Exit(clientKey, category);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxClients;
    private int _operationsSinceCleanup;

    internal LanRequestRateLimiter(int maxClients = 2048)
    {
        _maxClients = Math.Max(32, maxClients);
    }

    internal bool TryEnter(
        string? clientAddress,
        LanRequestCategory category,
        out IDisposable? lease,
        out int retryAfterSeconds,
        DateTimeOffset? nowOverride = null)
    {
        string key = string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress.Trim();
        DateTimeOffset now = nowOverride ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (++_operationsSinceCleanup >= 128)
            {
                Cleanup(now);
                _operationsSinceCleanup = 0;
            }

            if (!_clients.TryGetValue(key, out ClientState? state))
            {
                if (_clients.Count >= _maxClients)
                {
                    lease = null;
                    retryAfterSeconds = 30;
                    return false;
                }
                state = new ClientState();
                _clients.Add(key, state);
            }

            state.LastSeen = now;
            (int requestsPerMinute, int categoryConcurrency) = GetLimits(category);
            state.ActiveByCategory.TryGetValue(category, out int activeForCategory);
            if (state.ActiveRequests >= 8 || activeForCategory >= categoryConcurrency)
            {
                lease = null;
                retryAfterSeconds = 2;
                return false;
            }

            if (!state.Windows.TryGetValue(category, out WindowCounter? counter)
                || now - counter.StartedAt >= TimeSpan.FromMinutes(1))
            {
                counter = new WindowCounter { StartedAt = now };
                state.Windows[category] = counter;
            }
            if (counter.Count >= requestsPerMinute)
            {
                lease = null;
                retryAfterSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling((counter.StartedAt.AddMinutes(1) - now).TotalSeconds));
                return false;
            }

            counter.Count++;
            state.ActiveRequests++;
            state.ActiveByCategory[category] = activeForCategory + 1;
            lease = new Lease(this, key, category);
            retryAfterSeconds = 0;
            return true;
        }
    }

    internal int TrackedClientCount
    {
        get
        {
            lock (_gate)
                return _clients.Count;
        }
    }

    private static (int RequestsPerMinute, int Concurrent) GetLimits(LanRequestCategory category) =>
        category switch
        {
            LanRequestCategory.Enrollment => (24, 2),
            LanRequestCategory.Heartbeat => (180, 4),
            LanRequestCategory.BackupTransfer => (900, 6),
            LanRequestCategory.MediaStream => (120, 4),
            LanRequestCategory.ClipWork => (30, 2),
            _ => (300, 8)
        };

    private void Exit(string key, LanRequestCategory category)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(key, out ClientState? state))
                return;
            state.ActiveRequests = Math.Max(0, state.ActiveRequests - 1);
            if (state.ActiveByCategory.TryGetValue(category, out int active))
                state.ActiveByCategory[category] = Math.Max(0, active - 1);
        }
    }

    private void Cleanup(DateTimeOffset now)
    {
        foreach (string key in _clients
                     .Where(pair => pair.Value.ActiveRequests == 0
                                    && now - pair.Value.LastSeen > TimeSpan.FromMinutes(5))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _clients.Remove(key);
        }
    }
}
