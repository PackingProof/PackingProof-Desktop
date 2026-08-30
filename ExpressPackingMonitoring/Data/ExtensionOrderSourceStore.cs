using System.IO;
using System.Text.Json;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Data;

internal sealed record ExtensionOrderSourceUpdate
{
    internal long InboxId { get; init; }
    internal string ExtensionInstanceId { get; init; } = "";
    internal string ProviderId { get; init; } = "";
    internal string ResultId { get; init; } = "";
    internal string DeliveryId { get; init; } = "";
    internal string OriginNodeId { get; init; } = "";
    internal string TrackingNumber { get; init; } = "";
    internal string Capability { get; init; } = "";
    internal ExtensionScanResultStatus Status { get; init; }
    internal DateTimeOffset ObservedAtUtc { get; init; }
    internal IReadOnlyList<ExtensionNormalizedOrder> Orders { get; init; } = [];
}

internal sealed record ExtensionOrderMergeResult(
    OrderInfo? Order,
    int RespondedProviderCount,
    int NotFoundProviderCount,
    bool HasTransientFailure,
    bool SourceStateChanged);

/// <summary>
/// Stores each provider's latest conclusion separately. Transient failures keep the provider's last
/// confirmed orders, while an explicit newer not_found clears only that provider's confirmation.
/// </summary>
internal sealed class ExtensionOrderSourceStore : IDisposable
{
    private const int MaxMergedTextLength = 2000;
    private const int MaxMergedProductLength = 4000;
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    internal ExtensionOrderSourceStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("扩展订单来源数据库路径不能为空", nameof(databasePath));
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connection = new SqliteConnection($"Data Source={fullPath}");
        _connection.Open();
        Initialize();
    }

    internal ExtensionOrderMergeResult Apply(ExtensionOrderSourceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        NormalizedUpdate normalized = Normalize(update);
        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            ProviderState? existing = ReadState(normalized, transaction);
            bool changed = existing == null || CompareRank(normalized, existing) > 0;
            if (changed)
                Upsert(normalized, existing, transaction);
            transaction.Commit();
            return MergeCore(normalized.OriginNodeId, normalized.TrackingNumber, changed);
        }
    }

    internal ExtensionOrderMergeResult GetMerged(string originNodeId, string trackingNumber)
    {
        string origin = NormalizeIdentifier(originNodeId, "来源节点 ID");
        string tracking = NormalizeTrackingNumber(trackingNumber);
        lock (_gate)
            return MergeCore(origin, tracking, sourceStateChanged: false);
    }

    private void Initialize()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ExtensionOrderProviderStates (
                OriginNodeId TEXT NOT NULL,
                TrackingNumber TEXT NOT NULL,
                Capability TEXT NOT NULL,
                ExtensionInstanceId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                LatestInboxId INTEGER NOT NULL,
                LatestResultId TEXT NOT NULL,
                LatestDeliveryId TEXT NOT NULL,
                LatestStatus TEXT NOT NULL,
                LatestObservedAtUtc TEXT NOT NULL,
                ConfirmedOrdersJson TEXT NOT NULL DEFAULT '',
                ConfirmedObservedAtUtc TEXT,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (
                    OriginNodeId, TrackingNumber, Capability,
                    ExtensionInstanceId, ProviderId)
            );
            CREATE INDEX IF NOT EXISTS IX_ExtensionOrderProviderStates_Tracking
                ON ExtensionOrderProviderStates(OriginNodeId, TrackingNumber);";
        command.ExecuteNonQuery();
    }

    private ProviderState? ReadState(NormalizedUpdate update, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT LatestInboxId, LatestResultId, LatestDeliveryId, LatestStatus,
                   LatestObservedAtUtc, ConfirmedOrdersJson, ConfirmedObservedAtUtc
            FROM ExtensionOrderProviderStates
            WHERE OriginNodeId = @originNodeId
              AND TrackingNumber = @trackingNumber
              AND Capability = @capability
              AND ExtensionInstanceId = @extensionInstanceId
              AND ProviderId = @providerId;";
        AddIdentityParameters(command, update);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ProviderState(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseStatus(reader.GetString(3)),
            Parse(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)));
    }

    private void Upsert(
        NormalizedUpdate update,
        ProviderState? existing,
        SqliteTransaction transaction)
    {
        string confirmedJson = existing?.ConfirmedOrdersJson ?? "";
        DateTimeOffset? confirmedAt = existing?.ConfirmedObservedAtUtc;
        if (update.Status == ExtensionScanResultStatus.Found)
        {
            confirmedJson = update.OrdersJson;
            confirmedAt = update.ObservedAtUtc;
        }
        else if (update.Status == ExtensionScanResultStatus.NotFound)
        {
            confirmedJson = "";
            confirmedAt = null;
        }

        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO ExtensionOrderProviderStates (
                OriginNodeId, TrackingNumber, Capability, ExtensionInstanceId, ProviderId,
                LatestInboxId, LatestResultId, LatestDeliveryId, LatestStatus,
                LatestObservedAtUtc, ConfirmedOrdersJson, ConfirmedObservedAtUtc, UpdatedAtUtc)
            VALUES (
                @originNodeId, @trackingNumber, @capability, @extensionInstanceId, @providerId,
                @inboxId, @resultId, @deliveryId, @status,
                @observedAt, @confirmedOrders, @confirmedAt, @updatedAt)
            ON CONFLICT(OriginNodeId, TrackingNumber, Capability, ExtensionInstanceId, ProviderId)
            DO UPDATE SET
                LatestInboxId = excluded.LatestInboxId,
                LatestResultId = excluded.LatestResultId,
                LatestDeliveryId = excluded.LatestDeliveryId,
                LatestStatus = excluded.LatestStatus,
                LatestObservedAtUtc = excluded.LatestObservedAtUtc,
                ConfirmedOrdersJson = excluded.ConfirmedOrdersJson,
                ConfirmedObservedAtUtc = excluded.ConfirmedObservedAtUtc,
                UpdatedAtUtc = excluded.UpdatedAtUtc;";
        AddIdentityParameters(command, update);
        command.Parameters.AddWithValue("@inboxId", update.InboxId);
        command.Parameters.AddWithValue("@resultId", update.ResultId);
        command.Parameters.AddWithValue("@deliveryId", update.DeliveryId);
        command.Parameters.AddWithValue("@status", update.Status.ToString());
        command.Parameters.AddWithValue("@observedAt", Format(update.ObservedAtUtc));
        command.Parameters.AddWithValue("@confirmedOrders", confirmedJson);
        command.Parameters.AddWithValue("@confirmedAt", confirmedAt is null ? DBNull.Value : Format(confirmedAt.Value));
        command.Parameters.AddWithValue("@updatedAt", Format(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private ExtensionOrderMergeResult MergeCore(
        string originNodeId,
        string trackingNumber,
        bool sourceStateChanged)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT ExtensionInstanceId, ProviderId, Capability, LatestStatus,
                   LatestObservedAtUtc, ConfirmedOrdersJson, ConfirmedObservedAtUtc
            FROM ExtensionOrderProviderStates
            WHERE OriginNodeId = @originNodeId AND TrackingNumber = @trackingNumber;";
        command.Parameters.AddWithValue("@originNodeId", originNodeId);
        command.Parameters.AddWithValue("@trackingNumber", trackingNumber);
        using SqliteDataReader reader = command.ExecuteReader();
        var states = new List<MergeState>();
        while (reader.Read())
        {
            states.Add(new MergeState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseStatus(reader.GetString(3)),
                Parse(reader.GetString(4)),
                DeserializeOrders(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6))));
        }

        int responded = states.Count(state => IsFinal(state.LatestStatus));
        int notFound = states.Count(state => state.LatestStatus == ExtensionScanResultStatus.NotFound);
        bool transient = states.Any(state => !IsFinal(state.LatestStatus));
        OrderCandidate[] orderCandidates = states
            .Where(state => state.Capability == ExtensionScanCapabilities.OrderLookup
                && state.ConfirmedObservedAtUtc != null)
            .SelectMany(state => state.ConfirmedOrders.Select(order => new OrderCandidate(state, order)))
            .GroupBy(candidate => candidate.Order.OrderId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.State.ConfirmedObservedAtUtc)
                .ThenBy(candidate => candidate.State.ProviderId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.State.ExtensionInstanceId, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => candidate.Order.OrderId, StringComparer.Ordinal)
            .ToArray();
        RefundCandidate? refund = states
            .Where(state => state.ConfirmedObservedAtUtc != null)
            .SelectMany(state => state.ConfirmedOrders.Select(order => new RefundCandidate(state, order)))
            .Where(candidate => candidate.Order.RefundState is not ("none" or "unknown"))
            .OrderByDescending(candidate => RefundPriority(candidate.Order.RefundState))
            .ThenByDescending(candidate => candidate.State.ConfirmedObservedAtUtc)
            .ThenBy(candidate => candidate.State.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (orderCandidates.Length == 0 && refund == null)
            return new ExtensionOrderMergeResult(null, responded, notFound, transient, sourceStateChanged);

        DateTimeOffset observedAt = orderCandidates
            .Select(candidate => candidate.State.ConfirmedObservedAtUtc!.Value)
            .Append(refund?.State.ConfirmedObservedAtUtc ?? DateTimeOffset.MinValue)
            .Max();
        string providerId = orderCandidates.FirstOrDefault()?.State.ProviderId
            ?? refund!.State.ProviderId;
        string buyerMessage = JoinDistinct(
            orderCandidates.Select(candidate => candidate.Order.BuyerMessage),
            MaxMergedTextLength);
        string sellerMemo = JoinDistinct(
            orderCandidates.Select(candidate => candidate.Order.SellerMemo),
            MaxMergedTextLength);
        string productInfo = JoinDistinct(
            orderCandidates.SelectMany(candidate => candidate.Order.Products)
                .Select(product => $"{product.Name} ×{product.Quantity}"),
            MaxMergedProductLength);
        int totalItemCount = (int)Math.Min(
            ExtensionScanResultValidator.MaxTotalItemCount,
            orderCandidates.Sum(candidate => (long)candidate.Order.TotalItemCount));
        string refundState = refund?.Order.RefundState ?? "none";
        var orderInfo = new OrderInfo
        {
            TrackingNumber = trackingNumber,
            OrderId = orderCandidates.FirstOrDefault()?.Order.OrderId ?? "",
            BuyerMessage = buyerMessage,
            SellerMemo = sellerMemo,
            ProductInfo = productInfo,
            TotalItemCount = totalItemCount,
            MergedOrderCount = Math.Min(WebServer.MaxOrderInfoItems, orderCandidates.Length),
            ProviderId = providerId,
            HasRefund = refundState is not ("none" or "unknown"),
            IsPrintedRefund = refundState is "requested" or "processing" or "refunded" or "returned",
            RefundStatus = refundState,
            RefundProductInfo = refund?.Order.RefundReason ?? "",
            PushTime = observedAt.LocalDateTime
        };
        return new ExtensionOrderMergeResult(orderInfo, responded, notFound, transient, sourceStateChanged);
    }

    private static NormalizedUpdate Normalize(ExtensionOrderSourceUpdate update)
    {
        if (update.InboxId <= 0) throw new InvalidDataException("扩展结果 Inbox ID 无效");
        string capability = update.Capability?.Trim().ToLowerInvariant() ?? "";
        if (capability is not (ExtensionScanCapabilities.OrderLookup or ExtensionScanCapabilities.RefundLookup))
            throw new InvalidDataException("订单来源能力无效");
        if (update.Status == ExtensionScanResultStatus.Found && update.Orders.Count == 0)
            throw new InvalidDataException("found 订单来源必须包含订单");
        if (update.Status != ExtensionScanResultStatus.Found && update.Orders.Count > 0)
            throw new InvalidDataException("当前订单来源状态不能包含订单");
        if (update.ObservedAtUtc == default)
            throw new InvalidDataException("订单来源观察时间无效");
        string ordersJson = update.Orders.Count == 0
            ? ""
            : JsonSerializer.Serialize(update.Orders, JsonOptions);
        return new NormalizedUpdate(
            update.InboxId,
            NormalizeIdentifier(update.ExtensionInstanceId, "扩展实例 ID"),
            NormalizeProviderId(update.ProviderId),
            NormalizeIdentifier(update.ResultId, "结果 ID"),
            NormalizeIdentifier(update.DeliveryId, "投递 ID"),
            NormalizeIdentifier(update.OriginNodeId, "来源节点 ID"),
            NormalizeTrackingNumber(update.TrackingNumber),
            capability,
            update.Status,
            update.ObservedAtUtc,
            ordersJson);
    }

    private static int CompareRank(NormalizedUpdate update, ProviderState existing)
    {
        int observed = update.ObservedAtUtc.CompareTo(existing.LatestObservedAtUtc);
        if (observed != 0) return observed;
        int result = string.Compare(update.ResultId, existing.LatestResultId, StringComparison.Ordinal);
        if (result != 0) return result;
        return update.InboxId.CompareTo(existing.LatestInboxId);
    }

    private static void AddIdentityParameters(SqliteCommand command, NormalizedUpdate update)
    {
        command.Parameters.AddWithValue("@originNodeId", update.OriginNodeId);
        command.Parameters.AddWithValue("@trackingNumber", update.TrackingNumber);
        command.Parameters.AddWithValue("@capability", update.Capability);
        command.Parameters.AddWithValue("@extensionInstanceId", update.ExtensionInstanceId);
        command.Parameters.AddWithValue("@providerId", update.ProviderId);
    }

    private static IReadOnlyList<ExtensionNormalizedOrder> DeserializeOrders(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<ExtensionNormalizedOrder>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("扩展订单来源快照无法解析", ex);
        }
    }

    private static string JoinDistinct(IEnumerable<string> values, int maxLength)
    {
        string joined = string.Join(Environment.NewLine, values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal));
        return joined.Length <= maxLength ? joined : joined[..maxLength];
    }

    private static int RefundPriority(string state) => state switch
    {
        "refunded" => 5,
        "returned" => 4,
        "processing" => 3,
        "requested" => 2,
        "rejected" => 1,
        _ => 0
    };

    private static bool IsFinal(ExtensionScanResultStatus status) => status is not (
        ExtensionScanResultStatus.InProgress
        or ExtensionScanResultStatus.Unavailable
        or ExtensionScanResultStatus.ProviderAuthRequired
        or ExtensionScanResultStatus.RateLimited
        or ExtensionScanResultStatus.Timeout);

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length is < 3 or > 128
            || normalized.Any(character => !(IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':')))
        {
            throw new InvalidDataException($"{fieldName}格式无效");
        }
        return normalized;
    }

    private static string NormalizeProviderId(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Length is < 3 or > 128
            || normalized.Any(character => !(IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new InvalidDataException("来源标识格式无效");
        }
        return normalized;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9';

    private static string NormalizeTrackingNumber(string? value)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? "";
        if (normalized.Length is < 1 or > 128 || normalized.Any(char.IsControl))
            throw new InvalidDataException("快递单号格式无效");
        return normalized;
    }

    private static ExtensionScanResultStatus ParseStatus(string value) =>
        Enum.TryParse(value, out ExtensionScanResultStatus status)
            ? status
            : throw new InvalidDataException("扩展订单来源状态无法识别");

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static DateTimeOffset Parse(string value) =>
        new(DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());

    public void Dispose() => _connection.Dispose();

    private sealed record NormalizedUpdate(
        long InboxId,
        string ExtensionInstanceId,
        string ProviderId,
        string ResultId,
        string DeliveryId,
        string OriginNodeId,
        string TrackingNumber,
        string Capability,
        ExtensionScanResultStatus Status,
        DateTimeOffset ObservedAtUtc,
        string OrdersJson);

    private sealed record ProviderState(
        long LatestInboxId,
        string LatestResultId,
        string LatestDeliveryId,
        ExtensionScanResultStatus LatestStatus,
        DateTimeOffset LatestObservedAtUtc,
        string ConfirmedOrdersJson,
        DateTimeOffset? ConfirmedObservedAtUtc);

    private sealed record MergeState(
        string ExtensionInstanceId,
        string ProviderId,
        string Capability,
        ExtensionScanResultStatus LatestStatus,
        DateTimeOffset LatestObservedAtUtc,
        IReadOnlyList<ExtensionNormalizedOrder> ConfirmedOrders,
        DateTimeOffset? ConfirmedObservedAtUtc);

    private sealed record OrderCandidate(MergeState State, ExtensionNormalizedOrder Order);

    private sealed record RefundCandidate(MergeState State, ExtensionNormalizedOrder Order);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
