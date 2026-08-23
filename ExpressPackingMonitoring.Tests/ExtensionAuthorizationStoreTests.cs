using System.IO;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionAuthorizationStoreTests
{
    [Fact]
    public void Approve_PersistsEncryptedCredentialAndRestoresAuthorization()
    {
        string directory = CreateTempDirectory();
        try
        {
            var time = new MutableTimeProvider(Utc(8, 0, 0));
            var store = new ExtensionAuthorizationStore(directory, time);
            ExtensionEnrollmentCredential enrollment = store.Approve(OrderApproval());

            string extensionDirectory = Path.Combine(directory, "extensions");
            string registry = File.ReadAllText(
                Path.Combine(extensionDirectory, "authorizations.json"));
            Assert.DoesNotContain(enrollment.Credential, registry, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(extensionDirectory, "extension-root.key")));
            Assert.False(File.Exists(Path.Combine(directory, "backup-device-root.key")));

            var restarted = new ExtensionAuthorizationStore(directory, time);
            Assert.True(restarted.TryAuthenticate(
                enrollment.Authorization.ExtensionInstanceId,
                enrollment.Credential,
                out ExtensionAuthorizationContext? restored));
            Assert.NotNull(restored);
            Assert.True(restored!.HasPermission(ExtensionPermissions.ScanTasksRead));
            Assert.True(restored.SupportsCapability(ExtensionScanCapabilities.OrderLookup));
            Assert.True(restored.IsBoundToOriginNode("any-local-node-001"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Approve_MeasurementRequiresFieldPermissionAndSelectedNodeBinding()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new ExtensionAuthorizationStore(directory);
            ExtensionAuthorizationApproval approval = MeasurementApproval();

            Assert.Throws<InvalidDataException>(() => store.Approve(approval with
            {
                Permissions =
                [
                    ExtensionPermissions.ScanTasksRead,
                    ExtensionPermissions.ScanResultsWrite
                ]
            }));
            Assert.Throws<InvalidDataException>(() => store.Approve(approval with
            {
                RoutingScope = ExtensionRoutingScope.AllLocalRecordingNodes,
                BoundOriginNodeIds = []
            }));

            ExtensionEnrollmentCredential accepted = store.Approve(approval);
            Assert.True(accepted.Authorization.IsBoundToOriginNode("recording-node-001"));
            Assert.False(accepted.Authorization.IsBoundToOriginNode("recording-node-002"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Approve_RejectsUnknownPermissionAndCapability()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new ExtensionAuthorizationStore(directory);

            Assert.Throws<InvalidDataException>(() => store.Approve(OrderApproval() with
            {
                Permissions = ["admin.everything"]
            }));
            Assert.Throws<InvalidDataException>(() => store.Approve(OrderApproval() with
            {
                Capabilities = ["orders.lookpu"]
            }));
            Assert.Empty(store.GetAll());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RotateCredential_InvalidatesOldCredentialAndIncrementsGeneration()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new ExtensionAuthorizationStore(directory);
            ExtensionEnrollmentCredential original = store.Approve(OrderApproval());
            ExtensionEnrollmentCredential rotated = store.RotateCredential(
                original.Authorization.ExtensionInstanceId);

            Assert.NotEqual(original.Credential, rotated.Credential);
            Assert.Equal(
                original.Authorization.CredentialGeneration + 1,
                rotated.Authorization.CredentialGeneration);
            Assert.False(store.TryAuthenticate(
                original.Authorization.ExtensionInstanceId,
                original.Credential,
                out _));
            Assert.True(store.TryAuthenticate(
                original.Authorization.ExtensionInstanceId,
                rotated.Credential,
                out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Revoke_InvalidatesCredentialAcrossRestartWithoutDeletingAuditMetadata()
    {
        string directory = CreateTempDirectory();
        try
        {
            var time = new MutableTimeProvider(Utc(8, 0, 0));
            var store = new ExtensionAuthorizationStore(directory, time);
            ExtensionEnrollmentCredential enrollment = store.Approve(OrderApproval());
            time.Advance(TimeSpan.FromMinutes(1));

            Assert.True(store.Revoke(enrollment.Authorization.ExtensionInstanceId));
            Assert.False(store.Revoke(enrollment.Authorization.ExtensionInstanceId));
            Assert.False(store.TryAuthenticate(
                enrollment.Authorization.ExtensionInstanceId,
                enrollment.Credential,
                out _));

            var restarted = new ExtensionAuthorizationStore(directory, time);
            ExtensionAuthorizationContext revoked = Assert.Single(restarted.GetAll());
            Assert.NotNull(revoked.RevokedAtUtc);
            Assert.Empty(restarted.GetAll(includeRevoked: false));
            Assert.False(restarted.TryAuthenticate(
                enrollment.Authorization.ExtensionInstanceId,
                enrollment.Credential,
                out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Approve_ReapprovalRotatesCredentialAndPreservesOriginalApprovalTime()
    {
        string directory = CreateTempDirectory();
        try
        {
            var time = new MutableTimeProvider(Utc(8, 0, 0));
            var store = new ExtensionAuthorizationStore(directory, time);
            ExtensionEnrollmentCredential first = store.Approve(OrderApproval());
            time.Advance(TimeSpan.FromMinutes(2));

            ExtensionEnrollmentCredential second = store.Approve(OrderApproval() with
            {
                DisplayName = "更新后的 ERP 扩展"
            });

            Assert.Equal(first.Authorization.ApprovedAtUtc, second.Authorization.ApprovedAtUtc);
            Assert.True(second.Authorization.UpdatedAtUtc > first.Authorization.UpdatedAtUtc);
            Assert.Equal(2, second.Authorization.CredentialGeneration);
            Assert.False(store.TryAuthenticate(
                first.Authorization.ExtensionInstanceId,
                first.Credential,
                out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ExtensionAuthorizationApproval OrderApproval() => new()
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

    private static ExtensionAuthorizationApproval MeasurementApproval() => new()
    {
        ExtensionInstanceId = "scale-extension-001",
        ProviderId = "example.scale",
        DisplayName = "示例称重扩展",
        Version = "1.0",
        Source = "local-service",
        Permissions =
        [
            ExtensionPermissions.ScanTasksRead,
            ExtensionPermissions.ScanResultsWrite,
            ExtensionPermissions.RecordingFieldsWrite
        ],
        Capabilities = [ExtensionScanCapabilities.MeasurementCapture],
        RoutingScope = ExtensionRoutingScope.SelectedRecordingNodes,
        BoundOriginNodeIds = ["recording-node-001"]
    };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "packingproof-extension-auth-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
