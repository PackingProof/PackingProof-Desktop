using ExpressPackingMonitoring.Config;
using System.Collections.Generic;
using System.IO;

namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    private bool TableExists(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private HashSet<string> GetTableColumns(string tableName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''")}');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(1));
        return result;
    }

    private static string CreateSchemaMigrationBackupDirectory()
    {
        Directory.CreateDirectory(AppPaths.BackupsDir);
        string baseName = $"schema-migration-videos-db-{DateTime.Now:yyyyMMdd-HHmmss}";
        string dir = Path.Combine(AppPaths.BackupsDir, baseName);
        int suffix = 1;
        while (Directory.Exists(dir))
        {
            suffix++;
            dir = Path.Combine(AppPaths.BackupsDir, $"{baseName}-{suffix}");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CopySqliteFileIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath)) return;
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        ExecuteNonQuery($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
    }

    private void EnsureSchemaVersion()
    {
        int currentVersion = ReadUserVersion();
        if (currentVersion < SchemaVersion)
            ExecuteNonQuery($"PRAGMA user_version = {SchemaVersion};");
        else if (currentVersion > SchemaVersion)
            System.Diagnostics.Debug.WriteLine(
                $"VideoDatabase schema version {currentVersion} is newer than supported {SchemaVersion}");
    }
}
