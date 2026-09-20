namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// 等摄像头出画面时的轮询节拍。
///
/// <c>CameraFrameReadySignal</c> 是一次性信号：本会话收到过帧之后 <c>WaitAsync</c> 会立即返回 true，
/// 靠它节流会把"等新帧"变成空转（扫码卡住时能把一个核跑满、界面跟着卡）。
/// 所以这里给出固定轮询间隔，调用方在信号立刻返回时必须自己让出时间片。
/// </summary>
internal static class CameraFrameWaitPolicy
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>本次最多等多久再醒来一次；remaining 为正时返回值一定大于零。</summary>
    internal static TimeSpan ResolvePollDelay(TimeSpan remaining) =>
        remaining <= PollInterval ? remaining : PollInterval;
}
