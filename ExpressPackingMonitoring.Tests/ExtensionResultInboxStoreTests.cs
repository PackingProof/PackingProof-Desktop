using System.IO;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionResultInboxStoreTests
{
    [Fact]
    public void Accept_CommitsCanonicalPayloadAndTransportDuplicateAtomically()
    {
        using var fixture = new InboxFixture();
        ExtensionResultSubmission first = fixture.Submission() with
        {
            NormalizedPayloadJson = "{\"weight\":1.25,\"stable\":true}"
        };

        ExtensionResultInboxAcceptResult accepted = fixture.Store.Accept(first);
        ExtensionResultInboxAcceptResult duplicate = fixture.Store.Accept(first with
        {
            NormalizedPayloadJson = "{ \"stable\" : true, \"weight\" : 1.25 }"
        });

        Assert.Equal(ExtensionResultInboxDisposition.Accepted, accepted.Disposition);
        Assert.Equal(ExtensionResultInboxDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(accepted.InboxId, duplicate.InboxId);
        ExtensionResultInboxItem item = RequireNotNull(fixture.Store.Get(accepted.InboxId!.Value));
        Assert.Equal("{\"stable\":true,\"weight\":1.25}", item.PayloadJson);
        Assert.Equal(ExtensionResultInboxStates.Pending, item.State);
    }

    [Fact]
    public void Accept_RejectsRevisionConflictStaleRevisionAndDeliveryIdentityChange()
    {
        using var fixture = new InboxFixture();
        ExtensionResultSubmission revision2 = fixture.Submission() with { Revision = 2 };
        Assert.Equal(
            ExtensionResultInboxDisposition.Accepted,
            fixture.Store.Accept(revision2).Disposition);

        Assert.Equal(
            ExtensionResultInboxDisposition.RevisionConflict,
            fixture.Store.Accept(revision2 with
            {
                NormalizedPayloadJson = "{\"weight\":2.0}"
            }).Disposition);
        Assert.Equal(
            ExtensionResultInboxDisposition.StaleRevision,
            fixture.Store.Accept(revision2 with { Revision = 1 }).Disposition);
        Assert.Equal(
            ExtensionResultInboxDisposition.DeliveryIdentityConflict,
            fixture.Store.Accept(revision2 with
            {
                Revision = 3,
                TaskId = "different-task-001"
            }).Disposition);
    }

    [Fact]
    public void Accept_BusinessDuplicateDoesNotCreateAnotherInboxItem()
    {
        using var fixture = new InboxFixture();
        ExtensionResultSubmission original = fixture.Submission();
        ExtensionResultInboxAcceptResult accepted = fixture.Store.Accept(original);

        ExtensionResultInboxAcceptResult duplicate = fixture.Store.Accept(original with
        {
            DeliveryId = "different-delivery-001",
            TaskId = "different-task-001",
            Revision = 1
        });

        Assert.Equal(ExtensionResultInboxDisposition.BusinessDuplicate, duplicate.Disposition);
        Assert.Equal(accepted.InboxId, duplicate.InboxId);
        ExtensionResultInboxItem claimed = RequireNotNull(fixture.Store.ClaimNext());
        Assert.Equal(accepted.InboxId, claimed.Id);
        Assert.Null(fixture.Store.ClaimNext());
    }

    [Fact]
    public void Accept_RejectsResultIdReuseWithDifferentPayload()
    {
        using var fixture = new InboxFixture();
        ExtensionResultSubmission original = fixture.Submission();
        fixture.Store.Accept(original);

        ExtensionResultInboxAcceptResult conflict = fixture.Store.Accept(original with
        {
            DeliveryId = "different-delivery-002",
            TaskId = "different-task-002",
            NormalizedPayloadJson = "{\"weight\":9.99}"
        });

        Assert.Equal(ExtensionResultInboxDisposition.ResultIdConflict, conflict.Disposition);
    }

    [Fact]
    public void ClaimFailureRetryAndAppliedLifecycleIsDurable()
    {
        using var fixture = new InboxFixture();
        long id = fixture.Store.Accept(fixture.Submission()).InboxId!.Value;
        ExtensionResultInboxItem firstClaim = RequireNotNull(fixture.Store.ClaimNext());
        Assert.Equal(1, firstClaim.AttemptCount);
        DateTimeOffset retryAt = fixture.Time.GetUtcNow().AddSeconds(10);
        Assert.True(fixture.Store.MarkFailed(id, "temporary failure", retryAt));
        Assert.Null(fixture.Store.ClaimNext());

        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        ExtensionResultInboxItem secondClaim = RequireNotNull(fixture.Store.ClaimNext());
        Assert.Equal(2, secondClaim.AttemptCount);
        Assert.True(fixture.Store.MarkApplied(id));
        Assert.False(fixture.Store.MarkApplied(id));
        Assert.Equal(ExtensionResultInboxStates.Applied, fixture.Store.Get(id)!.State);
        Assert.Null(fixture.Store.ClaimNext());
    }

    [Fact]
    public void RecoverInterruptedReturnsApplyingItemToPendingAcrossRestart()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "videos.db");
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        try
        {
            long id;
            using (var first = new ExtensionResultInboxStore(databasePath, time))
            {
                id = first.Accept(Submission(time)).InboxId!.Value;
                Assert.NotNull(first.ClaimNext());
            }

            using var restarted = new ExtensionResultInboxStore(databasePath, time);
            Assert.Equal(1, restarted.RecoverInterrupted());
            ExtensionResultInboxItem recovered = RequireNotNull(restarted.ClaimNext());
            Assert.Equal(id, recovered.Id);
            Assert.Equal(2, recovered.AttemptCount);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Accept_RejectsDuplicateJsonPropertiesAndOversizedPayload()
    {
        using var fixture = new InboxFixture();

        Assert.Throws<InvalidDataException>(() => fixture.Store.Accept(fixture.Submission() with
        {
            NormalizedPayloadJson = "{\"weight\":1,\"weight\":2}"
        }));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Accept(fixture.Submission() with
        {
            NormalizedPayloadJson = "{\"value\":\"" + new string('x', ExtensionResultInboxStore.MaxPayloadBytes) + "\"}"
        }));
    }

    private static ExtensionResultSubmission Submission(MutableTimeProvider time) => new()
    {
        ExtensionInstanceId = "scale-extension-001",
        ProviderId = "example.scale",
        ResultId = "stable-result-001",
        DeliveryId = "delivery-result-001",
        TaskId = "scan-task-result-001",
        Capability = ExtensionScanCapabilities.MeasurementCapture,
        Revision = 1,
        Status = ExtensionScanResultStatus.Completed,
        ObservedAtUtc = time.GetUtcNow(),
        NormalizedPayloadJson = "{\"weight\":1.25}"
    };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "packingproof-extension-inbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private static T RequireNotNull<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value!;
    }

    private sealed class InboxFixture : IDisposable
    {
        internal InboxFixture()
        {
            Directory = CreateTempDirectory();
            Time = new MutableTimeProvider(Utc(8, 0, 0));
            Store = new ExtensionResultInboxStore(Path.Combine(Directory, "videos.db"), Time);
        }

        internal string Directory { get; }
        internal MutableTimeProvider Time { get; }
        internal ExtensionResultInboxStore Store { get; }
        internal ExtensionResultSubmission Submission() =>
            ExtensionResultInboxStoreTests.Submission(Time);

        public void Dispose()
        {
            Store.Dispose();
            SqliteTestPool.ClearPoolFor(Directory);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
