using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExpressPackingMonitoring.Services;

internal enum StorageWriteMediaKind
{
    Unknown,
    HardDisk,
    SolidState
}

internal static class StorageWriteMediaTypePolicy
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;

    internal static StorageWriteMediaKind Detect(string volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot) || !OperatingSystem.IsWindows())
            return StorageWriteMediaKind.Unknown;
        string root = Path.GetPathRoot(volumeRoot) ?? "";
        if (root.Length < 2 || root[1] != ':')
            return StorageWriteMediaKind.Unknown;

        using SafeFileHandle handle = CreateFile(
            $@"\\.\{char.ToUpperInvariant(root[0])}:",
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
            return StorageWriteMediaKind.Unknown;

        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = PropertyStandardQuery
        };
        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                ref query,
                (uint)Marshal.SizeOf<StoragePropertyQuery>(),
                out DeviceSeekPenaltyDescriptor descriptor,
                (uint)Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out _,
                IntPtr.Zero)
            || descriptor.Size == 0)
        {
            return StorageWriteMediaKind.Unknown;
        }

        return descriptor.IncursSeekPenalty == 0
            ? StorageWriteMediaKind.SolidState
            : StorageWriteMediaKind.HardDisk;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        ref StoragePropertyQuery inputBuffer,
        uint inputBufferSize,
        out DeviceSeekPenaltyDescriptor outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}

internal sealed class HostUploadStorageAdmissionController
{
    private readonly object _sync = new();
    private readonly Dictionary<string, UploadReservation> _reservations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VolumeState> _volumes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, StorageWriteMediaKind> _mediaTypeDetector;
    private readonly Func<long> _timestampProvider;
    private readonly TimeSpan _adaptationWindow;

    public HostUploadStorageAdmissionController(
        Func<string, StorageWriteMediaKind>? mediaTypeDetector = null,
        Func<long>? timestampProvider = null,
        TimeSpan? adaptationWindow = null)
    {
        _mediaTypeDetector = mediaTypeDetector ?? StorageWriteMediaTypePolicy.Detect;
        _timestampProvider = timestampProvider ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _adaptationWindow = adaptationWindow ?? TimeSpan.FromSeconds(1);
    }

