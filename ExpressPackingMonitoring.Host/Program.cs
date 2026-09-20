using ExpressPackingMonitoring;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;

// Mac 保存主机的最小入口：先把“能起来、能收备份、网页能看”跑通。
// 用途选择、存储目录设置和开机自启随后接入，这里只负责按参数启动服务。
if (args.Length < 1)
{
    Console.Error.WriteLine("用法: ExpressPackingMonitoring.Host <存储目录> [端口]");
    return 2;
}

string storageDirectory = Path.GetFullPath(args[0]);
int port = args.Length > 1 && int.TryParse(args[1], out int parsedPort) ? parsedPort : 5280;
Directory.CreateDirectory(storageDirectory);

using var database = new VideoDatabase(AppPaths.VideoDatabasePath);
using var server = new WebServer(
    database,
    port,
    listenerHost: "+",
    mobileBackupComputerId: WorkstationConfigStore.Load().MobileBackupComputerId,
    mobileBackupStateDirectory: AppPaths.MobileBackupStateDir,
    mobileBackupRecordingRootResolver: () => storageDirectory,
    nodeName: Environment.MachineName,
    deploymentPreset: DeploymentPresets.MobileBackupHost,
    backupDeviceEnrollmentApprover: _ => BackupDeviceEnrollmentApprovalDecision.Approved);
server.Start();

Console.WriteLine($"保存主机已启动，存储目录 {storageDirectory}");
Console.WriteLine($"本机网页地址 http://127.0.0.1:{port}/");
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
