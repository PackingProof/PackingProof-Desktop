using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 测试数据库连接池清理辅助。
/// 只清空指定临时目录内数据库对应的连接池，避免全局 ClearAllPools 与并行测试竞争。
/// </summary>
internal static class SqliteTestPool
{
    public static void ClearPoolFor(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        foreach (string databasePath in Directory.EnumerateFiles(
                     directory,
                     "*.db",
                     SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            SqliteConnection.ClearPool(connection);
        }
    }
}
