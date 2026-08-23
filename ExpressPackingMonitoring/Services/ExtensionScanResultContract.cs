using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class ExtensionScanResultRequest
{
    [JsonPropertyName("deliveryId")]
    public string DeliveryId { get; set; } = "";

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = "";

    [JsonPropertyName("resultId")]
    public string ResultId { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("observedAt")]
    public DateTimeOffset ObservedAtUtc { get; set; }

    [JsonPropertyName("retryAfter")]
    public DateTimeOffset? RetryAfterUtc { get; set; }

    [JsonPropertyName("orders")]
    public List<ExtensionOrderResult> Orders { get; set; } = [];

    [JsonPropertyName("measurements")]
    public List<ExtensionMeasurementResult> Measurements { get; set; } = [];
}

internal sealed class ExtensionOrderResult
{
    [JsonPropertyName("trackingNumber")]
    public string TrackingNumber { get; set; } = "";

    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = "";

    [JsonPropertyName("buyerMessage")]
    public string BuyerMessage { get; set; } = "";

    [JsonPropertyName("sellerMemo")]
    public string SellerMemo { get; set; } = "";

    [JsonPropertyName("totalItemCount")]
    public int TotalItemCount { get; set; }

    [JsonPropertyName("products")]
    public List<ExtensionProductResult> Products { get; set; } = [];

    [JsonPropertyName("refundState")]
    public string RefundState { get; set; } = "unknown";

    [JsonPropertyName("refundReason")]
    public string RefundReason { get; set; } = "";
}

internal sealed class ExtensionProductResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sku")]
    public string Sku { get; set; } = "";

    [JsonPropertyName("merchantSku")]
    public string MerchantSku { get; set; } = "";

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

