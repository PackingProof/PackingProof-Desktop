using System.IO;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionEnrollmentServiceTests
{
    [Fact]
    public void Enroll_RetryReturnsSameCredentialWithoutSecondPromptOrRotation()
    {
        using var fixture = new EnrollmentFixture();
        int prompts = 0;
        var service = fixture.Service(request =>
        {
            prompts++;
            return ApproveAll(request);
        });
        ExtensionEnrollmentRequest request = OrderRequest();

        ExtensionEnrollmentOutcome first = service.Enroll(request);
        ExtensionEnrollmentOutcome retry = service.Enroll(request);

        Assert.Equal(ExtensionEnrollmentDisposition.Approved, first.Disposition);
        Assert.Equal(first.Enrollment!.Credential, retry.Enrollment!.Credential);
        Assert.Equal(1, first.Enrollment.Authorization.CredentialGeneration);
        Assert.Equal(1, retry.Enrollment.Authorization.CredentialGeneration);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void Enroll_ApprovalCannotElevatePermissionOrCapability()
    {
        using var fixture = new EnrollmentFixture();
        var permissionElevation = fixture.Service(request => ApproveAll(request) with
        {
            ApprovedPermissions = request.RequestedPermissions
                .Append(ExtensionPermissions.RecordingFieldsWrite)
                .ToArray()
        });
        Assert.Throws<InvalidDataException>(() => permissionElevation.Enroll(OrderRequest()));

        var capabilityElevation = fixture.Service(request => ApproveAll(request) with
        {
            ApprovedCapabilities = [ExtensionScanCapabilities.MeasurementCapture]
        });
        Assert.Throws<InvalidDataException>(() => capabilityElevation.Enroll(OrderRequest() with
        {
            RequestId = "enroll-request-002",
            RequestSecret = Secret('b')
        }));
        Assert.Empty(fixture.Store.GetAll());
    }

    [Fact]
    public void Enroll_RecordingDownloadAlsoRequiresSearchPermission()
    {
        using var fixture = new EnrollmentFixture();
        ExtensionEnrollmentRequest request = OrderRequest() with
        {
            RequestedPermissions = [ExtensionPermissions.RecordingsDownload],
            RequestedCapabilities = []
        };

        Assert.Throws<InvalidDataException>(() => fixture.Service(ApproveAll).Enroll(request));
    }

    [Fact]
    public void Enroll_MeasurementUsesHostSelectedNodeBinding()
    {
        using var fixture = new EnrollmentFixture();
        var service = fixture.Service(request => ApproveAll(request) with
        {
            RoutingScope = ExtensionRoutingScope.SelectedRecordingNodes,
            BoundOriginNodeIds = ["recording-node-002"]
        });

        ExtensionEnrollmentOutcome outcome = service.Enroll(MeasurementRequest());

        Assert.Equal(ExtensionEnrollmentDisposition.Approved, outcome.Disposition);
        Assert.True(outcome.Enrollment!.Authorization.IsBoundToOriginNode("recording-node-002"));
        Assert.False(outcome.Enrollment.Authorization.IsBoundToOriginNode("recording-node-001"));
    }

    [Fact]
    public void Enroll_DeniedOrUnavailableDoesNotCreateAuthorization()
    {
        using var fixture = new EnrollmentFixture();
        ExtensionEnrollmentOutcome denied = fixture.Service(_ => new ExtensionEnrollmentApprovalResult
        {
            Disposition = ExtensionEnrollmentApprovalDisposition.Denied
        }).Enroll(OrderRequest());
        ExtensionEnrollmentOutcome unavailable = fixture.Service(_ => new ExtensionEnrollmentApprovalResult
        {
            Disposition = ExtensionEnrollmentApprovalDisposition.Unavailable
        }).Enroll(OrderRequest() with
        {
            RequestId = "enroll-request-003",
            RequestSecret = Secret('c')
        });

        Assert.Equal(ExtensionEnrollmentDisposition.Denied, denied.Disposition);
        Assert.Equal(ExtensionEnrollmentDisposition.Unavailable, unavailable.Disposition);
        Assert.Empty(fixture.Store.GetAll());
    }

    [Fact]
    public void Enroll_SameRetryProofWithDifferentRequestReturnsConflict()
    {
        using var fixture = new EnrollmentFixture();
        int prompts = 0;
        var service = fixture.Service(request =>
        {
            prompts++;
            return ApproveAll(request);
        });
        ExtensionEnrollmentRequest request = OrderRequest();
        service.Enroll(request);

        ExtensionEnrollmentOutcome conflict = service.Enroll(request with
        {
            DisplayName = "被篡改的扩展名称"
        });

        Assert.Equal(ExtensionEnrollmentDisposition.RequestConflict, conflict.Disposition);
        Assert.Equal(1, prompts);
        Assert.Single(fixture.Store.GetAll());
    }

    [Fact]
    public void Enroll_SameRetryProofFromDifferentAddressReturnsConflict()
    {
        using var fixture = new EnrollmentFixture();
        int prompts = 0;
        var service = fixture.Service(request =>
        {
            prompts++;
            return ApproveAll(request);
        });
        ExtensionEnrollmentRequest request = OrderRequest();
        service.Enroll(request);

        ExtensionEnrollmentOutcome conflict = service.Enroll(request with
        {
            RemoteAddress = "192.168.1.99"
        });

        Assert.Equal(ExtensionEnrollmentDisposition.RequestConflict, conflict.Disposition);
        Assert.Equal(1, prompts);
        Assert.Single(fixture.Store.GetAll());
    }

    private static ExtensionEnrollmentApprovalResult ApproveAll(
        ExtensionEnrollmentRequest request) => new()
    {
        Disposition = ExtensionEnrollmentApprovalDisposition.Approved,
        ApprovedPermissions = request.RequestedPermissions,
        ApprovedCapabilities = request.RequestedCapabilities,
        RoutingScope = ExtensionRoutingScope.AllLocalRecordingNodes
    };

    private static ExtensionEnrollmentRequest OrderRequest() => new()
    {
        RequestId = "enroll-request-001",
        RequestSecret = Secret('a'),
        ExtensionInstanceId = "order-extension-001",
        ProviderId = "example.erp",
        DisplayName = "示例订单扩展",
        Version = "1.0",
        Source = "https://example.invalid/order-extension",
        RemoteAddress = "192.168.1.20",
        RequestedPermissions =
        [
            ExtensionPermissions.ScanTasksRead,
            ExtensionPermissions.ScanResultsWrite
        ],
        RequestedCapabilities = [ExtensionScanCapabilities.OrderLookup]
    };

    private static ExtensionEnrollmentRequest MeasurementRequest() => new()
    {
        RequestId = "enroll-request-004",
        RequestSecret = Secret('d'),
        ExtensionInstanceId = "scale-extension-001",
        ProviderId = "example.scale",
        DisplayName = "示例称重扩展",
        Version = "1.0",
        Source = "local-userscript",
        RemoteAddress = "192.168.1.21",
        RequestedPermissions =
        [
            ExtensionPermissions.ScanTasksRead,
            ExtensionPermissions.ScanResultsWrite,
            ExtensionPermissions.RecordingFieldsWrite
        ],
        RequestedCapabilities = [ExtensionScanCapabilities.MeasurementCapture]
    };

    private static string Secret(char value) => new(value, 64);

    private sealed class EnrollmentFixture : IDisposable
    {
        internal EnrollmentFixture()
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "packingproof-extension-enrollment-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero));
            Store = new ExtensionAuthorizationStore(Directory, Time);
        }

        internal string Directory { get; }
        internal MutableTimeProvider Time { get; }
        internal ExtensionAuthorizationStore Store { get; }

        internal ExtensionEnrollmentService Service(
            Func<ExtensionEnrollmentRequest, ExtensionEnrollmentApprovalResult> approver) =>
            new(Store, approver, Time);

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
