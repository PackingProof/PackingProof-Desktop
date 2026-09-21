using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Host;

/// <summary>
/// 保存主机模式：按配置启动 Web 服务，接收手机与电脑上传的录像。
/// 服务、存储策略与数据库全部复用桌面端的实现，Mac 端只负责进程与配置。
/// </summary>
internal static class HostSession
{
    internal static async Task<int> RunAsync(AppConfig config, CancellationToken token)
    {
        string storageDirectory = config.StorageLocations?.FirstOrDefault()?.Path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(storageDirectory))
        {
            ReportStorageProblem("尚未设置录像保存位置。请用 --switch-purpose 重新选择用途与目录");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(storageDirectory);
        }
        catch (Exception ex)
        {
            ReportStorageProblem($"录像保存位置不可用：{storageDirectory}（{ex.Message}）");
            return 1;
        }

        // 手机扫码要用局域网地址，启动时解析一次；解析不到时手机连接页会提示尚未准备好
        string lanAddress = await WorkstationNetwork.GetVerifiedLocalAccessAddressAsync(config.WebServerPort, token);
        string connectionUrl = MobileConnectionService.BuildAccessUrl(
            lanAddress,
            config.RequireWebAccessKey,
            config.WebAccessKey);
        string localUrl = MobileConnectionService.BuildAccessUrl(
            $"127.0.0.1:{config.WebServerPort}",
            config.RequireWebAccessKey,
            config.WebAccessKey);

        using var database = new VideoDatabase(AppPaths.VideoDatabasePath);
        using var server = new WebServer(
            database,
            config.WebServerPort,
            listenerHost: "+",
            requireAccessKey: config.RequireWebAccessKey,
            accessKey: config.WebAccessKey,
            mobileConnectionUrlProvider: () => connectionUrl,
            mobileBackupComputerId: config.MobileBackupComputerId,
            mobileBackupComputerName: config.NodeName,
            mobileBackupStateDirectory: AppPaths.MobileBackupStateDir,
            mobileBackupRecordingRootResolver: () => storageDirectory,
            nodeId: config.NodeId,
            nodeName: config.NodeName,
            deploymentPreset: DeploymentPresets.MobileBackupHost,
            backupDeviceEnrollmentApprover: ApproveDeviceEnrollment);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            // 端口被占、权限不足等都以可读信息退出，不抛堆栈
            ReportProblem($"保存主机启动失败：{ex.Message}");
            return 1;
        }

        Console.WriteLine($"保存主机已启动，存储目录 {storageDirectory}");
        Console.WriteLine($"本机网页地址 {localUrl}");
        HostOptions.OpenUrl(localUrl);

        return await WaitForExitAsync();
    }

    /// <summary>
    /// 设备接入是信任决定：弹窗确认，弹不出来就拒绝。
    /// 手机端拿到二维码后仍需要主人在这台机器上点一次允许。
    /// </summary>
    private static BackupDeviceEnrollmentApprovalDecision ApproveDeviceEnrollment(
        BackupDeviceEnrollmentRequest request)
    {
        bool approved = MacDialog.AskDeviceApproval(request.DeviceName, request.DeviceKind);
        Console.WriteLine(
            $"设备接入申请 {request.DeviceName}（{request.DeviceKind}，{request.RemoteAddress}）：{(approved ? "已允许" : "已拒绝")}");
        return approved
            ? BackupDeviceEnrollmentApprovalDecision.Approved
            : BackupDeviceEnrollmentApprovalDecision.Denied;
    }

    private static async Task<int> WaitForExitAsync()
    {
        Console.WriteLine("按 Ctrl+C 退出");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            completion.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => completion.TrySetResult();
        await completion.Task;
        return 0;
    }

    private static void ReportStorageProblem(string message) =>
        ReportProblem($"{message}。可用 --switch-purpose 重新设置");

    private static void ReportProblem(string message)
    {
        Console.Error.WriteLine(message);
        MacDialog.ShowMessage(message);
    }
}
