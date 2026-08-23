using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal sealed class ExtensionRuntime : IDisposable
{
    private readonly ExtensionAuthorizationStore _authorizations;
    private readonly ExtensionResultInboxStore _inbox;
    private readonly ExtensionOrderSourceStore _orderSources;
    private readonly ExtensionResultInboxProcessor _processor;
    private int _processing;
    private int _disposed;
    private int _storesDisposed;

    internal ExtensionRuntime(
        VideoDatabase database,
        string databasePath,
        string localNodeId,
        ExtensionAuthorizationStore authorizations,
        Action<string, IReadOnlyList<RecordingExtensionField>> fieldsChanged,
        Action<OrderInfo> orderChanged)
    {
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        Broker = new ExtensionScanTaskBroker();
        _inbox = new ExtensionResultInboxStore(databasePath);
        _orderSources = new ExtensionOrderSourceStore(databasePath);
        Coordinator = new ExtensionScanResultSubmissionCoordinator(
            Broker,
            new ExtensionScanResultValidator(),
            _inbox);
        _processor = new ExtensionResultInboxProcessor(
            _inbox,
            new ExtensionMeasurementResultApplier(database, localNodeId, fieldsChanged),
            new ExtensionOrderResultApplier(database, _orderSources, localNodeId),
            (_, merged) =>
            {
                if (merged.Order != null) orderChanged?.Invoke(merged.Order);
            });
    }

    internal ExtensionScanTaskBroker Broker { get; }
    internal ExtensionScanResultSubmissionCoordinator Coordinator { get; }

    internal ExtensionScanPublishResult Publish(
        string originNodeId,
        string recordingSessionId,
        string trackingNumber,
        string recordingMode)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return new ExtensionScanPublishResult([], []);
        ExtensionScanTarget[] targets = _authorizations.GetAll(includeRevoked: false)
            .Where(value => value.HasPermission(ExtensionPermissions.ScanTasksRead)
                && value.HasPermission(ExtensionPermissions.ScanResultsWrite)
                && value.IsBoundToOriginNode(originNodeId))
            .Select(value => new ExtensionScanTarget
            {
                ExtensionInstanceId = value.ExtensionInstanceId,
                Capabilities = value.Capabilities
            })
            .Where(value => value.Capabilities.Count > 0)
            .ToArray();
        if (targets.Length == 0)
            return new ExtensionScanPublishResult([], []);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Broker.Publish(new ExtensionScanEvent
        {
            TaskId = Guid.NewGuid().ToString("N"),
            OriginNodeId = originNodeId,
            RecordingSessionId = recordingSessionId,
            TrackingNumber = trackingNumber,
            RecordingMode = recordingMode,
            OccurredAtUtc = now,
            SoftDeadlineUtc = now.AddSeconds(5),
            ExpiresAtUtc = now.AddSeconds(30),
            RequestedCapabilities = ExtensionScanCapabilities.Supported.ToArray()
        }, targets);
    }

    internal void ProcessAvailableResults()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    ExtensionResultProcessingOutcome outcome = _processor.ProcessNext();
                    if (outcome.Disposition == ExtensionResultProcessingDisposition.Empty)
                        break;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("ExtensionResult", "Result Inbox processing failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _processing, 0);
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!SpinWait.SpinUntil(() => Volatile.Read(ref _processing) == 0, TimeSpan.FromSeconds(5)))
        {
            _ = Task.Run(() =>
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref _processing) == 0);
                DisposeStores();
            });
            return;
        }
        DisposeStores();
    }

    private void DisposeStores()
    {
        if (Interlocked.Exchange(ref _storesDisposed, 1) != 0) return;
        _orderSources.Dispose();
        _inbox.Dispose();
    }
}