internal sealed class ExtensionMeasurementResult
{
    [JsonPropertyName("measurementType")]
    public string MeasurementType { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAtUtc { get; set; }
}

internal sealed record ExtensionValidatedScanResult(
    ExtensionResultSubmission InboxSubmission,
    DateTimeOffset? RetryAfterUtc);

/// <summary>
/// Converts untrusted extension JSON DTOs into bounded semantic data. No third-party value is
/// interpreted as drawing, TTS, SQL, shell, file path, or FFmpeg instruction.
/// </summary>
internal sealed class ExtensionScanResultValidator
{
    internal const int MaxOrders = 50;
    internal const int MaxProductsPerOrder = 100;
    internal const int MaxMeasurements = 8;
    internal const int MaxTotalItemCount = 100000;

    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, ExtensionScanResultStatus> Statuses =
        new Dictionary<string, ExtensionScanResultStatus>(StringComparer.Ordinal)
        {
            ["in_progress"] = ExtensionScanResultStatus.InProgress,
            ["found"] = ExtensionScanResultStatus.Found,
            ["not_found"] = ExtensionScanResultStatus.NotFound,
            ["completed"] = ExtensionScanResultStatus.Completed,
            ["unavailable"] = ExtensionScanResultStatus.Unavailable,
            ["provider_auth_required"] = ExtensionScanResultStatus.ProviderAuthRequired,
            ["rate_limited"] = ExtensionScanResultStatus.RateLimited,
            ["timeout"] = ExtensionScanResultStatus.Timeout,
            ["invalid_request"] = ExtensionScanResultStatus.InvalidRequest
        };
    private static readonly IReadOnlySet<string> RefundStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "requested", "processing", "refunded", "returned", "rejected", "unknown"
    };
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> MeasurementUnits =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["weight"] = new HashSet<string>(["g", "kg"], StringComparer.Ordinal),
            ["length"] = new HashSet<string>(["mm", "cm", "m"], StringComparer.Ordinal),
            ["width"] = new HashSet<string>(["mm", "cm", "m"], StringComparer.Ordinal),
            ["height"] = new HashSet<string>(["mm", "cm", "m"], StringComparer.Ordinal)
        };

    private readonly TimeProvider _timeProvider;

    internal ExtensionScanResultValidator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal ExtensionValidatedScanResult Validate(
        ExtensionAuthorizationContext authorization,
        ExtensionScanDelivery delivery,
        ExtensionScanResultRequest request)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(request);
        if (authorization.RevokedAtUtc != null
            || !authorization.HasPermission(ExtensionPermissions.ScanResultsWrite))
        {
            throw new UnauthorizedAccessException("扩展没有扫码结果写入权限");
        }
        if (!string.Equals(
                authorization.ExtensionInstanceId,
                delivery.ExtensionInstanceId,
                StringComparison.Ordinal)
            || !authorization.SupportsCapability(delivery.Capability)
            || !authorization.IsBoundToOriginNode(delivery.ScanEvent.OriginNodeId))
        {
            throw new UnauthorizedAccessException("扩展无权响应此投递");
        }

        string providerId = request.ProviderId?.Trim().ToLowerInvariant() ?? "";
        if (!string.Equals(providerId, authorization.ProviderId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("响应来源与扩展授权不一致");
        string deliveryId = NormalizeIdentifier(request.DeliveryId, "投递 ID");
        string taskId = NormalizeIdentifier(request.TaskId, "任务 ID");
        string resultId = NormalizeIdentifier(request.ResultId, "结果 ID");
        if (!string.Equals(deliveryId, delivery.DeliveryId, StringComparison.Ordinal)
            || !string.Equals(taskId, delivery.ScanEvent.TaskId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("响应投递或任务不匹配");
        }
        if (request.Revision <= 0)
            throw new InvalidDataException("响应修订号必须大于 0");
        string statusName = request.Status?.Trim().ToLowerInvariant() ?? "";
        if (!Statuses.TryGetValue(statusName, out ExtensionScanResultStatus status))
            throw new InvalidDataException("响应状态不受支持");
        ValidateStatusForCapability(delivery.Capability, status);
        ValidateRetryAfter(request.RetryAfterUtc, status, delivery.ScanEvent.ExpiresAtUtc);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (request.ObservedAtUtc == default
            || request.ObservedAtUtc < delivery.ScanEvent.OccurredAtUtc - ExtensionRequestSignature.AllowedClockSkew
            || request.ObservedAtUtc > now + ExtensionRequestSignature.AllowedClockSkew)
        {
            throw new InvalidDataException("结果观察时间无效");
        }

        IReadOnlyList<ExtensionNormalizedOrder> orders = ValidateOrders(
            request.Orders,
            delivery,
            status);
        IReadOnlyList<ExtensionNormalizedMeasurement> measurements = ValidateMeasurements(
            request.Measurements,
            authorization,
            delivery,
            status,
            now);
        var payload = new ExtensionNormalizedResultPayload
        {
            SchemaVersion = 1,
            Orders = orders,
            Measurements = measurements
        };
        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        return new ExtensionValidatedScanResult(
            new ExtensionResultSubmission
            {
                ExtensionInstanceId = authorization.ExtensionInstanceId,
                ProviderId = authorization.ProviderId,
                ResultId = resultId,
                DeliveryId = delivery.DeliveryId,
                TaskId = delivery.ScanEvent.TaskId,
                OriginNodeId = delivery.ScanEvent.OriginNodeId,
                RecordingSessionId = delivery.ScanEvent.RecordingSessionId,
                TrackingNumber = delivery.ScanEvent.TrackingNumber,
                Capability = delivery.Capability,
                Revision = request.Revision,
                Status = status,
                ObservedAtUtc = request.ObservedAtUtc,
                NormalizedPayloadJson = payloadJson
            },
            request.RetryAfterUtc);
    }

    private static IReadOnlyList<ExtensionNormalizedOrder> ValidateOrders(
        IReadOnlyList<ExtensionOrderResult>? orders,
        ExtensionScanDelivery delivery,
        ExtensionScanResultStatus status)
    {
        ExtensionOrderResult[] values = (orders ?? []).ToArray();
        bool orderCapability = delivery.Capability is ExtensionScanCapabilities.OrderLookup
            or ExtensionScanCapabilities.RefundLookup;
        if (!orderCapability && values.Length > 0)
            throw new InvalidDataException("测量投递不能提交订单数据");
        if (values.Length > MaxOrders)
            throw new InvalidDataException($"单次最多提交 {MaxOrders} 条订单");
        if (status == ExtensionScanResultStatus.Found && orderCapability && values.Length == 0)
            throw new InvalidDataException("found 响应必须包含订单数据");
        if (status != ExtensionScanResultStatus.Found && values.Length > 0)
            throw new InvalidDataException("只有 found 响应可以包含订单数据");

        var normalized = new List<ExtensionNormalizedOrder>(values.Length);
        foreach (ExtensionOrderResult value in values)
        {
            if (value == null)
                throw new InvalidDataException("订单结果包含空项");
            string trackingNumber = NormalizeTrackingNumber(value.TrackingNumber);
            if (!string.Equals(trackingNumber, delivery.ScanEvent.TrackingNumber, StringComparison.Ordinal))
                throw new InvalidDataException("订单快递单号与扫码任务不一致");
            string orderId = NormalizeText(value.OrderId, 128, "订单号", required: true);
            string buyerMessage = NormalizeText(value.BuyerMessage, 2000, "买家留言");
            string sellerMemo = NormalizeText(value.SellerMemo, 2000, "卖家备注");
            string refundState = value.RefundState?.Trim().ToLowerInvariant() ?? "unknown";
            if (!RefundStates.Contains(refundState))
                throw new InvalidDataException("退款状态不受支持");
            string refundReason = NormalizeText(value.RefundReason, 1000, "退款说明");
            ExtensionProductResult[] products = (value.Products ?? []).ToArray();
            if (products.Length > MaxProductsPerOrder)
                throw new InvalidDataException($"每条订单最多包含 {MaxProductsPerOrder} 个商品项");

            var normalizedProducts = new List<ExtensionNormalizedProduct>(products.Length);
            int productQuantity = 0;
            foreach (ExtensionProductResult product in products)
            {
                if (product == null)
                    throw new InvalidDataException("商品结果包含空项");
                if (product.Quantity is < 1 or > MaxTotalItemCount)
                    throw new InvalidDataException("商品数量超出允许范围");
                productQuantity = checked(productQuantity + product.Quantity);
                if (productQuantity > MaxTotalItemCount)
                    throw new InvalidDataException("商品总件数超出允许范围");
                normalizedProducts.Add(new ExtensionNormalizedProduct
                {
                    Name = NormalizeText(product.Name, 500, "商品名称", required: true),
                    Sku = NormalizeText(product.Sku, 128, "SKU"),
                    MerchantSku = NormalizeText(product.MerchantSku, 128, "商家 SKU"),
                    Quantity = product.Quantity
                });
            }
            if (value.TotalItemCount is < 0 or > MaxTotalItemCount)
                throw new InvalidDataException("商品总件数超出允许范围");
            int totalItemCount = products.Length > 0 ? productQuantity : value.TotalItemCount;
            if (products.Length > 0
                && value.TotalItemCount > 0
                && value.TotalItemCount != productQuantity)
            {
                throw new InvalidDataException("商品总件数与商品明细数量不一致");
            }
            normalized.Add(new ExtensionNormalizedOrder
            {
                TrackingNumber = trackingNumber,
                OrderId = orderId,
                BuyerMessage = buyerMessage,
                SellerMemo = sellerMemo,
                TotalItemCount = totalItemCount,
                Products = normalizedProducts,
                RefundState = refundState,
                RefundReason = refundReason
            });
        }
        return normalized;
    }

    private static IReadOnlyList<ExtensionNormalizedMeasurement> ValidateMeasurements(
        IReadOnlyList<ExtensionMeasurementResult>? measurements,
        ExtensionAuthorizationContext authorization,
        ExtensionScanDelivery delivery,
        ExtensionScanResultStatus status,
        DateTimeOffset now)
    {
        ExtensionMeasurementResult[] values = (measurements ?? []).ToArray();
        if (delivery.Capability != ExtensionScanCapabilities.MeasurementCapture && values.Length > 0)
            throw new InvalidDataException("订单投递不能提交测量数据");
        if (values.Length > MaxMeasurements)
            throw new InvalidDataException($"单次最多提交 {MaxMeasurements} 个测量值");
        if (delivery.Capability == ExtensionScanCapabilities.MeasurementCapture
            && !authorization.HasPermission(ExtensionPermissions.RecordingFieldsWrite))
        {
            throw new UnauthorizedAccessException("扩展没有录像字段写入权限");
        }
        if (status == ExtensionScanResultStatus.Completed
            && delivery.Capability == ExtensionScanCapabilities.MeasurementCapture
            && !values.Any(value => value?.Stable == true))
        {
            throw new InvalidDataException("completed 测量响应必须包含稳定读数");
        }
        if (status is not (ExtensionScanResultStatus.InProgress or ExtensionScanResultStatus.Completed)
            && values.Length > 0)
        {
            throw new InvalidDataException("当前响应状态不能包含测量数据");
        }

        var normalized = new List<ExtensionNormalizedMeasurement>(values.Length);
        var usedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExtensionMeasurementResult value in values)
        {
            if (value == null)
                throw new InvalidDataException("测量结果包含空项");
            string type = value.MeasurementType?.Trim().ToLowerInvariant() ?? "";
            if (!MeasurementUnits.TryGetValue(type, out IReadOnlySet<string>? units))
                throw new InvalidDataException("测量类型不受支持");
            if (!usedTypes.Add(type))
                throw new InvalidDataException("同一响应不能重复提交相同测量类型");
            string unit = value.Unit?.Trim().ToLowerInvariant() ?? "";
            if (!units.Contains(unit))
                throw new InvalidDataException("测量单位与类型不匹配");
            if (!decimal.TryParse(
                    value.Value?.Trim(),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal number)
                || number < 0
                || number > 1_000_000m
                || (value.Stable && number == 0))
            {
                throw new InvalidDataException("测量数值格式或范围无效");
            }
            if (value.CapturedAtUtc == default
                || value.CapturedAtUtc < delivery.ScanEvent.OccurredAtUtc - ExtensionRequestSignature.AllowedClockSkew
                || value.CapturedAtUtc > now + ExtensionRequestSignature.AllowedClockSkew)
            {
                throw new InvalidDataException("测量采集时间无效");
            }
            normalized.Add(new ExtensionNormalizedMeasurement
            {
                MeasurementType = type,
                Value = number.ToString("0.############################", CultureInfo.InvariantCulture),
                Unit = unit,
                Stable = value.Stable,
                CapturedAtUtc = value.CapturedAtUtc
            });
        }
        return normalized;
    }

    private static void ValidateStatusForCapability(string capability, ExtensionScanResultStatus status)
    {
        if (status is ExtensionScanResultStatus.InProgress
            or ExtensionScanResultStatus.Unavailable
            or ExtensionScanResultStatus.ProviderAuthRequired
            or ExtensionScanResultStatus.RateLimited
            or ExtensionScanResultStatus.Timeout
            or ExtensionScanResultStatus.InvalidRequest)
        {
            return;
        }
        bool valid = capability switch
        {
            ExtensionScanCapabilities.OrderLookup or ExtensionScanCapabilities.RefundLookup =>
                status is ExtensionScanResultStatus.Found or ExtensionScanResultStatus.NotFound,
            ExtensionScanCapabilities.MeasurementCapture =>
                status == ExtensionScanResultStatus.Completed,
            _ => false
        };
        if (!valid)
            throw new InvalidDataException("响应状态与投递能力不匹配");
    }

    private void ValidateRetryAfter(
        DateTimeOffset? retryAfterUtc,
        ExtensionScanResultStatus status,
        DateTimeOffset expiresAtUtc)
    {
        if (retryAfterUtc == null)
            return;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (retryAfterUtc <= now || retryAfterUtc > expiresAtUtc)
            throw new InvalidDataException("重试时间必须晚于当前时间且不能超过任务有效期");
        if (status is not (ExtensionScanResultStatus.InProgress
            or ExtensionScanResultStatus.Unavailable
            or ExtensionScanResultStatus.RateLimited))
        {
            throw new InvalidDataException("最终响应不能设置重试时间");
        }
    }

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (!IdentifierPattern.IsMatch(normalized))
            throw new InvalidDataException($"{fieldName}格式无效");
        return normalized;
    }

    private static string NormalizeTrackingNumber(string? value)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? "";
        if (normalized.Length is < 1 or > 128 || normalized.Any(char.IsControl))
            throw new InvalidDataException("快递单号格式无效");
        return normalized;
    }

    private static string NormalizeText(
        string? value,
        int maxLength,
        string fieldName,
        bool required = false)
    {
        string normalized = value?.Trim() ?? "";
        if ((required && normalized.Length == 0)
            || normalized.Length > maxLength
            || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"{fieldName}格式无效");
        }
        return normalized;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

internal sealed class ExtensionNormalizedResultPayload
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<ExtensionNormalizedOrder> Orders { get; init; } = [];
    public IReadOnlyList<ExtensionNormalizedMeasurement> Measurements { get; init; } = [];
}

internal sealed class ExtensionNormalizedOrder
{
    public string TrackingNumber { get; init; } = "";
    public string OrderId { get; init; } = "";
    public string BuyerMessage { get; init; } = "";
    public string SellerMemo { get; init; } = "";
    public int TotalItemCount { get; init; }
    public IReadOnlyList<ExtensionNormalizedProduct> Products { get; init; } = [];
    public string RefundState { get; init; } = "unknown";
    public string RefundReason { get; init; } = "";
}

internal sealed class ExtensionNormalizedProduct
{
    public string Name { get; init; } = "";
    public string Sku { get; init; } = "";
    public string MerchantSku { get; init; } = "";
    public int Quantity { get; init; }
}

internal sealed class ExtensionNormalizedMeasurement
{
    public string MeasurementType { get; init; } = "";
    public string Value { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool Stable { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
}
