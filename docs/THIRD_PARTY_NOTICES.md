# 第三方组件说明

发布包使用以下第三方组件。各组件仍遵循其自身许可证，具体版本以项目文件和发布基线清单为准。

## FFmpeg

- Windows 二进制来源：[Gyan FFmpeg Builds](https://www.gyan.dev/ffmpeg/builds/)
- 发布变体：Essentials build
- 许可证：GPLv3
- 对应版本、下载地址、文件大小和 SHA256：`Tools/ffmpeg-baseline.json`
- FFmpeg 源代码：[FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg)

- macOS 二进制来源：[Martin Riedl FFmpeg Build Server](https://ffmpeg.martin-riedl.de/)
- 发布变体：macOS Apple Silicon arm64 release 静态构建
- 许可证：GPLv3（该构建未启用 `--enable-nonfree`，可以随包分发）
- 对应版本、下载地址、文件大小和 SHA256：`Tools/ffmpeg-macos-baseline.json`
- FFmpeg 源代码：[FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg)

发布包只携带独立的 `ffmpeg.exe`（Windows）或 `ffmpeg`（macOS），用于录像编码、封装、转码、缩略图和剪辑。macOS 主机上它位于 `PackingProofHost.app/Contents/MacOS/Host/tools/ffmpeg`。

## LibVLC

- 项目：[VideoLAN LibVLC](https://www.videolan.org/vlc/libvlc.html)
- 许可证：LGPL-2.1-or-later，部分插件可能采用兼容的其他开源许可证
- 用途：电脑端本机录像回放

发布包仅保留本地录像回放所需的运行库和插件，不包含网络串流、光盘访问或推流输出组件。
