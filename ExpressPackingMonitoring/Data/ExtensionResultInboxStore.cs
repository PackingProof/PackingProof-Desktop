using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Data;

internal static class ExtensionResultInboxStates
{
    internal const string Pending = "Pending";
    internal const string Applying = "Applying";
    internal const string Applied = "Applied";
    internal const string Failed = "Failed";
}

internal enum ExtensionResultInboxDisposition
{
    Accepted,
    Duplicate,
    BusinessDuplicate,
    StaleRevision,
    RevisionConflict,
    ResultIdConflict,
    DeliveryIdentityConflict
}

internal sealed record ExtensionResultSubmission
{
    internal string ExtensionInstanceId { get; init; } = "";
    internal string ProviderId { get; init; } = "";
    internal string ResultId { get; init; } = "";
    internal string DeliveryId { get; init; } = "";
    internal string TaskId { get; init; } = "";
    internal string OriginNodeId { get; init; } = "";
    internal string RecordingSessionId { get; init; } = "";
    internal string TrackingNumber { get; init; } = "";
    internal string Capability { get; init; } = "";
    internal long Revision { get; init; }
    internal ExtensionScanResultStatus Status { get; init; }
    internal DateTimeOffset ObservedAtUtc { get; init; }
    internal string NormalizedPayloadJson { get; init; } = "{}";
}

internal sealed record ExtensionResultInboxAcceptResult(
    ExtensionResultInboxDisposition Disposition,
    long? InboxId = null);

