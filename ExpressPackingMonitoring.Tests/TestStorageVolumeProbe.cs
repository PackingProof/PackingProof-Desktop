using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 测试用的假卷探测：只要盘符存在就给一个"容量 1TB、可用 500GB"的卷，
/// 这样剩余空间相关的用例断言的是业务规则，而不是这台机器现在剩多少空间。
/// 路径不存在（例如离线网络盘）返回 null，保持"不可用"的语义。
/// </summary>
internal static class TestStorageVolumeProbe
{
    private const long OneTb = 1024L * 1024L * 1024L * 1024L;
    private const long FiveHundredGb = 500L * 1024L * 1024L * 1024L;

    public static IDisposable Use()
    {
        StorageLocationResolver.VolumeProbe = path =>
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                root = path;
            if (!Directory.Exists(root))
                return null;
            return new StorageVolumeInfo(root, OneTb, FiveHundredGb, "test-volume");
        };
        return new Reset();
    }

    private sealed class Reset : IDisposable
    {
        public void Dispose() => StorageLocationResolver.VolumeProbe = null;
    }
}
