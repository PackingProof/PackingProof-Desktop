using System.Globalization;
using System.IO;
using System.Text.Json;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class ExtensionMeasurementResultApplier
{
    private readonly VideoDatabase _database;
    private readonly string _localOriginNodeId;
    private readonly Action<string, IReadOnlyList<RecordingExtensionField>>? _fieldsChanged;

    internal ExtensionMeasurementResultApplier(
        VideoDatabase database,
        string localOriginNodeId,
        Action<string, IReadOnlyList<RecordingExtensionField>>? fieldsChanged = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _localOriginNodeId = localOriginNodeId?.Trim() ?? "";
        if (_localOriginNodeId.Length == 0)
            throw new ArgumentException("本机录像节点 ID 不能为空", nameof(localOriginNodeId));
        _fieldsChanged = fieldsChanged;
    }

    internal int Apply(ExtensionResultInboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Capability != ExtensionScanCapabilities.MeasurementCapture)
            throw new InvalidOperationException("当前应用器只处理测量结果");
        if (!string.Equals(item.OriginNodeId, _localOriginNodeId, StringComparison.Ordinal))
            throw new InvalidDataException("测量结果不属于本机录像节点");

        ExtensionNormalizedResultPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ExtensionNormalizedResultPayload>(
                item.PayloadJson,
                JsonOptions) ?? throw new InvalidDataException("测量结果内容为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("测量结果内容无法解析", ex);
        }
        if (payload.SchemaVersion != 1 || payload.Orders.Count > 0)
            throw new InvalidDataException("测量结果结构版本或内容无效");

        VideoRecord record = _database.GetRecordingBySession(item.RecordingSessionId)
            ?? throw new InvalidDataException("测量结果关联的录像会话不存在");
        string recordTracking = string.IsNullOrWhiteSpace(record.TrackingNumber)
            ? record.OrderId?.Trim().ToUpperInvariant() ?? ""
            : record.TrackingNumber.Trim().ToUpperInvariant();
        if (!string.Equals(recordTracking, item.TrackingNumber, StringComparison.Ordinal))
            throw new InvalidDataException("测量结果关联的录像单号不一致");

        Dictionary<string, string> fields = payload.Measurements
            .Where(measurement => measurement != null && measurement.Stable)
            .Select(ValidateStableMeasurement)
            .ToDictionary(
                measurement => measurement.MeasurementType,
                measurement => $"{measurement.Value} {measurement.Unit}",
                StringComparer.Ordinal);
        if (fields.Count == 0)
            return 0;

        int updated = _database.UpsertRecordingExtensionFields(
            item.RecordingSessionId,
            item.ProviderId,
            item.ProviderId,
            item.DeliveryId,
            item.Revision,
            fields,
            item.ObservedAtUtc.UtcDateTime);
        _fieldsChanged?.Invoke(
            item.RecordingSessionId,
            _database.GetRecordingExtensionFields(item.RecordingSessionId));
        return updated;
    }

    private static ExtensionNormalizedMeasurement ValidateStableMeasurement(
        ExtensionNormalizedMeasurement measurement)
    {
        string type = measurement.MeasurementType?.Trim().ToLowerInvariant() ?? "";
        string unit = measurement.Unit?.Trim().ToLowerInvariant() ?? "";
        bool known = type switch
        {
            "weight" => unit is "g" or "kg",
            "length" or "width" or "height" => unit is "mm" or "cm" or "m",
            _ => false
        };
        if (!known
            || !decimal.TryParse(
                measurement.Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal value)
            || value <= 0
            || value > 1_000_000m)
        {
            throw new InvalidDataException("持久化测量值未通过应用前复核");
        }
        return new ExtensionNormalizedMeasurement
        {
            MeasurementType = type,
            Value = value.ToString("0.############################", CultureInfo.InvariantCulture),
            Unit = unit,
            Stable = true,
            CapturedAtUtc = measurement.CapturedAtUtc
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
