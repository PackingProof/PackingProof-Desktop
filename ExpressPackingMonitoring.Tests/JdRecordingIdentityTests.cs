using ExpressPackingMonitoring.Data;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class JdRecordingIdentityTests
{
    [Fact]
    public void CompleteOnlyActiveBareRecordAndSearchBothBarcodeForms()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var db = new VideoDatabase(path);
            long first = db.InsertVideoRecord("JD123456789012", "发货", "", "", "first.mkv", DateTime.Now);
            Assert.True(db.CompleteRecordingPackageIdentity(first, "JD123456789012", "JD123456789012-1-2-"));
            Assert.False(db.CompleteRecordingPackageIdentity(first, "JD123456789012", "JD123456789012-2-2-"));
            db.InsertVideoRecord("JD123456789012-2-2-", "发货", "", "", "second.mkv", DateTime.Now);
            db.InsertVideoRecord("JD1234567890129-1-2-", "发货", "", "", "other.mkv", DateTime.Now);
            var byBare = db.QueryVideosPaged(null, null, "JD123456789012", 1, 50, false, VideoSearchMode.ExactOrderIdentifiers);
            Assert.Equal(2, byBare.Records.Count);
            var byPackage = db.QueryVideosPaged(null, null, "JD123456789012-1-2-", 1, 50, false, VideoSearchMode.ExactOrderIdentifiers);
            Assert.Equal(first, Assert.Single(byPackage.Records).Id);
            long stopped = db.InsertVideoRecord("JD999999999999", "发货", "", "", "stopped.mkv", DateTime.Now);
            db.UpdateVideoRecordOnStop(stopped, DateTime.Now, 10, 1000, "test");
            Assert.False(db.CompleteRecordingPackageIdentity(stopped, "JD999999999999", "JD999999999999-1-1-"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
