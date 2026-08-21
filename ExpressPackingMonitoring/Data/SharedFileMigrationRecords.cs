namespace ExpressPackingMonitoring.Data;

internal sealed record SharedFileMigrationQuarantine(long RecordId, string StateJson);

internal sealed record SharedFileMigrationUpdate(
    long RecordId,
    string ExpectedFilePath,
    string NewFilePath,
    long FileSizeBytes,
    string ExpectedArchivePath,
    string NewArchivePath,
    string RestoredArchiveStatus,
    string RestoredArchiveError);
