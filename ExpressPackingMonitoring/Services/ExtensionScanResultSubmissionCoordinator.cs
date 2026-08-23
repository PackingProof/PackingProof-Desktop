using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal enum ExtensionScanResultSubmissionDisposition
{
    Accepted,
    Duplicate,
    BusinessDuplicate,
    StaleRevision,
    RevisionConflict,
    ResultIdConflict,
    DeliveryIdentityConflict,
    Expired,
    DeliveryNotFound
}

internal sealed record ExtensionScanResultSubmissionOutcome(
    ExtensionScanResultSubmissionDisposition Disposition,
    long? InboxId = null);

/// <summary>
/// Validates an extension response, persists it and only then synchronizes the in-memory task state.
/// The durable inbox remains authoritative if the process stops between those two operations.
/// </summary>
internal sealed class ExtensionScanResultSubmissionCoordinator
{
    private readonly ExtensionScanTaskBroker _broker;
    private readonly ExtensionScanResultValidator _validator;
    private readonly ExtensionResultInboxStore _inbox;

    internal ExtensionScanResultSubmissionCoordinator(
        ExtensionScanTaskBroker broker,
        ExtensionScanResultValidator validator,
        ExtensionResultInboxStore inbox)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
    }

    internal ExtensionScanResultSubmissionOutcome Submit(
        ExtensionAuthorizationContext authorization,
        ExtensionScanResultRequest request)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);

        ExtensionScanDelivery? delivery = _broker.GetDelivery(request.DeliveryId);
        if (delivery == null)
        {
            return new ExtensionScanResultSubmissionOutcome(
                ExtensionScanResultSubmissionDisposition.DeliveryNotFound);
        }

        ExtensionValidatedScanResult validated = _validator.Validate(
            authorization,
            delivery,
            request);
        ExtensionResultInboxAcceptResult accepted = _inbox.Accept(validated.InboxSubmission);
        ExtensionScanResultSubmissionDisposition disposition = Map(accepted.Disposition);
        if (accepted.Disposition is ExtensionResultInboxDisposition.Accepted
            or ExtensionResultInboxDisposition.Duplicate
            or ExtensionResultInboxDisposition.BusinessDuplicate)
        {
            SynchronizeBroker(validated, accepted);
        }

        return new ExtensionScanResultSubmissionOutcome(disposition, accepted.InboxId);
    }

    private void SynchronizeBroker(
        ExtensionValidatedScanResult validated,
        ExtensionResultInboxAcceptResult accepted)
    {
        var submission = new ExtensionScanSubmission
        {
            ExtensionInstanceId = validated.InboxSubmission.ExtensionInstanceId,
            DeliveryId = validated.InboxSubmission.DeliveryId,
            TaskId = validated.InboxSubmission.TaskId,
            Revision = validated.InboxSubmission.Revision,
            Status = validated.InboxSubmission.Status,
            PayloadFingerprint = accepted.PayloadFingerprint,
            RetryAfterUtc = validated.RetryAfterUtc
        };
        ExtensionScanSubmissionResult synchronized = _broker.ApplyDurablyAccepted(submission);
        if (synchronized.Disposition is not (
            ExtensionScanSubmissionDisposition.Accepted
            or ExtensionScanSubmissionDisposition.Duplicate))
        {
            throw new InvalidOperationException(
                $"持久化扩展结果无法同步到任务状态：{synchronized.Disposition}");
        }
    }

    private static ExtensionScanResultSubmissionDisposition Map(
        ExtensionResultInboxDisposition disposition) => disposition switch
    {
        ExtensionResultInboxDisposition.Accepted => ExtensionScanResultSubmissionDisposition.Accepted,
        ExtensionResultInboxDisposition.Duplicate => ExtensionScanResultSubmissionDisposition.Duplicate,
        ExtensionResultInboxDisposition.BusinessDuplicate => ExtensionScanResultSubmissionDisposition.BusinessDuplicate,
        ExtensionResultInboxDisposition.StaleRevision => ExtensionScanResultSubmissionDisposition.StaleRevision,
        ExtensionResultInboxDisposition.RevisionConflict => ExtensionScanResultSubmissionDisposition.RevisionConflict,
        ExtensionResultInboxDisposition.ResultIdConflict => ExtensionScanResultSubmissionDisposition.ResultIdConflict,
        ExtensionResultInboxDisposition.DeliveryIdentityConflict => ExtensionScanResultSubmissionDisposition.DeliveryIdentityConflict,
        ExtensionResultInboxDisposition.Expired => ExtensionScanResultSubmissionDisposition.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
    };
}
