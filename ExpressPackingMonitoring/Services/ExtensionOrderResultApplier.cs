using System.IO;
using System.Text.Json;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class ExtensionOrderResultApplier
{
    private readonly VideoDatabase _database;
    private readonly ExtensionOrderSourceStore _sourceStore;
    private readonly string _localOriginNodeId;

    internal ExtensionOrderResultApplier(
        VideoDatabase database,
        ExtensionOrderSourceStore sourceStore,
        string localOriginNodeId)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _sourceStore = sourceStore ?? throw new ArgumentNullException(nameof(sourceStore));
        _localOriginNodeId = localOriginNodeId?.Trim() ?? "";
        if (_localOriginNodeId.Length == 0)
            throw new ArgumentException("本机录像节点 ID 不能为空", nameof(localOriginNodeId));
    }

    internal ExtensionOrderMergeResult Apply(ExtensionResultInboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Capability is not (
            ExtensionScanCapabilities.OrderLookup
            or ExtensionScanCapabilities.RefundLookup))
        {
            throw new InvalidOperationException("当前应用器只处理订单和退款结果");
        }
        if (!string.Equals(item.OriginNodeId, _localOriginNodeId, StringComparison.Ordinal))
            throw new InvalidDataException("订单结果不属于本机录像节点");

        VideoRecord record = _database.GetRecordingBySession(item.RecordingSessionId)
            ?? throw new InvalidDataException("订单结果关联的录像会话不存在");
        string recordTracking = string.IsNullOrWhiteSpace(record.TrackingNumber)
            ? record.OrderId?.Trim().ToUpperInvariant() ?? ""
            : record.TrackingNumber.Trim().ToUpperInvariant();
        if (!string.Equals(recordTracking, item.TrackingNumber, StringComparison.Ordinal))
            throw new InvalidDataException("订单结果关联的录像单号不一致");

        ExtensionNormalizedResultPayload payload = DeserializePayload(item.PayloadJson);
        ValidatePayload(item, payload);
        return _sourceStore.Apply(new ExtensionOrderSourceUpdate
        {
            InboxId = item.Id,
            ExtensionInstanceId = item.ExtensionInstanceId,
            ProviderId = item.ProviderId,
            ResultId = item.ResultId,
            DeliveryId = item.DeliveryId,
            OriginNodeId = item.OriginNodeId,
            TrackingNumber = item.TrackingNumber,
            Capability = item.Capability,
            Status = item.Status,
            ObservedAtUtc = item.ObservedAtUtc,
            Orders = payload.Orders ?? []
        });
    }

    private static ExtensionNormalizedResultPayload DeserializePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtensionNormalizedResultPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("订单结果内容为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("订单结果内容无法解析", ex);
        }
    }

    private static void ValidatePayload(
        ExtensionResultInboxItem item,
        ExtensionNormalizedResultPayload payload)
    {
        IReadOnlyList<ExtensionNormalizedOrder> orders = payload.Orders ?? [];
        IReadOnlyList<ExtensionNormalizedMeasurement> measurements = payload.Measurements ?? [];
        if (payload.SchemaVersion != 1 || measurements.Count > 0)
            throw new InvalidDataException("订单结果结构版本或内容无效");
        if (orders.Count > ExtensionScanResultValidator.MaxOrders)
            throw new InvalidDataException("订单结果数量超过限制");
        if (item.Status == ExtensionScanResultStatus.Found && orders.Count == 0)
            throw new InvalidDataException("found 订单结果缺少订单数据");
        if (item.Status != ExtensionScanResultStatus.Found && orders.Count > 0)
            throw new InvalidDataException("当前订单结果状态不能包含订单数据");

        foreach (ExtensionNormalizedOrder order in orders)
        {
            if (order == null)
                throw new InvalidDataException("订单结果包含空项");
            if (!string.Equals(
                order.TrackingNumber?.Trim().ToUpperInvariant(),
                item.TrackingNumber,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException("持久化订单结果的快递单号不一致");
            }
            if (string.IsNullOrWhiteSpace(order.OrderId)
                || order.OrderId.Length > 128
                || order.OrderId.Any(char.IsControl))
                throw new InvalidDataException("持久化订单号无效");
            if ((order.BuyerMessage?.Length ?? 0) > 2000
                || (order.SellerMemo?.Length ?? 0) > 2000
                || (order.RefundReason?.Length ?? 0) > 1000
                || order.TotalItemCount is < 0 or > ExtensionScanResultValidator.MaxTotalItemCount)
            {
                throw new InvalidDataException("持久化订单字段超过限制");
            }
            if (order.RefundState is not (
                "none" or "requested" or "processing" or "refunded"
                or "returned" or "rejected" or "unknown"))
            {
                throw new InvalidDataException("持久化退款状态无效");
            }
            ValidateProducts(order.Products ?? [], order.TotalItemCount);
        }
    }

    private static void ValidateProducts(
        IReadOnlyList<ExtensionNormalizedProduct> products,
        int totalItemCount)
    {
        if (products.Count > ExtensionScanResultValidator.MaxProductsPerOrder)
            throw new InvalidDataException("持久化商品数量超过限制");
        int total = 0;
        foreach (ExtensionNormalizedProduct product in products)
        {
            if (product == null
                || string.IsNullOrWhiteSpace(product.Name)
                || product.Name.Length > 500
                || (product.Sku?.Length ?? 0) > 128
                || (product.MerchantSku?.Length ?? 0) > 128
                || product.Quantity is < 1 or > ExtensionScanResultValidator.MaxTotalItemCount)
            {
                throw new InvalidDataException("持久化商品字段无效");
            }
            total = checked(total + product.Quantity);
            if (total > ExtensionScanResultValidator.MaxTotalItemCount)
                throw new InvalidDataException("持久化商品总件数超过限制");
        }
        if (products.Count > 0 && total != totalItemCount)
            throw new InvalidDataException("持久化商品数量与订单总件数不一致");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
