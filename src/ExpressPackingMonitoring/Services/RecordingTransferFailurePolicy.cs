using System.Net;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

/// <summary>保存主机按协议拒绝了这次上传；错误码和 HTTP 状态共同决定是否继续重试。</summary>
internal sealed class RecordingTransferHostException : Exception
{
    public RecordingTransferHostException(
        string errorCode,
        string message,
        HttpStatusCode? statusCode = null,
        bool retryWholeFile = false,
        int maxFileAttempts = 0)
        : base(message)
    {
        ErrorCode = errorCode ?? "";
        StatusCode = statusCode;
        RetryWholeFile = retryWholeFile;
        MaxFileAttempts = maxFileAttempts;
    }

    public string ErrorCode { get; }
    public HttpStatusCode? StatusCode { get; }
    public bool RetryWholeFile { get; }
    public int MaxFileAttempts { get; }
}

internal static class RecordingTransferFailurePolicy
{
    /// <summary>返回非空表示必须停止自动重试（内容为原因），空串表示继续退避重试。</summary>
    internal static string ClassifyFailure(Exception exception)
    {
        if (exception is RecordingTransferHostException host)
        {
            if (string.Equals(host.ErrorCode, "session_conflict", StringComparison.OrdinalIgnoreCase))
            {
                return "保存主机上已存在同编号的其它录像，已停止自动重试；请先在保存主机删除那条旧记录，再手动重试";
            }
            if (IsRetryableHostErrorCode(host.ErrorCode)
                || (!IsDeterministicHostErrorCode(host.ErrorCode)
                    && IsRetryableStatusCode(host.StatusCode)))
            {
                return "";
            }

            return NonEmptyFailureReason(host.Message);
        }

        if (exception is HttpRequestException http)
        {
            return IsRetryableStatusCode(http.StatusCode)
                ? ""
                : NonEmptyFailureReason(http.Message);
        }

        if (exception is FileNotFoundException)
            return "本地缓存录像不存在，已停止自动重试；请检查录像文件或手动重试";

        if (exception is InvalidOperationException
            or InvalidDataException
            or JsonException
            or UnauthorizedAccessException)
        {
            return NonEmptyFailureReason(exception.Message);
        }

        return "";
    }

    internal static (string ErrorCode, string Message, bool RetryWholeFile, int MaxFileAttempts) TryReadError(
        string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string errorCode = document.RootElement.TryGetProperty("errorCode", out JsonElement code)
                ? code.GetString() ?? ""
                : "";
            string message = document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString() ?? ""
                : "";
            bool retryWholeFile = document.RootElement.TryGetProperty(
                    "retryWholeFile",
                    out JsonElement retry)
                && retry.ValueKind is JsonValueKind.True or JsonValueKind.False
                && retry.GetBoolean();
            int maxFileAttempts = document.RootElement.TryGetProperty(
                    "maxFileAttempts",
                    out JsonElement maxAttempts)
                && maxAttempts.TryGetInt32(out int parsedMaxAttempts)
                    ? Math.Max(0, parsedMaxAttempts)
                    : 0;
            return (errorCode, message, retryWholeFile, maxFileAttempts);
        }
        catch
        {
            return ("", "", false, 0);
        }
    }

    private static bool IsRetryableHostErrorCode(string errorCode) =>
        errorCode is "server_busy"
            or "request_rate_limited"
            or "storage_unavailable"
            or "offset_mismatch"
            or "upload_not_found";

    private static bool IsDeterministicHostErrorCode(string errorCode) =>
        errorCode is "session_conflict"
            or "backup_client_upgrade_required"
            or "device_identity_required"
            or "signed_auth_required"
            or "enrollment_denied"
            or "chunk_too_large"
            or "invalid_json"
            or "invalid_request"
            or "sha256_mismatch"
            or "verified_record_invalid";

    private static bool IsRetryableStatusCode(HttpStatusCode? statusCode) =>
        statusCode is null
        || statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
        || (statusCode is HttpStatusCode status && (int)status == 425);

    private static string NonEmptyFailureReason(string message) =>
        string.IsNullOrWhiteSpace(message) ? "上传失败" : message.Trim();
}