internal sealed record ExtensionResultInboxItem
{
    internal long Id { get; init; }
    internal string ExtensionInstanceId { get; init; } = "";
    internal string ProviderId { get; init; } = "";
    internal string ResultId { get; init; } = "";
    internal string DeliveryId { get; init; } = "";
    internal string TaskId { get; init; } = "";
    internal string OriginNodeId { get; init; } = "";
    internal string RecordingSessionId { get; init; } = "";
    internal string TrackingNumber { get; init; } = "";
    internal string Capability { get; init; } = "";
    internal long Revision { get; init; }
    internal ExtensionScanResultStatus Status { get; init; }
    internal DateTimeOffset ObservedAtUtc { get; init; }
    internal string PayloadJson { get; init; } = "{}";
    internal string PayloadFingerprint { get; init; } = "";
    internal string State { get; init; } = "";
    internal int AttemptCount { get; init; }
    internal DateTimeOffset? NextAttemptAtUtc { get; init; }
    internal string LastError { get; init; } = "";
    internal DateTimeOffset CreatedAtUtc { get; init; }
    internal DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// Durable inbox for already validated extension results. Transport revision and business result
/// idempotency are committed in the same SQLite transaction as the normalized payload.
/// </summary>
internal sealed class ExtensionResultInboxStore : IDisposable
{
    internal const int MaxPayloadBytes = 256 * 1024;
    internal const int MaxPayloadDepth = 16;
    internal const int MaxLastErrorLength = 2000;

    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProviderPattern = new(
        "^[a-z0-9][a-z0-9._-]{2,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _timeProvider;

    internal ExtensionResultInboxStore(string databasePath, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connection = new SqliteConnection($"Data Source={fullPath}");
        _connection.Open();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
    }

    internal ExtensionResultInboxAcceptResult Accept(ExtensionResultSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        NormalizedSubmission normalized = NormalizeSubmission(submission, _timeProvider.GetUtcNow());

        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            DeliveryRevision? latest = ReadDeliveryRevision(normalized.DeliveryId, transaction);
            if (latest != null)
            {
                if (!string.Equals(latest.ExtensionInstanceId, normalized.ExtensionInstanceId, StringComparison.Ordinal)
                    || !string.Equals(latest.TaskId, normalized.TaskId, StringComparison.Ordinal)
                    || !string.Equals(latest.OriginNodeId, normalized.OriginNodeId, StringComparison.Ordinal)
                    || !string.Equals(latest.RecordingSessionId, normalized.RecordingSessionId, StringComparison.Ordinal)
                    || !string.Equals(latest.TrackingNumber, normalized.TrackingNumber, StringComparison.Ordinal)
                    || !string.Equals(latest.Capability, normalized.Capability, StringComparison.Ordinal))
                {
                    transaction.Rollback();
                    return new(ExtensionResultInboxDisposition.DeliveryIdentityConflict);
                }
                if (normalized.Revision == latest.LatestRevision)
                {
                    bool duplicate = string.Equals(
                            normalized.PayloadFingerprint,
                            latest.LatestPayloadFingerprint,
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(normalized.ResultId, latest.LatestResultId, StringComparison.Ordinal);
                    transaction.Rollback();
                    return new(duplicate
                        ? ExtensionResultInboxDisposition.Duplicate
                        : ExtensionResultInboxDisposition.RevisionConflict,
                        latest.LatestInboxId);
                }
                if (normalized.Revision < latest.LatestRevision)
                {
                    transaction.Rollback();
                    return new(ExtensionResultInboxDisposition.StaleRevision, latest.LatestInboxId);
                }
            }

            BusinessResult? existingResult = ReadBusinessResult(
                normalized.ExtensionInstanceId,
                normalized.ProviderId,
                normalized.ResultId,
                transaction);
            if (existingResult != null
                && !string.Equals(
                    existingResult.PayloadFingerprint,
                    normalized.PayloadFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                transaction.Rollback();
                return new(ExtensionResultInboxDisposition.ResultIdConflict, existingResult.InboxId);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            long? inboxId = existingResult?.InboxId;
            if (existingResult == null)
                inboxId = InsertInbox(normalized, now, transaction);
            UpsertDeliveryRevision(normalized, inboxId, now, transaction);
            transaction.Commit();
            return new(
                existingResult == null
                    ? ExtensionResultInboxDisposition.Accepted
                    : ExtensionResultInboxDisposition.BusinessDuplicate,
                inboxId);
        }
    }

    internal int RecoverInterrupted()
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = @"
                UPDATE ExtensionResultInbox
                SET State = @pending,
                    NextAttemptAtUtc = NULL,
                    UpdatedAtUtc = @now
                WHERE State = @applying;";
            command.Parameters.AddWithValue("@pending", ExtensionResultInboxStates.Pending);
            command.Parameters.AddWithValue("@applying", ExtensionResultInboxStates.Applying);
            command.Parameters.AddWithValue("@now", Format(_timeProvider.GetUtcNow()));
            return command.ExecuteNonQuery();
        }
    }

    internal ExtensionResultInboxItem? ClaimNext()
    {
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            long? id;
            using (SqliteCommand select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
                    SELECT Id
                    FROM ExtensionResultInbox
                    WHERE State = @pending
                       OR (State = @failed AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now))
                    ORDER BY CreatedAtUtc, Id
                    LIMIT 1;";
                select.Parameters.AddWithValue("@pending", ExtensionResultInboxStates.Pending);
                select.Parameters.AddWithValue("@failed", ExtensionResultInboxStates.Failed);
                select.Parameters.AddWithValue("@now", Format(now));
                object? value = select.ExecuteScalar();
                id = value == null || value == DBNull.Value ? null : Convert.ToInt64(value);
            }
            if (id == null)
            {
                transaction.Rollback();
                return null;
            }

            using (SqliteCommand update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
                    UPDATE ExtensionResultInbox
                    SET State = @applying,
                        AttemptCount = AttemptCount + 1,
                        NextAttemptAtUtc = NULL,
                        UpdatedAtUtc = @now
                    WHERE Id = @id
                      AND (State = @pending
                           OR (State = @failed AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now)));";
                update.Parameters.AddWithValue("@applying", ExtensionResultInboxStates.Applying);
                update.Parameters.AddWithValue("@pending", ExtensionResultInboxStates.Pending);
                update.Parameters.AddWithValue("@failed", ExtensionResultInboxStates.Failed);
                update.Parameters.AddWithValue("@now", Format(now));
                update.Parameters.AddWithValue("@id", id.Value);
                if (update.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return null;
                }
            }
            ExtensionResultInboxItem item = ReadInboxItem(id.Value, transaction)
                ?? throw new InvalidOperationException("已领取的扩展结果不存在");
            transaction.Commit();
            return item;
        }
    }

    internal bool MarkApplied(long inboxId)
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = @"
                UPDATE ExtensionResultInbox
                SET State = @applied,
                    LastError = '',
                    NextAttemptAtUtc = NULL,
                    UpdatedAtUtc = @now
                WHERE Id = @id AND State = @applying;";
            command.Parameters.AddWithValue("@applied", ExtensionResultInboxStates.Applied);
            command.Parameters.AddWithValue("@applying", ExtensionResultInboxStates.Applying);
            command.Parameters.AddWithValue("@now", Format(_timeProvider.GetUtcNow()));
            command.Parameters.AddWithValue("@id", inboxId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    internal bool MarkFailed(long inboxId, string error, DateTimeOffset? retryAtUtc)
    {
        string normalizedError = (error ?? "").Trim();
        if (normalizedError.Length > MaxLastErrorLength)
            normalizedError = normalizedError[..MaxLastErrorLength];
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (retryAtUtc is { } retry && retry <= now)
            throw new InvalidDataException("重试时间必须晚于当前时间");

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = @"
                UPDATE ExtensionResultInbox
                SET State = @failed,
                    LastError = @error,
                    NextAttemptAtUtc = @retryAt,
                    UpdatedAtUtc = @now
                WHERE Id = @id AND State = @applying;";
            command.Parameters.AddWithValue("@failed", ExtensionResultInboxStates.Failed);
            command.Parameters.AddWithValue("@applying", ExtensionResultInboxStates.Applying);
            command.Parameters.AddWithValue("@error", normalizedError);
            command.Parameters.AddWithValue("@retryAt", retryAtUtc is null ? DBNull.Value : Format(retryAtUtc.Value));
            command.Parameters.AddWithValue("@now", Format(now));
            command.Parameters.AddWithValue("@id", inboxId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    internal ExtensionResultInboxItem? Get(long inboxId)
    {
        lock (_gate)
            return ReadInboxItem(inboxId, transaction: null);
    }

    private void Initialize()
    {
        Execute("PRAGMA busy_timeout=10000;");
        Execute(@"
            CREATE TABLE IF NOT EXISTS ExtensionResultInbox (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ExtensionInstanceId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ResultId TEXT NOT NULL,
                DeliveryId TEXT NOT NULL,
                TaskId TEXT NOT NULL,
                OriginNodeId TEXT NOT NULL,
                RecordingSessionId TEXT NOT NULL,
                TrackingNumber TEXT NOT NULL,
                Capability TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                Status TEXT NOT NULL,
                ObservedAtUtc TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                PayloadFingerprint TEXT NOT NULL,
                State TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL DEFAULT 0,
                NextAttemptAtUtc TEXT,
                LastError TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE (ExtensionInstanceId, ProviderId, ResultId),
                UNIQUE (DeliveryId, Revision)
            );");
        Execute(@"
            CREATE TABLE IF NOT EXISTS ExtensionDeliveryRevisions (
                DeliveryId TEXT PRIMARY KEY,
                ExtensionInstanceId TEXT NOT NULL,
                TaskId TEXT NOT NULL,
                OriginNodeId TEXT NOT NULL,
                RecordingSessionId TEXT NOT NULL,
                TrackingNumber TEXT NOT NULL,
                Capability TEXT NOT NULL,
                LatestRevision INTEGER NOT NULL,
                LatestPayloadFingerprint TEXT NOT NULL,
                LatestResultId TEXT NOT NULL,
                LatestInboxId INTEGER,
                UpdatedAtUtc TEXT NOT NULL
            );");
        EnsureColumn("ExtensionResultInbox", "OriginNodeId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("ExtensionResultInbox", "RecordingSessionId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("ExtensionResultInbox", "TrackingNumber", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("ExtensionDeliveryRevisions", "OriginNodeId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("ExtensionDeliveryRevisions", "RecordingSessionId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("ExtensionDeliveryRevisions", "TrackingNumber", "TEXT NOT NULL DEFAULT ''");
        Execute("CREATE INDEX IF NOT EXISTS IX_ExtensionResultInbox_State_NextAttempt ON ExtensionResultInbox(State, NextAttemptAtUtc, CreatedAtUtc);");
    }

    private long InsertInbox(
        NormalizedSubmission submission,
        DateTimeOffset now,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO ExtensionResultInbox (
                ExtensionInstanceId, ProviderId, ResultId, DeliveryId, TaskId,
                OriginNodeId, RecordingSessionId, TrackingNumber,
                Capability, Revision, Status, ObservedAtUtc, PayloadJson,
                PayloadFingerprint, State, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                @extensionId, @providerId, @resultId, @deliveryId, @taskId,
                @originNodeId, @recordingSessionId, @trackingNumber,
                @capability, @revision, @status, @observedAt, @payload,
                @fingerprint, @state, @now, @now);
            SELECT last_insert_rowid();";
        AddSubmissionParameters(command, submission);
        command.Parameters.AddWithValue("@state", ExtensionResultInboxStates.Pending);
        command.Parameters.AddWithValue("@now", Format(now));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void UpsertDeliveryRevision(
        NormalizedSubmission submission,
        long? inboxId,
        DateTimeOffset now,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO ExtensionDeliveryRevisions (
                DeliveryId, ExtensionInstanceId, TaskId, OriginNodeId,
                RecordingSessionId, TrackingNumber, Capability, LatestRevision,
                LatestPayloadFingerprint, LatestResultId, LatestInboxId, UpdatedAtUtc)
            VALUES (
                @deliveryId, @extensionId, @taskId, @originNodeId,
                @recordingSessionId, @trackingNumber, @capability, @revision,
                @fingerprint, @resultId, @inboxId, @now)
            ON CONFLICT(DeliveryId) DO UPDATE SET
                LatestRevision = excluded.LatestRevision,
                LatestPayloadFingerprint = excluded.LatestPayloadFingerprint,
                LatestResultId = excluded.LatestResultId,
                LatestInboxId = excluded.LatestInboxId,
                UpdatedAtUtc = excluded.UpdatedAtUtc;";
        AddSubmissionParameters(command, submission);
        command.Parameters.AddWithValue("@inboxId", inboxId is null ? DBNull.Value : inboxId.Value);
        command.Parameters.AddWithValue("@now", Format(now));
        command.ExecuteNonQuery();
    }

    private static void AddSubmissionParameters(SqliteCommand command, NormalizedSubmission submission)
    {
        command.Parameters.AddWithValue("@extensionId", submission.ExtensionInstanceId);
        command.Parameters.AddWithValue("@providerId", submission.ProviderId);
        command.Parameters.AddWithValue("@resultId", submission.ResultId);
        command.Parameters.AddWithValue("@deliveryId", submission.DeliveryId);
        command.Parameters.AddWithValue("@taskId", submission.TaskId);
        command.Parameters.AddWithValue("@originNodeId", submission.OriginNodeId);
        command.Parameters.AddWithValue("@recordingSessionId", submission.RecordingSessionId);
        command.Parameters.AddWithValue("@trackingNumber", submission.TrackingNumber);
        command.Parameters.AddWithValue("@capability", submission.Capability);
        command.Parameters.AddWithValue("@revision", submission.Revision);
        command.Parameters.AddWithValue("@status", submission.Status.ToString());
        command.Parameters.AddWithValue("@observedAt", Format(submission.ObservedAtUtc));
        command.Parameters.AddWithValue("@payload", submission.PayloadJson);
        command.Parameters.AddWithValue("@fingerprint", submission.PayloadFingerprint);
    }

    private DeliveryRevision? ReadDeliveryRevision(string deliveryId, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT ExtensionInstanceId, TaskId, OriginNodeId, RecordingSessionId,
                   TrackingNumber, Capability, LatestRevision,
                   LatestPayloadFingerprint, LatestResultId, LatestInboxId
            FROM ExtensionDeliveryRevisions
            WHERE DeliveryId = @deliveryId;";
        command.Parameters.AddWithValue("@deliveryId", deliveryId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new DeliveryRevision(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }

    private BusinessResult? ReadBusinessResult(
        string extensionInstanceId,
        string providerId,
        string resultId,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT Id, PayloadFingerprint
            FROM ExtensionResultInbox
            WHERE ExtensionInstanceId = @extensionId
              AND ProviderId = @providerId
              AND ResultId = @resultId;";
        command.Parameters.AddWithValue("@extensionId", extensionInstanceId);
        command.Parameters.AddWithValue("@providerId", providerId);
        command.Parameters.AddWithValue("@resultId", resultId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new BusinessResult(reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private ExtensionResultInboxItem? ReadInboxItem(long id, SqliteTransaction? transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT Id, ExtensionInstanceId, ProviderId, ResultId, DeliveryId, TaskId,
                   OriginNodeId, RecordingSessionId, TrackingNumber,
                   Capability, Revision, Status, ObservedAtUtc, PayloadJson,
                   PayloadFingerprint, State, AttemptCount, NextAttemptAtUtc,
                   LastError, CreatedAtUtc, UpdatedAtUtc
            FROM ExtensionResultInbox
            WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (!Enum.TryParse(reader.GetString(11), out ExtensionScanResultStatus status))
            throw new InvalidDataException("扩展结果状态无法识别");
        return new ExtensionResultInboxItem
        {
            Id = reader.GetInt64(0),
            ExtensionInstanceId = reader.GetString(1),
            ProviderId = reader.GetString(2),
            ResultId = reader.GetString(3),
            DeliveryId = reader.GetString(4),
            TaskId = reader.GetString(5),
            OriginNodeId = reader.GetString(6),
            RecordingSessionId = reader.GetString(7),
            TrackingNumber = reader.GetString(8),
            Capability = reader.GetString(9),
            Revision = reader.GetInt64(10),
            Status = status,
            ObservedAtUtc = Parse(reader.GetString(12)),
            PayloadJson = reader.GetString(13),
            PayloadFingerprint = reader.GetString(14),
            State = reader.GetString(15),
            AttemptCount = reader.GetInt32(16),
            NextAttemptAtUtc = reader.IsDBNull(17) ? null : Parse(reader.GetString(17)),
            LastError = reader.GetString(18),
            CreatedAtUtc = Parse(reader.GetString(19)),
            UpdatedAtUtc = Parse(reader.GetString(20))
        };
    }

    private static NormalizedSubmission NormalizeSubmission(
        ExtensionResultSubmission submission,
        DateTimeOffset now)
    {
        string extensionId = NormalizeIdentifier(submission.ExtensionInstanceId, "扩展实例 ID");
        string providerId = submission.ProviderId?.Trim().ToLowerInvariant() ?? "";
        if (!ProviderPattern.IsMatch(providerId))
            throw new InvalidDataException("来源标识格式无效");
        string resultId = NormalizeIdentifier(submission.ResultId, "结果 ID");
        string deliveryId = NormalizeIdentifier(submission.DeliveryId, "投递 ID");
        string taskId = NormalizeIdentifier(submission.TaskId, "任务 ID");
        string originNodeId = NormalizeIdentifier(submission.OriginNodeId, "来源节点 ID");
        string recordingSessionId = NormalizeIdentifier(submission.RecordingSessionId, "录像会话 ID");
        string trackingNumber = submission.TrackingNumber?.Trim().ToUpperInvariant() ?? "";
        if (trackingNumber.Length is < 1 or > 128 || trackingNumber.Any(char.IsControl))
            throw new InvalidDataException("快递单号格式无效");
        string capability = submission.Capability?.Trim().ToLowerInvariant() ?? "";
        if (!ExtensionScanCapabilities.Supported.Contains(capability))
            throw new InvalidDataException("结果能力不受支持");
        if (submission.Revision <= 0)
            throw new InvalidDataException("结果修订号必须大于 0");
        if (submission.ObservedAtUtc == default
            || submission.ObservedAtUtc > now + ExtensionRequestSignature.AllowedClockSkew)
        {
            throw new InvalidDataException("结果观察时间无效");
        }

        string payload = CanonicalizeJson(submission.NormalizedPayloadJson);
        string fingerprintSource = $"{submission.Status}\n{payload}";
        string fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new NormalizedSubmission(
            extensionId,
            providerId,
            resultId,
            deliveryId,
            taskId,
            originNodeId,
            recordingSessionId,
            trackingNumber,
            capability,
            submission.Revision,
            submission.Status,
            submission.ObservedAtUtc,
            payload,
            fingerprint);
    }

    private static string CanonicalizeJson(string? json)
    {
        string value = json?.Trim() ?? "";
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > MaxPayloadBytes)
            throw new InvalidDataException("扩展结果内容为空或超过大小限制");
        try
        {
            using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxPayloadDepth
            });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteCanonical(document.RootElement, writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("扩展结果 JSON 无效", ex);
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                JsonProperty[] properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                if (properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new InvalidDataException("扩展结果 JSON 包含重复字段");
                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (!IdentifierPattern.IsMatch(normalized))
            throw new InvalidDataException($"{fieldName}格式无效");
        return normalized;
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void EnsureColumn(string tableName, string columnName, string definition)
    {
        using SqliteCommand check = _connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''")}');";
        using SqliteDataReader reader = check.ExecuteReader();
        bool exists = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
        reader.Close();
        if (exists)
            return;
        Execute($"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};");
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static DateTimeOffset Parse(string value) =>
        new(DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());

    public void Dispose() => _connection.Dispose();

    private sealed record NormalizedSubmission(
        string ExtensionInstanceId,
        string ProviderId,
        string ResultId,
        string DeliveryId,
        string TaskId,
        string OriginNodeId,
        string RecordingSessionId,
        string TrackingNumber,
        string Capability,
        long Revision,
        ExtensionScanResultStatus Status,
        DateTimeOffset ObservedAtUtc,
        string PayloadJson,
        string PayloadFingerprint);

    private sealed record DeliveryRevision(
        string ExtensionInstanceId,
        string TaskId,
        string OriginNodeId,
        string RecordingSessionId,
        string TrackingNumber,
        string Capability,
        long LatestRevision,
        string LatestPayloadFingerprint,
        string LatestResultId,
        long? LatestInboxId);

    private sealed record BusinessResult(long InboxId, string PayloadFingerprint);
}
