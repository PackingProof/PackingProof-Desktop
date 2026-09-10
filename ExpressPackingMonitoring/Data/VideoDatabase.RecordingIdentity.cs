using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    /// <summary>停录前将本次录像的京东裸号补全；不修改已有包裹或已结束记录。</summary>
    public bool CompleteRecordingPackageIdentity(long recordId, string expectedWaybill, string packageCode)
    {
        if (!JdBarcodePolicy.MatchesPackage(expectedWaybill, packageCode)) return false;
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                UPDATE VideoRecords SET OrderId = @package, TrackingNumber = @package
                WHERE Id = @id AND OrderId = @expected AND EndTime IS NULL
                    AND IsDeleted = 0 AND SourceType = 'pc';";
            command.Parameters.AddWithValue("@id", recordId);
            command.Parameters.AddWithValue("@expected", expectedWaybill);
            command.Parameters.AddWithValue("@package", packageCode);
            return command.ExecuteNonQuery() == 1;
        }
    }

    private static string ExactRecordingIdentitySearch(string keyword)
    {
        const string exact = "OrderId = @keyword OR TrackingNumber = @keyword OR SourceOrderId = @keyword";
        // The delimiter prevents a bare waybill matching a longer unrelated waybill.
        if (JdBarcodePolicy.IsBareWaybill(keyword.ToUpperInvariant()))
            return " AND (" + exact + " OR upper(OrderId) GLOB (upper(@keyword) || '-[0-9]*-[0-9]*-'))";
        return " AND (" + exact + ")";
    }
}