    public void ReserveUpload(
        string uploadId,
        string deviceId,
        string volumeKey,
        long totalBytes,
        long receivedBytes,
        long availableBytes,
        long reserveBytes,
        string? volumeProbePath = null,
        string? physicalDiskKey = null)
    {
        string normalizedUploadId = Normalize(uploadId);
        string normalizedDeviceId = Normalize(deviceId);
        string normalizedVolume = Normalize(volumeKey);
        string normalizedPhysicalDisk = string.IsNullOrWhiteSpace(physicalDiskKey)
            ? normalizedVolume
            : Normalize(physicalDiskKey);
        long remainingBytes = Math.Max(0, totalBytes - Math.Max(0, receivedBytes));
        lock (_sync)
        {
            long otherReservedBytes = _reservations.Values
                .Where(item =>
                    !string.Equals(
                        item.UploadId,
                        normalizedUploadId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        item.VolumeKey,
                        normalizedVolume,
                        StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.RemainingBytes);
            long usableBytes = Math.Max(0, availableBytes - Math.Max(0, reserveBytes));
            if (remainingBytes > usableBytes)
            {
                throw new MobileBackupStorageUnavailableException(
                    "主机存储空间不足，无法接收新的上传任务");
            }
            if (remainingBytes > Math.Max(0, usableBytes - otherReservedBytes))
                throw new HostUploadStorageBusyException(2);

            _ = GetVolumeStateCore(
                normalizedPhysicalDisk,
                string.IsNullOrWhiteSpace(volumeProbePath)
                    ? normalizedVolume
                    : volumeProbePath);
            if (_reservations.TryGetValue(normalizedUploadId, out UploadReservation? existing)
                && !string.Equals(
                    existing.VolumeKey,
                    normalizedVolume,
                    StringComparison.OrdinalIgnoreCase))
            {
                ReleaseUploadCore(normalizedUploadId);
            }
            _reservations[normalizedUploadId] = new UploadReservation(
                normalizedUploadId,
                normalizedDeviceId,
                normalizedVolume,
                normalizedPhysicalDisk,
                remainingBytes);
        }
    }

    public HostUploadWriteLease AcquireWrite(
        string uploadId,
        string deviceId,
        string volumeKey,
        int retryAfterSeconds = 1)
    {
        string normalizedUploadId = Normalize(uploadId);
        string normalizedDeviceId = Normalize(deviceId);
        string normalizedVolume = Normalize(volumeKey);
        lock (_sync)
        {
            if (!_reservations.ContainsKey(normalizedUploadId))
                throw new MobileBackupStorageUnavailableException("上传任务尚未完成存储空间预留");

            UploadReservation reservation = _reservations[normalizedUploadId];
            VolumeState volume = GetVolumeStateCore(
                reservation.PhysicalDiskKey,
                normalizedVolume);
        DateTimeOffset now = DateTimeOffset.UtcNow;
            RemoveExpiredWaiters(volume, now);
            if (volume.ActiveByDevice.TryGetValue(
                    normalizedDeviceId,
                    out string? activeUploadId)
                && !string.Equals(
                    activeUploadId,
                    normalizedUploadId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new HostUploadStorageBusyException(retryAfterSeconds);
            }

            if (volume.WaitingDevicesSet.Add(normalizedDeviceId))
                volume.WaitingDevices.Enqueue(normalizedDeviceId);
            volume.WaitingAt[normalizedDeviceId] = now;
            string waitingDevice = volume.WaitingDevices.Peek();
            bool canWrite = volume.ActiveUploads.Count < volume.WriteConcurrency.CurrentLimit
                && string.Equals(
                    waitingDevice,
                    normalizedDeviceId,
                    StringComparison.OrdinalIgnoreCase);
            if (!canWrite)
                throw new HostUploadStorageBusyException(retryAfterSeconds);

            volume.WaitingDevices.Dequeue();
            _ = volume.WaitingDevicesSet.Remove(normalizedDeviceId);
            _ = volume.WaitingAt.Remove(normalizedDeviceId);
            if (!volume.ActiveUploads.Add(normalizedUploadId))
                throw new HostUploadStorageBusyException(retryAfterSeconds);
            volume.ActiveByDevice[normalizedDeviceId] = normalizedUploadId;
            return new HostUploadWriteLease(
                this,
                normalizedUploadId,
                normalizedDeviceId,
                normalizedVolume,
                reservation.PhysicalDiskKey,
                _timestampProvider);
        }
    }

    public void CommitChunk(string uploadId, int bytesWritten)
    {
        if (bytesWritten <= 0)
            return;
        string normalizedUploadId = Normalize(uploadId);
        lock (_sync)
        {
            if (!_reservations.TryGetValue(normalizedUploadId, out UploadReservation? reservation))
                return;
            reservation.RemainingBytes = Math.Max(
                0,
                reservation.RemainingBytes - bytesWritten);
        }
    }

    public void ReleaseUpload(string uploadId)
    {
        string normalizedUploadId = Normalize(uploadId);
        lock (_sync)
            ReleaseUploadCore(normalizedUploadId);
    }

    public long GetReservedBytes(string volumeKey)
    {
        string normalizedVolume = Normalize(volumeKey);
        lock (_sync)
        {
            return _reservations.Values
                .Where(item => string.Equals(
                    item.VolumeKey,
                    normalizedVolume,
                    StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.RemainingBytes);
        }
    }

    internal int GetConcurrentWriteLimit(string physicalDiskKey)
    {
        string normalizedPhysicalDisk = Normalize(physicalDiskKey);
        lock (_sync)
            return GetVolumeStateCore(normalizedPhysicalDisk, normalizedPhysicalDisk)
                .WriteConcurrency.CurrentLimit;
    }

    internal HostUploadStorageWriteLoad GetWriteLoad(
        string physicalDiskKey,
        string? volumeProbePath = null)
    {
        string normalizedPhysicalDisk = Normalize(physicalDiskKey);
        lock (_sync)
        {
            VolumeState volume = GetVolumeStateCore(
                normalizedPhysicalDisk,
                string.IsNullOrWhiteSpace(volumeProbePath)
                    ? normalizedPhysicalDisk
                    : volumeProbePath);
            RemoveExpiredWaiters(volume, DateTimeOffset.UtcNow);
            return new HostUploadStorageWriteLoad(
                volume.ActiveUploads.Count,
                volume.WaitingDevices.Count,
                volume.WriteConcurrency.CurrentLimit);
        }
    }

    internal void ReleaseWrite(
        string uploadId,
        string deviceId,
        string volumeKey,
        string physicalDiskKey,
        int bytesWritten,
        TimeSpan duration)
    {
        string normalizedUploadId = Normalize(uploadId);
        string normalizedDeviceId = Normalize(deviceId);
        string normalizedVolume = Normalize(volumeKey);
        string normalizedPhysicalDisk = Normalize(physicalDiskKey);
        lock (_sync)
        {
            if (!_volumes.TryGetValue(normalizedPhysicalDisk, out VolumeState? volume))
                return;
            volume.ActiveUploads.Remove(normalizedUploadId);
            if (volume.ActiveByDevice.TryGetValue(
                    normalizedDeviceId,
                    out string? activeUploadId)
                && string.Equals(
                    activeUploadId,
                    normalizedUploadId,
                    StringComparison.OrdinalIgnoreCase))
            {
                volume.ActiveByDevice.Remove(normalizedDeviceId);
            }
            RemoveWaiter(volume, normalizedDeviceId);
            volume.WriteConcurrency.RecordWrite(
                bytesWritten,
                duration,
                volume.WaitingDevices.Count > 0);
        }
    }

    internal void RecordWriteFailure(string physicalDiskKey)
    {
        string normalizedPhysicalDisk = Normalize(physicalDiskKey);
        lock (_sync)
        {
            if (_volumes.TryGetValue(normalizedPhysicalDisk, out VolumeState? volume))
                volume.WriteConcurrency.RecordWriteFailure();
        }
    }

    private void ReleaseUploadCore(string uploadId)
    {
        if (!_reservations.Remove(uploadId, out UploadReservation? reservation))
            return;
        if (_volumes.TryGetValue(reservation.PhysicalDiskKey, out VolumeState? volume))
        {
            volume.ActiveUploads.Remove(uploadId);
            RemoveWaiter(volume, reservation.DeviceId);
            if (volume.ActiveByDevice.TryGetValue(
                    reservation.DeviceId,
                    out string? activeUploadId)
                && string.Equals(activeUploadId, uploadId, StringComparison.OrdinalIgnoreCase))
            {
                volume.ActiveByDevice.Remove(reservation.DeviceId);
            }
            if (volume.ActiveUploads.Count == 0
                && volume.WaitingDevices.Count == 0
                && volume.ActiveByDevice.Count == 0)
            {
                _volumes.Remove(reservation.PhysicalDiskKey);
            }
        }
    }

    private VolumeState GetVolumeStateCore(
        string physicalDiskKey,
        string volumeProbePath)
    {
        if (_volumes.TryGetValue(physicalDiskKey, out VolumeState? volume))
            return volume;
        StorageWriteMediaKind mediaType = _mediaTypeDetector(volumeProbePath);
        volume = new VolumeState(
            new AdaptiveWriteConcurrencyPolicy(
                initialConcurrency: mediaType == StorageWriteMediaKind.SolidState ? 4 : 2,
                timestampProvider: _timestampProvider,
                adaptationWindow: _adaptationWindow));
        _volumes[physicalDiskKey] = volume;
        return volume;
    }

    private static void RemoveExpiredWaiters(VolumeState volume, DateTimeOffset now)
    {
        while (volume.WaitingDevices.Count > 0)
        {
            string deviceId = volume.WaitingDevices.Peek();
            if (volume.WaitingAt.TryGetValue(deviceId, out DateTimeOffset waitingAt)
                && now - waitingAt <= TimeSpan.FromSeconds(2))
            {
                break;
            }
            volume.WaitingDevices.Dequeue();
            _ = volume.WaitingDevicesSet.Remove(deviceId);
            _ = volume.WaitingAt.Remove(deviceId);
        }
    }

    private static void RemoveWaiter(VolumeState volume, string deviceId)
    {
        if (!volume.WaitingDevicesSet.Remove(deviceId))
            return;
        _ = volume.WaitingAt.Remove(deviceId);
        if (volume.WaitingDevices.Count == 0)
            return;
        string[] remaining = volume.WaitingDevices
            .Where(item => !string.Equals(
                item,
                deviceId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        volume.WaitingDevices.Clear();
        foreach (string item in remaining)
            volume.WaitingDevices.Enqueue(item);
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("上传存储准入参数不能为空", nameof(value))
            : value.Trim();

    private sealed class UploadReservation(
        string uploadId,
        string deviceId,
        string volumeKey,
        string physicalDiskKey,
        long remainingBytes)
    {
        public string UploadId { get; } = uploadId;
        public string DeviceId { get; } = deviceId;
        public string VolumeKey { get; } = volumeKey;
        public string PhysicalDiskKey { get; } = physicalDiskKey;
        public long RemainingBytes { get; set; } = remainingBytes;
    }

    private sealed class VolumeState(
        AdaptiveWriteConcurrencyPolicy writeConcurrency)
    {
        public AdaptiveWriteConcurrencyPolicy WriteConcurrency { get; } =
            writeConcurrency;
        public HashSet<string> ActiveUploads { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ActiveByDevice { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Queue<string> WaitingDevices { get; } = new();
        public HashSet<string> WaitingDevicesSet { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> WaitingAt { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class HostUploadStorageBusyException : Exception
{
    public HostUploadStorageBusyException(int retryAfterSeconds = 1)
        : base("主机写入槽位繁忙，请稍后自动继续")
    {
        RetryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, 300);
    }

    public int RetryAfterSeconds { get; }
}

internal sealed record HostUploadStorageAvailability(
    bool IsAvailable,
    string State,
    int RetryAfterSeconds,
    long ReservedBytes);

internal readonly record struct HostUploadStorageWriteLoad(
    int ActiveWrites,
    int WaitingWriters,
    int ConcurrencyLimit);

internal sealed class HostUploadWriteLease : IDisposable
{
    private readonly HostUploadStorageAdmissionController _controller;
    private readonly string _uploadId;
    private readonly string _deviceId;
    private readonly string _volumeKey;
    private readonly string _physicalDiskKey;
    private readonly Func<long> _timestampProvider;
    private readonly long _startedTimestamp;
    private int _bytesWritten;
    private int _failed;
    private int _disposed;

    public HostUploadWriteLease(
        HostUploadStorageAdmissionController controller,
        string uploadId,
        string deviceId,
        string volumeKey,
        string physicalDiskKey,
        Func<long> timestampProvider)
    {
        _controller = controller;
        _uploadId = uploadId;
        _deviceId = deviceId;
        _volumeKey = volumeKey;
        _physicalDiskKey = physicalDiskKey;
        _timestampProvider = timestampProvider;
        _startedTimestamp = timestampProvider();
    }

    public void Commit(int bytesWritten)
    {
        if (bytesWritten <= 0)
            return;
        _controller.CommitChunk(_uploadId, bytesWritten);
        Interlocked.Add(ref _bytesWritten, bytesWritten);
    }

    public void Fail() => Interlocked.Exchange(ref _failed, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        long elapsedTicks = Math.Max(
            0,
            _timestampProvider() - _startedTimestamp);
        int bytesWritten = Volatile.Read(ref _bytesWritten);
        if (Volatile.Read(ref _failed) != 0)
            _controller.RecordWriteFailure(_physicalDiskKey);
        _controller.ReleaseWrite(
            _uploadId,
            _deviceId,
            _volumeKey,
            _physicalDiskKey,
            bytesWritten,
            TimeSpan.FromSeconds(
                elapsedTicks / (double)System.Diagnostics.Stopwatch.Frequency));
    }
}
