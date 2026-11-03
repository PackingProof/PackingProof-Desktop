# 第三方组件与许可证

发布包使用以下第三方组件，各组件仍遵循其自身许可证。组件版本、下载地址、文件大小与 SHA256 固定在 `tools/ffmpeg-baseline.json`、`tools/ffmpeg-macos-baseline.json` 和项目文件里，可逐项复核。

本项目自身以 AGPL-3.0 授权，全文见仓库根目录的 `LICENSE`。

## 随包分发的许可证全文

许可证全文随安装包一起分发，Windows 在安装目录的 `app\licenses\`，macOS 主机在 `PackingProofHost.app/Contents/MacOS/Host/licenses/`：

| 文件 | 对应组件 | 许可证 |
| --- | --- | --- |
| `AGPL-3.0.txt` | PackingProof 主程序（本项目自身） | AGPL-3.0 |
| `GPL-3.0.txt` | FFmpeg（Windows 与 macOS 构建） | GPL-3.0 |
| `LGPL-2.1.txt` | LibVLC 核心（`libvlc.dll` / `libvlccore.dll`） | LGPL-2.1 |
| `GPL-2.0.txt` | LibVLC 构建中含 GPL 组件的部分（VLC 3.0.x 为 GPLv2 或更高版本） | GPL-2.0 |
| `ffmpeg-build-notice.txt` | FFmpeg Windows 构建说明：版本、上游 commit、构建参数 | — |
| `ONNX-Runtime-LICENSE.txt` | ONNX Runtime | MIT |
| `ONNX-Runtime-ThirdPartyNotices.txt` | ONNX Runtime 第三方组件 | — |
| `DirectML-LICENSE.txt` | Microsoft DirectML | Microsoft 可再分发运行时条款 |
| `DirectML-ThirdPartyNotices.txt` | DirectML 第三方组件 | — |
| `THIRD_PARTY_NOTICES.md` | 本文件 | — |

全文来源：Windows 的 `ffmpeg-build-notice.txt` 同时说明可随包使用的自建精简构建与 Gyan essentials 回退构建，具体实际使用哪一份可依据 `ffmpeg -version` 和基线哈希确认；`GPL-3.0.txt` 对应 FFmpeg 4.4.1 源码。`LGPL-2.1.txt` 与 `GPL-2.0.txt` 取自 VideoLAN 上游 `vlc` 仓库 `3.0.21` 标签的 `COPYING.LIB` 与 `COPYING`。仓库内的副本保存在 `docs/third-party-licenses/`，打包时原样复制进安装包。

## FFmpeg

- Windows 二进制优先使用本项目基于 [FFmpeg 4.4.1 源码](https://ffmpeg.org/releases/ffmpeg-4.4.1.tar.xz) 自建的 PackingProof minimal V01 构建，以减小包体积
- 没有自建产物时允许使用 [Gyan FFmpeg 4.4.1 Essentials](https://www.gyan.dev/ffmpeg/builds/)；两种来源都必须在 `tools/ffmpeg-baseline.json` 中登记并通过大小与 SHA256 校验
- 发布变体与构建说明：`third_party/ffmpeg/README.md`
- 许可证：GPLv3
- 对应版本、下载地址、文件大小和 SHA256：`tools/ffmpeg-baseline.json`
- 源码归档 SHA256：`eadbad9e9ab30b25f5520fbfde99fae4a92a1ae3c0257a8d68569a4651e30e02`
- FFmpeg 源代码：[FFmpeg 4.4.1](https://ffmpeg.org/releases/ffmpeg-4.4.1.tar.xz)
- 构建依赖版本：[third_party/ffmpeg/versions.env](../third_party/ffmpeg/versions.env)

- macOS 二进制来源：[Martin Riedl FFmpeg Build Server](https://ffmpeg.martin-riedl.de/)
- 发布变体：macOS Apple Silicon arm64 release 静态构建
- 许可证：GPLv3（该构建未启用 `--enable-nonfree`，可以随包分发）
- 对应版本、下载地址、文件大小和 SHA256：`tools/ffmpeg-macos-baseline.json`
- FFmpeg 源代码：[FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg)

发布包只携带独立的 `ffmpeg.exe`（Windows）或 `ffmpeg`（macOS），用于录像编码、封装、转码、缩略图和剪辑。macOS 主机上它位于 `PackingProofHost.app/Contents/MacOS/Host/tools/ffmpeg`。

## LibVLC

- 项目：[VideoLAN LibVLC](https://www.videolan.org/vlc/libvlc.html)
- 许可证：LGPL-2.1-or-later，部分插件可能采用兼容的其他开源许可证
- 用途：电脑端本机录像回放

发布包仅保留本地录像回放所需的运行库和插件，不包含网络串流、光盘访问或推流输出组件。

## ONNX Runtime 与 DirectML

- ONNX Runtime：`Microsoft.ML.OnnxRuntime.DirectML` 1.23.0，MIT 许可证
- DirectML：`Microsoft.AI.DirectML` 1.15.4，Microsoft DirectX Machine Learning 可再分发条款
- 用途：增强面单识别功能的本地 ONNX 推理，优先使用 DirectML 调用显卡，初始化失败时回退 CPU
- 文件：`onnxruntime.dll`、`onnxruntime_providers_shared.dll`、`DirectML.dll` 与托管程序集
- 训练权重不提交到 Git；`tools\vision-model-baseline.json` 锁定公开模型资产、大小与 SHA256

增强识别模型由独立的 `PackingProof-Vision` 仓库使用 Ultralytics 训练与导出，
Ultralytics 工具链许可证为 AGPL-3.0。训练仓库负责模型代码、导出参数和权重 Release；
Desktop 只保存下载清单和运行时契约。
