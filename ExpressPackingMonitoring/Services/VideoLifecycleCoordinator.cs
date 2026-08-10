namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 按记录 ID 的异步所有权锁：归档、清理、删除同一记录时互斥，
/// 失败任务不得删除其他任务已发布的有效目标。
/// </summary>
internal static class VideoLifecycleCoordinator
{
    private static readonly object Sync = new();
    private static readonly Dictionary<long, Entry> Entries = new();

    public static async Task<IDisposable> EnterAsync(long recordId, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(recordId, out entry!))
            {
                entry = new Entry();
                Entries[recordId] = entry;
            }
            entry.RefCount++;
        }

        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(recordId, entry);
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly long _recordId;
        private readonly Entry _entry;
        private int _disposed;

        public Lease(long recordId, Entry entry)
        {
            _recordId = recordId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _entry.Semaphore.Release();
            lock (Sync)
            {
                _entry.RefCount--;
                if (_entry.RefCount <= 0)
                    Entries.Remove(_recordId);
            }
        }
    }
}
