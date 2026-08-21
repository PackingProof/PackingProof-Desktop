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

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(recordId, entry);
        }
        catch
        {
            // WaitAsync 未成功取得信号量时没有 Lease 可以负责回收引用，
            // 否则取消/失败的等待会让 Entries 永久保留该记录。
            ReleaseReference(recordId, entry);
            throw;
        }
    }

    private static void ReleaseReference(long recordId, Entry entry)
    {
        lock (Sync)
        {
            // 只有当前字典仍指向同一个 Entry 时才修改它；旧 Entry
            // 可能已经被后续请求替换，不能误伤新锁。
            if (!Entries.TryGetValue(recordId, out Entry? current)
                || !ReferenceEquals(current, entry))
            {
                return;
            }

            entry.RefCount--;
            if (entry.RefCount <= 0)
                Entries.Remove(recordId);
        }
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
            ReleaseReference(_recordId, _entry);
        }
    }
}
