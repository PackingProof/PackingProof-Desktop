using System.ComponentModel;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 识别“网络归档目标不可达”类错误，与单文件冲突、哈希失败、目标磁盘满等
/// 单条可重试错误区分。这类错误通常意味着整个备份位置离线，应触发归档熔断，
/// 而不是对数千条积压录像逐条快速空转重试。
/// </summary>
internal static class NetworkArchiveErrorClassifier
{
    // Win32 网络路径/共享错误码。.NET 会把 GetLastError 转成 HRESULT_FROM_WIN32，
    // 因此低 16 位与 NativeErrorCode 相同；部分异常则直接包裹 Win32Exception。
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidDrive = 15;
    private const int ErrorDeviceNotReady = 21;
    private const int ErrorBadNetPath = 53;
    private const int ErrorNetworkNameDeleted = 64;
    private const int ErrorNetworkAccessDenied = 65;
    private const int ErrorBadNetName = 67;
    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorHostUnreachable = 1232;

    public static bool IsTargetUnreachable(Exception? ex)
    {
        for (Exception? current = ex;
             current != null;
             current = current.InnerException)
        {
            if (current is IOException io
                && IsUnreachableCode(io.HResult & 0xFFFF))
            {
                return true;
            }

            if (current is Win32Exception win32
                && IsUnreachableCode(win32.NativeErrorCode))
            {
                return true;
            }

            if (current is DirectoryNotFoundException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnreachableCode(int code) =>
        code is ErrorPathNotFound
            or ErrorInvalidDrive
            or ErrorDeviceNotReady
            or ErrorBadNetPath
            or ErrorNetworkNameDeleted
            or ErrorNetworkAccessDenied
            or ErrorBadNetName
            or ErrorNetworkUnreachable
            or ErrorHostUnreachable;
}
