using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;

if (args.Length < 3 || !int.TryParse(args[0], out int port))
{
    Console.Error.WriteLine("Usage: <port> <data-dir> <fixture-video>");
    return 2;
}

string dataDir = Path.GetFullPath(args[1]);
string fixtureVideo = Path.GetFullPath(args[2]);
Directory.CreateDirectory(dataDir);
Environment.SetEnvironmentVariable("EPM_USER_DATA_DIR", dataDir);

using var database = new VideoDatabase(Path.Combine(dataDir, "automation.db"));
long recordId = database.InsertVideoRecord(
    "AUTO_WEB_001",
    "发货",
    "H264",
    "automation",
    fixtureVideo,
    DateTime.Now.AddSeconds(-2));
database.UpdateVideoRecordOnStop(
    recordId,
    DateTime.Now,
    2,
    new FileInfo(fixtureVideo).Length,
    "automation",
    "H264",
    "automation");

string photoPath = Path.Combine(dataDir, "AUTO_PHOTO_001.jpg");
File.WriteAllBytes(
    photoPath,
    Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q=="));
var photoStore = new PhotoStore(Path.Combine(dataDir, "automation.db"));
photoStore.Add(
    "AUTO_PHOTO_001",
    "发货",
    photoPath,
    "automation",
    "自动化电脑",
    1,
    1,
    new FileInfo(photoPath).Length,
    DateTimeOffset.Now);

using var server = new WebServer(
    database,
    port,
    transCacheMaxMB: 64,
    listenerHost: "127.0.0.1",
    mobileConnectionUrlProvider: () => $"http://192.168.1.20:{port}",
    photoStore: photoStore);
server.Start();
Console.WriteLine($"READY http://127.0.0.1:{port}/");
Console.Out.Flush();

var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    completion.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => completion.TrySetResult();
await completion.Task;
return 0;
