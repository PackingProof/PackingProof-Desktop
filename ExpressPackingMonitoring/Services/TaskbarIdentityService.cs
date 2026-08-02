using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services;

internal static class TaskbarIdentityService
{
    internal const string AppUserModelId = "PackingProof.ExpressPackingMonitoring";

    internal static void TryApply()
    {
        try
        {
            int result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (result < 0)
            {
                RuntimeLog.Warn(
                    "Startup",
                    $"Unable to set AppUserModelID HRESULT=0x{result:X8}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Startup", $"Unable to set AppUserModelID: {ex.Message}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
