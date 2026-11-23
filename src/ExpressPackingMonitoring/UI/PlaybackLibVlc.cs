using LibVLCSharp.Shared;

namespace ExpressPackingMonitoring.UI;

internal static class PlaybackLibVlc
{
    // 发布时必须保留 video_output/libdrawable_plugin.dll，
    // 它提供 embed-hwnd 窗口模块，MediaPlayer.Hwnd 才会被 LibVLC 使用。
    private static readonly string[] Options =
    [
        "--avcodec-hw=any",
        "--no-video-title-show"
    ];

    internal static LibVLC Create() => new(Options);
}
