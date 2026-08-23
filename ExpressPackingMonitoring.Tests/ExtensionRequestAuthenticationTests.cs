using System.Text;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionRequestAuthenticationTests
{
    [Fact]
    public void Authenticate_AcceptsValidSignatureAndReturnsTrustedAuthorization()
    {
        using var fixture = new AuthenticationFixture();
        ExtensionSignedRequest request = fixture.CreateRequest(
            nonce: Nonce('a'),
            requestTarget: "/api/extensions/v1/scan-tasks/poll?wait=20");

        ExtensionAuthenticationResult result = fixture.Authenticator.Authenticate(request);

        Assert.Equal(ExtensionAuthenticationDisposition.Accepted, result.Disposition);
        Assert.NotNull(result.Authorization);
        Assert.Equal(
            fixture.Enrollment.Authorization.ExtensionInstanceId,
            result.Authorization!.ExtensionInstanceId);
        Assert.True(result.Authorization.HasPermission(ExtensionPermissions.ScanTasksRead));
    }

    [Fact]
    public void Authenticate_RejectsBodyAndQueryTamperingWithoutClaimingNonce()
    {
        using var fixture = new AuthenticationFixture();
        ExtensionSignedRequest original = fixture.CreateRequest(
            nonce: Nonce('b'),
            requestTarget: "/api/extensions/v1/scan-results?mode=order",
            body: "{\"revision\":1}");

        Assert.Equal(
            ExtensionAuthenticationDisposition.ContentHashMismatch,
            fixture.Authenticator.Authenticate(original with
            {
                Body = Encoding.UTF8.GetBytes("{\"revision\":2}")
            }).Disposition);
        Assert.Equal(
            ExtensionAuthenticationDisposition.InvalidSignature,
            fixture.Authenticator.Authenticate(original with
            {
                RequestTarget = "/api/extensions/v1/scan-results?mode=measurement"
            }).Disposition);
        Assert.Equal(
            ExtensionAuthenticationDisposition.Accepted,
            fixture.Authenticator.Authenticate(original).Disposition);
    }

    [Fact]
    public void Authenticate_RejectsReplayAfterValidRequest()
    {
        using var fixture = new AuthenticationFixture();
        ExtensionSignedRequest request = fixture.CreateRequest(nonce: Nonce('c'));

        Assert.Equal(
            ExtensionAuthenticationDisposition.Accepted,
            fixture.Authenticator.Authenticate(request).Disposition);
        Assert.Equal(
            ExtensionAuthenticationDisposition.ReplayDetected,
            fixture.Authenticator.Authenticate(request).Disposition);
    }

    [Fact]
    public void Authenticate_RejectsStaleTimestampBeforeSignatureProcessing()
    {
        using var fixture = new AuthenticationFixture();
        ExtensionSignedRequest stale = fixture.CreateRequest(
            nonce: Nonce('d'),
            timestamp: fixture.Time.GetUtcNow().AddMinutes(-6).ToUnixTimeSeconds());

        Assert.Equal(
            ExtensionAuthenticationDisposition.StaleTimestamp,
            fixture.Authenticator.Authenticate(stale).Disposition);
    }

    [Fact]
    public void Authenticate_RotationRejectsOldGenerationAndAcceptsNewCredential()
    {
        using var fixture = new AuthenticationFixture();
        ExtensionSignedRequest oldRequest = fixture.CreateRequest(nonce: Nonce('e'));
        ExtensionEnrollmentCredential rotated = fixture.Store.RotateCredential(
            fixture.Enrollment.Authorization.ExtensionInstanceId);

        Assert.Equal(
            ExtensionAuthenticationDisposition.CredentialGenerationMismatch,
            fixture.Authenticator.Authenticate(oldRequest).Disposition);

        ExtensionSignedRequest newRequest = CreateRequest(
            fixture.Time,
            rotated,
            nonce: Nonce('f'));
        Assert.Equal(
            ExtensionAuthenticationDisposition.Accepted,
            fixture.Authenticator.Authenticate(newRequest).Disposition);
    }

    [Fact]
    public void Authenticate_ReplayCapacityIsPartitionedPerExtension()
    {
        using var fixture = new AuthenticationFixture(maxNoncesPerExtension: 1);
        ExtensionSignedRequest first = fixture.CreateRequest(nonce: Nonce('1'));
        ExtensionSignedRequest second = fixture.CreateRequest(nonce: Nonce('2'));

        Assert.Equal(
            ExtensionAuthenticationDisposition.Accepted,
            fixture.Authenticator.Authenticate(first).Disposition);
        Assert.Equal(
            ExtensionAuthenticationDisposition.ReplayCapacityExceeded,
            fixture.Authenticator.Authenticate(second).Disposition);

        ExtensionEnrollmentCredential other = fixture.Store.Approve(Approval() with
        {
            ExtensionInstanceId = "other-extension-001",
            ProviderId = "example.other"
        });
        Assert.Equal(
            ExtensionAuthenticationDisposition.Accepted,
            fixture.Authenticator.Authenticate(CreateRequest(
                fixture.Time,
                other,
                nonce: Nonce('3'))).Disposition);
    }

    private static ExtensionSignedRequest CreateRequest(
        TimeProvider time,
        ExtensionEnrollmentCredential enrollment,
        string nonce,
        string requestTarget = "/api/extensions/v1/scan-tasks/poll",
        string body = "")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        long timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        string contentHash = ExtensionRequestSignature.ComputeContentHash(bytes);
        string signature = ExtensionRequestSignature.Create(
            enrollment.Credential,
            "GET",
            requestTarget,
            timestamp,
            nonce,
            contentHash,
            enrollment.Authorization.ExtensionInstanceId,
            enrollment.Authorization.CredentialGeneration);
        return new ExtensionSignedRequest
        {
            Version = ExtensionRequestSignature.CurrentVersion,
            ExtensionInstanceId = enrollment.Authorization.ExtensionInstanceId,
            CredentialGeneration = enrollment.Authorization.CredentialGeneration,
            Timestamp = timestamp,
            Nonce = nonce,
            ContentHash = contentHash,
            Signature = signature,
            Method = "GET",
            RequestTarget = requestTarget,
            Body = bytes
        };
    }

    private static ExtensionAuthorizationApproval Approval() => new()
    {
        ExtensionInstanceId = "erp-extension-001",
        ProviderId = "example.erp",
        DisplayName = "示例 ERP 扩展",
        Version = "1.0",
        Source = "local-userscript",
        Permissions =
        [
            ExtensionPermissions.ScanTasksRead,
            ExtensionPermissions.ScanResultsWrite
        ],
        Capabilities = [ExtensionScanCapabilities.OrderLookup],
        RoutingScope = ExtensionRoutingScope.AllLocalRecordingNodes
    };

    private static string Nonce(char value) => new(value, 32);

    private sealed class AuthenticationFixture : IDisposable
    {
        internal AuthenticationFixture(int maxNoncesPerExtension = 2048)
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "packingproof-extension-request-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Time = new MutableTimeProvider(
                new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero));
            Store = new ExtensionAuthorizationStore(Directory, Time);
            Enrollment = Store.Approve(Approval());
            Authenticator = new ExtensionRequestAuthenticator(
                Store,
                Time,
                maxNoncesPerExtension);
        }

        internal string Directory { get; }
        internal MutableTimeProvider Time { get; }
        internal ExtensionAuthorizationStore Store { get; }
        internal ExtensionEnrollmentCredential Enrollment { get; }
        internal ExtensionRequestAuthenticator Authenticator { get; }

        internal ExtensionSignedRequest CreateRequest(
            string nonce,
            string requestTarget = "/api/extensions/v1/scan-tasks/poll",
            string body = "",
            long? timestamp = null)
        {
            ExtensionSignedRequest request = ExtensionRequestAuthenticationTests.CreateRequest(
                Time,
                Enrollment,
                nonce,
                requestTarget,
                body);
            if (timestamp == null)
                return request;
            string signature = ExtensionRequestSignature.Create(
                Enrollment.Credential,
                request.Method,
                request.RequestTarget,
                timestamp.Value,
                request.Nonce,
                request.ContentHash,
                request.ExtensionInstanceId,
                request.CredentialGeneration);
            return request with { Timestamp = timestamp.Value, Signature = signature };
        }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
