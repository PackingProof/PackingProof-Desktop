# 发布与运行时开发规范

修改发布、启动器、更新、FFmpeg、LibVLC 或 TTS 前必须阅读本文件。通用开发、测试、提交和安全规则仍以仓库根目录 `AGENTS.md` 为准。

## 运行时与发布包布局

- 发布脚本默认同时生成目录包、Setup 和 Microsoft Store MSIX；三份产物必须来自同一次应用负载。完整 7z 与完整 ZIP 仅在分别传入 `-IncludeSevenZip`、`-IncludeFullZip` 时作为本地产物生成，都不上传到 Release。清爽包根目录主要包含 `ExpressPackingMonitoring.exe` 与 `app\`；应用负载、依赖、Web 文件、LibVLC 和 `tools\ffmpeg.exe` 位于 `app\`。Store MSIX 只提交 Partner Center，不上传 GitHub/Gitee Release。
- 运行时数据统一存放在 `%LOCALAPPDATA%\ExpressPackingMonitoring\`。发布包不得包含 `config.json`、`videos.db`、缓存、日志、录像或其他本机状态。
- 共享运行时布局（安装目录下的 `runtime\`）用来让桌面端与 QQ 机器人共用同一份 .NET 运行时：`runtime\dotnet.exe` + `runtime\host\fxr\<版本>\hostfxr.dll` + `runtime\shared\Microsoft.NETCore.App|<WindowsDesktop.App>\<版本>\`，外加记录框架版本、逐文件 SHA256 与老布局迁移映射的 `runtime\layout.json`。自包含应用里的运行时是私有的，别的程序借不到；只有共享框架布局能被多个程序复用。用 `tools\Build-SharedRuntimeLayout.ps1` 生成并自检，`tools\Test-SharedRuntimeLayout.ps1` 可单独复核。
- 应用本身是**自包含发布**（`app\` 里有 `coreclr.dll` / `hostfxr.dll`，运行时平铺在 `app\` 里），包根**不再**放 `runtime\`。这是为了让**任何启动器都能装上增量包**：老启动器只认 `app\` 内的条目、也不认 `root` 落点（见下"升级时序"），而 0.0.82 现场已经证明"先换启动器再装运行时补丁"这条路在老装机上走不通。共享布局由**主程序首次启动时自己落成**：出包时按布局清单生成 `app\runtime-seed\`（布局里自包含产物没有的那几个小文件），主程序启动早期调用 `ExpressPackingMonitoring.UpdateCore\RuntimeLayoutSeeder.cs`，给 `app\` 里已有的运行时文件在安装根目录 `runtime\` 下建**硬链接**（同一份磁盘数据两个名字，不额外占盘；不支持硬链接时退化成复制），逐文件核对 SHA256、整份布局自检通过后才整体改名落地；只在根目录还没有 `runtime\` 时动作，失败就放弃（扩展退回系统运行时）。验证入口：`pwsh -NoProfile -File tools\Test-RuntimeLayoutSeed.ps1`（自包含发布 → 种子 → 落成 → 框架依赖的探针带 `DOTNET_ROOT` 真的从布局加载运行时）。打包脚本会在出包时强校验：自包含特征文件必须在、每个布局文件必须有同名同哈希来源或进种子、种子内容与清单逐文件一致。
- **升级时序与能力协议号**：版本号说明发的哪一版，能力号（`ExpressPackingMonitoring.UpdateCore\LauncherProtocol.cs`）说明**能做到什么**：`patch_package.required_launcher_protocol` 表示"这份补丁至少要能力 N 的启动器才能装"（带 `root=runtime` 载荷的过渡补丁必须 ≥2），`launcher_package.required_app_protocol` 表示"这个启动器至少要能力 N 的主程序才能换"。四个检查点缺一不可：① 主程序准备补丁前发现本机启动器能力不够 → **不下补丁**，先更新启动器（约 3MB）并提示重启（`AppPatchPreparationStatus.LauncherUpdateRequired`）；② 启动器后台下载前不满足 → 不下载；③ 启动器应用 pending 前不满足 → 把这份补丁挪到 `pending-rejected\<版本>` 并明确提示（**不再每次启动失败一遍**，0.0.82 现场就是这样循环的）；④ 主程序替换启动器前发现启动器包要求更高的应用能力 → 不替换。已安装启动器的能力由它自己每次启动写进 `launcher-protocol.json`；文件缺失（0.0.81 及更早从来不写）、内容不合法、或记录的哈希与当前启动器文件不符（刚被安装器换过）都按**最低能力**处理。另外，启动器自更新的"成功检查"缓存必须有复查窗口（`SuccessfulCheckWindow`，当前 6 小时）：0.0.82 现场就是"应用版本没变 + 启动器文件没动"于是**永久跳过**，老机器永远拿不到新启动器，需要新启动器的补丁也就永远装不上。
- **过渡补丁（`root=runtime` 载荷）已退役**：它当初的设想是"主程序先把根启动器换掉（`launcher_package`），新启动器在下次启动时应用带运行时的补丁"。**这条路在老装机上不成立**——启动器自更新只在**应用版本变化后**才重新跑，而应用版本变化又得靠一次能装上的补丁，成了循环；0.0.82 现场因此出现"补丁下好了、老启动器在 `ValidatePatchFiles` 阶段拒绝（`Patch 文件不存在：dotnet.exe`，安全中止、没动文件）、而新启动器永远装不上"的死循环。所以自 0.0.83 起改为**自包含发布 + 主程序自己落成共享布局**（见上），老启动器能直接装新应用的补丁。`-IncludeRuntimeInPatch`、`root` 字段与 `files\.runtime\` 布局、`tools\Test-TransitionPatch.ps1` 都保留可用（协议号机制保证它不会被发给他装不了的机器），但正常发布不再使用；带它出包时必须声明 `required_launcher_protocol` ≥ 2。
- **运行时分界线与补丁基线抬升**：换 .NET 大版本的那一版（**0.0.83**，自包含 .NET 10）是"运行时分界线"，它自己**必须保持 `-PatchBaselineVersion 0.0.18`**，否则 0.0.18~0.0.82 的机器会被判成"低于补丁基线"而拿不到这次升级、白白退回完整包。**0.0.84 同样保持 `0.0.18`**：0.0.83 只发过预览版（启动器还因 AOT 崩溃被下架），把它当基线等于让所有正式版用户都收不到增量包、只能下完整安装包；自包含产物 + 主程序落成共享布局这套形态我们已经实跑验证过任意老启动器都能装补丁（见 `tools\Test-UpgradeFromLegacyLauncher.ps1`）。**0.0.84 之后的下一个正式版（本次 0.0.86）起统一使用 `-PatchBaselineVersion 0.0.84`**；0.0.85 从未正式发布，不单独作为基线版本。0.0.84 及以上直接使用最新增量包；低于 0.0.84 的客户端不会直接失去增量路径：主程序和启动器会沿 baseline 链查找 0.0.84 的 `update_v0.0.84.json` 与 AppPatch，先把 0.0.84 基线补丁放进 pending，重启安装后再继续升级到 0.0.86。只有 0.0.84 的中间增量包不可用时才回退完整安装包。因此 **0.0.84 的 Release、`update_v0.0.84.json` 和 `PackingProof_AppPatch_v0.0.84.zip` 必须保留可下载，不能为了清理旧 Release 把它们删掉**。**主动强制完整包的标志位是 update JSON 里的 `patch_supported: false`**；`required_launcher_protocol` 不是强制完整包，它会先尝试自动更新启动器，只有启动器更新失败才提示重试或改用完整包。打包脚本会拒绝 0.0.86+ 使用低于 0.0.84 的基线。
- 当前应用目标框架与随包运行时基线都是 **.NET 10**（本机实测 10.0.13）。宿主不会用 10 的运行时去跑请求 8.x 的应用（.NET 7 起没有多级回退），所以桌面端与市场里的 .NET 扩展（QQ 机器人）必须**同大版本**升级；升级后布局里的框架目录名会跟着变（`shared\Microsoft.WindowsDesktop.App\10.0.x`）。
- 工具链门槛：`C:\Program Files\dotnet\sdk\<版本>\minimumMSBuildVersion` 决定 IDE 能不能解析这个 SDK，.NET 10 写的是 `18.0.0`（.NET 9 是 `17.12.0`）。所以本机 IDE 必须是 **Visual Studio 2026（18.x）**；VS 2022（MSBuild 17.14）会报"未能正确加载解决方案中的一个或多个项目"，实际是 `MSB4236`/`error : 无法解析 SDK"Microsoft.NET.Sdk"`。命令行 `dotnet build`、`dotnet test` 和发布脚本走 SDK 自带的 MSBuild，不受影响，所以出现这个对话框先看 VS 版本，不要去改 `.sln`。
- 市场里的框架依赖扩展（QQ 机器人）不能用在没有 `runtime\` 的桌面端上：那种机器只能靠系统 .NET，而老桌面端是自包含的、不提供新大版本运行时。所以插件发布时要把 manifest 的 `minPackingProofVersion` 抬到**第一个带 `runtime\` 的桌面端版本**；扩展跟着桌面端抬 .NET 大版本时，门槛要跟着抬到**第一个该大版本的桌面端**。当前 QQ 机器人是 .NET 10，并且配置目录改用扩展 API 规范（桌面端注入 `PACKINGPROOF_EXTENSION_DATA_DIR`、卸载清理 `extension-data` 都从 `0.0.86` 起），所以门槛为 `0.0.86`；0.0.63~0.0.81 的布局是 .NET 8、0.0.82~0.0.84 没有数据目录注入与卸载清理，市场不会把扩展推给这些版本。
- 打包后按这三条核产物：`app\` **有** `coreclr.dll`/`hostfxr.dll`（证明是自包含发布）、`app\runtime-seed\layout.json` 在且种子逐文件与布局清单一致、包根**没有** `runtime\`（共享布局由主程序首次启动落成）；`pwsh -NoProfile -File tools\Test-RuntimeLayoutSeed.ps1` 会一次跑完这几条并验证子进程确实从落成的布局加载运行时。安装器冒烟：`Setup /SILENT /SUPPRESSMSGBOXES /NORESTART /DIR=<临时目录>` 装完确认 `app\coreclr.dll` 在、包根没有 `runtime\`，再用 `unins000.exe /SILENT /SUPPRESSMSGBOXES` 卸载，确认安装目录连同 `app\` 一起清空、且 `%LOCALAPPDATA%\ExpressPackingMonitoring` 未被动过（静默卸载不带 `/EPMUNINSTALLOPTIONS` 不会删设置与录像）。
- `ffmpeg.exe` 可从 `app\tools\ffmpeg.exe`、应用运行目录或系统 `PATH` 解析。AppPatch 不携带 FFmpeg，因此应用逻辑必须兼容用户机器长期保留的旧版本。
- OpenCV 原生库由 `tools\opencv-baseline.json` 固定 OpenCvSharp 提交、OpenCV 文件包、MSVC/SDK 版本和最终 SHA256；`tools\Build-MinimalOpenCv.ps1` 只保留 `core,imgproc` 并生成可复现的 `OpenCvSharpExtern.dll`。**AppPatch 明确排除 `OpenCvSharpExtern.dll`**，老用户继续保留原官方完整库，新安装 Setup 用户先承担精简库兼容验证。Store 是整包更新，没有“保留旧 native”的能力：在 `store_channel_allowed=false` 期间打包脚本禁止生成 Store MSIX，预览版必须使用 `-SkipStorePackage`，避免存量 Store 用户被动切换到未验证的 native；只有真机验证完成且产品接受风险后才可改为 true。启动日志的 `[OpenCV] Native runtime loaded` 会记录 `variant/modules/size/sha256/path`，现场反馈时必须核对。确认精简库长期稳定后，才允许另行决定是否进入 AppPatch 或 Store。
- LibVLC 按 `ExpressPackingMonitoring.csproj` 的显式白名单发布：保留 `filesystem`（本地/映射盘）、`imem`（UNC NAS 通过 `StreamMediaInput` 读取）、解码、解封装、字幕、音视频输出等必要插件；不包含 `smb`、HTTP 等网络访问插件，也不包含推流、复用、服务发现、可视化和 Lua 组件。UNC NAS 回放由 .NET `FileStream` 使用 Windows SMB 凭据，不能把 UNC 直接当 `file://` URI，也不能删除 `imem` 插件。
- Edge TTS 是默认在线语音；Kokoro 模型和运行时默认不随包发布。完整包包含生成的默认 Edge TTS 缓存，AppPatch 必须排除 TTS 缓存。

## FFmpeg 兼容基线

- 正式基线固定为 FFmpeg 4.4.1，以兼容 Win7 老显卡的 NVENC API 11.1（自建精简构建用 nv-codec-headers 11.1.5.2 钉住该 API）。AV1 不作为产品能力，选择 AV1 时回退 H.265；暂不实施 8.0.1 + 4.4.1 双基线。
- 随包二进制优先使用本仓库自建的**精简构建**（`third_party/ffmpeg`，配方含 `build.sh`、`configure-flags.txt`、`versions.env`），以减少安装包体积；这不是第三方开发者的硬门槛。产物 `package/ffmpeg-build/ffmpeg.exe` 由 `tools/ffmpeg-baseline.json` 的 `local_build` 钉住（脚本 + 产物路径 + 尺寸 + SHA256），打包与开发构建经同一 `Resolve-FFmpegBaselineExecutable` 校验。解析优先级：精简身份缓存 → 本地构建产物 → 清单 `package` 段的 Gyan essentials。没有精简产物时允许自动下载上游包并使用其登记的大小和 SHA256 校验，不需要 MSYS2，也不会阻断正常发布。只有在明确希望验证体积优化时才显式传 `-RequireLocalBuild`。构建脚本会覆盖 MSYS2 `x265.pc` 中显式写死的 `-lgcc_s`，避免 `-static-libgcc` 被绕过；实际选中的 FFmpeg 都必须通过 `pwsh -NoProfile -File tools\ffmpeg-capability-check.ps1`，该脚本会先清空 PATH、只保留 Windows 系统目录启动 FFmpeg，再检查能力矩阵。普通开发与 `dotnet build` / `dotnet test` 完全不碰钉住链路：开发构建在基线缓存缺失时回退项目根 `ffmpeg.exe`，运行时再回退系统 PATH，贡献者不需要编译 FFmpeg。自建二进制带 `--extra-version=packingproof-ffmpeg-minimal-V01` 版本戳，诊断日志第一行据此识别构建来源；同版本重出包递增 V 号并同步 baseline 哈希。老用户机器上由历史安装包留下的 Gyan 4.4.1 Essentials 等版本仍登记在 `app_patch_compatible_executables`（AppPatch 从不携带 FFmpeg），保持被识别为兼容环境。
- 高级用户可在 Win8+ 自行替换 `app\tools\ffmpeg.exe`，官方不保证该自定义环境。
- 禁止假设 CLI 参数跨版本通用：FFmpeg 8.x 已移除 RTSP `-stimeout`；4.4.x 的 RTSP `-timeout` 会挂起，因此网络摄像头不传 socket 超时参数，由应用层 15 秒连接超时和断流看门狗兜底。`-fps_mode` 仅 5.1+ 可用，旧版回退 `-vsync passthrough`。网络摄像头输入**不再加** `-fflags nobuffer` / `-flags low_delay`：现场相机（夜间约 19-20fps）抖动后会停在"会话正常、媒体不再送来"，而同一台相机用不带这两个参数的自检/ffmpeg 探测每次都能出帧；打包取证对百毫秒级延迟不敏感。参数策略集中在 `NetworkCameraSource.BuildArguments`。
- 网络摄像头"连上但零帧"按连接失败处理，不能拿 SDP 当"已连接"：SDP 走 RTSP 控制通道，媒体走 RTP，控制通道通了不代表媒体能通。拿到流信息后 10 秒内没有第一帧就换另一种 RTSP 传输方式（tcp/udp）重试，真正出过帧的方式沿用到本次运行结束；网络源按退避持续重试，不会像 USB 设备那样连续失败 5 次就停。判定与重试间隔在 `NetworkCameraRecoveryPolicy`：网络源断帧门限 3 秒（1.5 秒会把 19-20fps 相机的一次小抖动放大成整路重启），重连前等 2 秒让相机回收上一个没有 TEARDOWN 的会话。会话帧数与流信息时刻由 `NetworkCameraSource.FramesReceived` / `StreamInfoReceivedAtUtc` 提供；零帧会话停止时会把 ffmpeg stderr 尾部写进日志。
- 网页转码的编码器不能写死：`h264_nvenc` 只在 NVIDIA 机器上存在，AMD/Intel 显卡和 macOS 每次播放都要先失败一次再回退 CPU，Mac 上更是永远拿不到硬件编码。平台候选与参数集中在 `WebTranscodeEncoderPolicy`，用 `ffmpeg -encoders` 探测一次并缓存，探不到就直接走 `libx264`；硬件参数按编码器分别给（NVENC `-preset p1 -cq 30`、QSV `-global_quality`、AMF `-rc cqp`、VideoToolbox `-q:v 45 -realtime true`），不能跨编码器照搬。
- 修改任何 FFmpeg 调用前，必须使用 `tools/ffmpeg-baseline.json` 锁定的 4.4.1（自建精简构建）和至少一个其他受支持主版本（如 8.0.1）验证受影响流程；同步更新 `NetworkCameraSourceTests` 参数断言和随包 FFmpeg 参数识别测试。切换随包二进制或重建精简版时，先对产物跑一遍能力矩阵核对（编码器/协议/滤镜），确认应用实际用到的能力都在。
- macOS 不自带 FFmpeg（`avconvert`、`qlmanage`、`sips` 都不是 ffmpeg CLI 的替代品），因此 Mac 主机必须随包携带二进制：基线锁在 `tools/ffmpeg-macos-baseline.json`，用 `tools/Prepare-PinnedFFmpegMac.sh` 下载校验到 `package/dependency-cache/ffmpeg-macos/<版本>/`，`tools/Publish-MacHost.sh` 再拷进 `Contents/MacOS/Host/tools/ffmpeg`。缺这个文件时缩略图、预览和网页转码会全部失败，网页会显示"编码未知"并回退直接播放原片。
- macOS 基线固定为 Martin Riedl Build Server 的 arm64 9.0.2 静态 GPL 构建：原生 arm64、只依赖系统框架、上游已用 Developer ID 与加固运行时签名，且未启用 `--enable-nonfree`（可随包分发）。版本 ≥5.1，因此 Mac 主机始终走 `-fps_mode passthrough` 分支。

## 启动器与增量更新

- 用户应从包根目录启动器进入。启动器立即启动主程序、后台检查并下载经过校验的 AppPatch 到 `cache\updates`，下次启动时安装。
- 增量包下载带断点续传：主程序与启动器共用 `ExpressPackingMonitoring.UpdateCore\ResumableFileDownload.cs`，半截包按版本留在 `cache\updates\partial\*.partial` 里，断线、停滞、换源（GitHub ↔ Gitee）、跨重启都带 `Range` 从断点继续。服务端不认 `Range`（回 200/416）就截断重下，绝不拼两段不同来源的字节；最终仍要过体积 + SHA256 双校验才发布到 `pending`，校验不过的断点会被清掉并只重下一次。下载停滞判据是"60 秒没有新字节"而不是绝对时长：主程序端增量包不再用清单那 75 秒的 `HttpClient` 总超时（否则 75MB 的过渡包在慢网上正好卡在下到一半），启动器的下载也单独给 30 分钟预算；超时保留断点，重试从断点继续。
- 启动器在启动主程序前处理共享运行时布局：安装目录里有可用的 `runtime\` 就把子进程的 `DOTNET_ROOT` 指向它（框架依赖的主程序因此用安装自带的运行时，不依赖机器另外装的 .NET）；没有或不完整就保持原样，走系统运行时，兼容 Gitee 的 no-runtime 安装包。老装机升级上来时 `app\` 里可能还留着旧的平铺运行时副本，其中 `app\hostfxr.dll` 会改变宿主行为：启动器按 `runtime\layout.json` 的 `appFileMap` 把其中**当前版本应用自己不再声明为依赖**的那批搬进 `runtime\.legacy\` 留作回滚（判定用 `app\` 里的 `*.deps.json` 的 runtime/native 段）。**实测边界**：自包含发布把整套运行时都声明为应用自身依赖，所以自包含形态的老装机只搬个别文件（用 0.0.81 完整包升 0.0.83 实测搬 1 个：`WindowsFormsIntegration.dll`），不会再出现"整套 140MB 平铺副本被搬走"；框架依赖形态的老装机才会搬走整套平铺副本。回滚备份在启动器**连续两次处理布局**之后清理（计数写在 `runtime\layout-state.json`，只数"主程序没在跑时启动器处理了一次"，不判断主程序这次有没有正常跑完）；主程序正在运行时只解析布局、不动任何文件。另外，过渡补丁如果被**老启动器**应用，整套运行时会落在 `app\runtime\`（老版本把所有条目都按 `app\` 解析），框架依赖的主程序找不到运行时；启动器在根目录还没有 `runtime\`、且 `app\runtime\` 里确实是一份完整布局时把它搬回根目录，用户重启一次即可自愈。验证入口：`pwsh -NoProfile -File tools\Test-SharedRuntimeLaunch.ps1`（含"运行时装在 `app\runtime\`"这一场景）。
- 主程序可通过独立校验的 `launcher_package` 更新根启动器：等待旧进程退出、持有共享更新互斥体、只替换标准根启动器，失败时恢复旧文件。
- AppPatch 是固定基线累计补丁：0.0.83/0.0.84 历史发布使用 `0.0.18`，**0.0.86 及以后正式版统一使用 `0.0.84`**（0.0.85 未发布）；启动器基线与 AppPatch 基线相互独立。不要沿用旧示例里的 `0.0.18`。低于新基线时客户端会优先查找中间基线补丁分步升级；对应的中间版本 Release 与 AppPatch 必须保持可下载。
- 增量清单的文件条目支持可选 `root` 字段（缺省 `app\`，另一个允许值是 `runtime`），启动器按它决定落点，去重键是「位置 + 相对路径」，因此同一相对路径可以分别出现在两个位置；`tools\Apply-AppPatch.ps1` 用同一套语义，备份也按位置分开。只有过渡补丁会写这个字段，普通增量包不带。
- AppPatch 默认只新增或覆盖清单中的文件；要从安装目录移除文件必须走清单里的**清理列表**（自 0.0.83 起实现）：出包时按"基线目录里有、当前发布目录里没有"算出候选，再与累计的**退役清单** `tools\app-patch-retire-list.json` 合并，写成清单里的 `deletes`（`{"path": "..."}`，可带 `root`）。安装端（启动器的 pending 安装与手动 `apply_app_patch.ps1` 两条路径行为一致）**先备份再删、失败整体回滚**，删完收掉被清空的目录，并拒绝同一路径既写入又清理。白名单不删也不登记：`tts_cache\`（缓存）与用户可替换的 `ffmpeg.exe` / `libvlc\`；只允许删 `app\`（或 `runtime\`）内的文件。
- 载荷白名单在**打包侧与安装侧各校验一遍**，两侧规则必须同步：写入或清理 `tools\ffmpeg.exe` / `libvlc\` 一律拒绝，清理列表另外不得包含主程序核心文件（`ExpressPackingMonitoring.exe` / `.dll` / `.deps.json` / `.runtimeconfig.json`）与 `runtime-seed\`。安装侧的实现在 `ExpressPackingMonitoring.UpdateCore\AppPatchPayloadPolicy.cs`（启动器用）与 `tools\Apply-AppPatch.ps1` 的 `Get-AppPatchPayloadViolation`（手动安装器用），两者由 `AppPatchPayloadPolicyTests` 用同一批样本做一致性回归；打包侧是 `tools\AppPatchRuntimeCompatibility.Common.ps1` 的 `Test-IsAppPatchManagedRuntimePath` 与 `New-AppPatchPackage` 的清单守卫。放行一条就可能删掉用户自己换过的 ffmpeg，或者删掉运行时种子导致共享布局再也落不成。
- 退役清单是**累计、只增不减**的：清理列表按"基线 − 当前"算，而基线每换一次大版本就会抬升，上一代漏清的残留（老启动器不认 `deletes`、或用户走手动安装）不会再次出现在差集里。所以出包脚本会把新算出的条目并进 `tools\app-patch-retire-list.json` 并写回，**发布时必须把这个文件的变化一起提交**，后面的补丁才会继续带着这些老路径。**边界**：只有认得 `deletes` 的启动器（0.0.83 重建基线起）会执行清理，更早的启动器只处理 `files`（有意的预期边界，不是缺陷）。功能迁移不得把安装目录残留文件视为用户仍在使用该功能。
- 启动器基线由 `tools/launcher-baseline.json` 锁定。普通应用发布复用锁定字节，不重建或重复上传 LauncherPatch。启动器逻辑输入变化时运行 `tools/Publish-LauncherBaseline.ps1`、提交新锁并创建普通 `launcher-vX.Y.Z` 标签，不为该组件标签创建 Release。
- 启动器的 AOT 构建产物不可复现（同一份源码、同一台机器多次构建的哈希都不相同），所以 `tools/launcher-baseline.json` 里的哈希必须与那一次构建产出的 `PackingProof_LauncherPatch_v*.zip` 成对保留：基线脚本产出的补丁包要就地留着，打包时用 `-LauncherBaselinePackagePath` 指给它，不要重新构建一份来对齐锁。重建基线会改变锁与补丁包哈希，需要重新提交锁并移动组件标签。
- AppPatch 与 LauncherPatch 是两个独立 ZIP，各自带手动安装器和说明；AppPatch 绝不能包含启动器。
- 更新地址通过环境变量或 `.env` 配置；默认检查 GitHub latest release API。

## 打包与发布流程

- **独立评审门槛（打包/发布前先自我评估改动量）**：先按**本次发布的完整改动范围**（上一个正式版标签..`HEAD`）算一次量，再决定要不要发起外部独立评审。
  - 可以不评审：改动很小且不碰关键路径。经验线是「改动文件 ≤ 5 个、代码净增删 ≤ 50 行」，并且**没有**落在下面任一类关键路径上。
  - **必须发起外部独立评审**：超出上面任一条件（0.0.82 + 0.0.83 这一批就是典型），或者只要**触碰**下列范围，无论行数多少：
    1. 更新、自更新、根启动器、补丁格式/清单字段（含 `deletes`、`root`、能力协议号）、打包与发布脚本；
    2. 录像停止/收尾、FFmpeg 生命周期与参数、存储选择/删除/归档、备份与恢复、跨盘导入；
    3. 扩展 API 的授权、路由、协议与市场分发。
  - **强制重评**：一个发布周期内出现过"上线后被现场打回"的缺陷时，下一次发布前必须对同一范围重新做独立评审。
  - 自评结论（量级、是否触发评审、覆盖范围）要在动打包之前报告给用户。
- **独立评审的规矩**（不是走过场）：
  - **不带上下文**：评审者不继承对话，只给它一份自包含的评审文件（仓库、分支、评审范围、要核对的契约、可跑命令、输出格式），并要求它自己看代码、自己跑命令。
  - **范围是完整差集**：`git log --oneline <上个正式版标签>..HEAD` 与 `git diff <上个正式版标签>..HEAD`，不是只看最后一次提交。
  - **按风险分域、可以并行**：更新/自更新/打包、录像/摄像头/交付、存储/卸载/扩展 API 各指定评审者；改动量大时并行多个。评审者只读、不改仓库文件，允许跑构建、测试与真机脚本。
  - **结论要可落地**：每条发现给严重度（阻断/重要/次要）+ `file:line` + 证据或复现命令；明确列出"未能验证"的部分；不接受"看起来没问题"。
  - **阻断项修完要复评**：修复本身不小的话，对修复再做一轮（可以只针对修复范围）。

- macOS 包（`tools/Publish-MacHost.sh`）产出 `.app` 与 `PackingProof-macOS-<版本>.dmg`；设置 `SIGN_IDENTITY` 时走 Developer ID + 加固运行时并在 DMG 与 .app 上装订公证票据。macOS 不做增量补丁：逐文件替换会破坏签名与公证，升级一律整包替换（下载 DMG、拖进应用程序）。Mac 端只通过 `UpdateCheckService` 检查并提示新版本，不在应用内替换自身。

- macOS 包只能在 Mac 上构建（swift + codesign），Windows 侧发布脚本够不到，所以用 `tools/Publish-MacRelease.sh [版本号] [github|gitee|both]`：校验工作区干净且标签指向当前提交 → 构建并签名/公证 DMG → 挂到**同一个 `vX.Y.Z` Release**（不另开 mac 标签）→ 在 `update_v<版本>.json` 里补 `platforms.macos`（只作下载入口与人工核对）。版本号两个平台共用；**更新检查按"这个 Release 里有没有本平台的发布资产"挑版本**：macOS 只看 DMG，Windows 只看 `update_v<版本>.json`，所以只修 Windows 的版本 Mac 端会自动跳过，只发 macOS 的版本 Windows 端也会自动跳过（应用内检查与启动器自动检查都按这条挑，`UpdateReleaseSelection` 是唯一判定处）。单独发 macOS 时必须打对应的 `vX.Y.Z` 标签；只发 Windows 时无需任何额外操作。
- **预览版（prerelease）默认不推送给客户端**：标了预览版的版本会被正式机器整条跳过，只认最高的正式版；只有测试机才会照常收到它：在 设置 → 高级设置 → 接收预览版更新 里打开即可（应用内检查与启动器读同一份应用配置），也可以设环境变量 `UPDATE_ALLOW_PRERELEASE`（1/true/yes/on，优先级更高，便于临时覆盖）。草稿（draft）任何情况下都不推送。所以"先标预览版发给内部测、测完去掉预览标记"是安全节奏：正式机器在去掉标记前不会升上去。渠道开关与判定在 `UpdateChannelPolicy` / `UpdateReleaseSelection`，应用内检查、启动器自动更新、启动器包更新三处共用同一份逻辑。

- 发布版本维护在仓根 `Directory.Build.props` 的 `<Version>`（所有工程继承同一份，工程文件不得自己再写 `<Version>`；`ProjectVersionSourceTests` 守这条），并与 `vX.Y.Z` 标签一致。之所以收敛成一处：应用显示版本、更新判断读的是 `Config/AppVersion.cs`，而它跟 `Logging/RuntimeLog.cs` 一起被编进 **Core** 程序集，Core 与主程序各写一份版本时开发构建会读到旧号（0.0.79 之后界面一直显示 0.0.78，Mac 打包也拿 Core 的版本号），正式包因为发布脚本全局覆盖 `InformationalVersion` 才看不出来。对应版本标签位于 `HEAD` 且工作区干净时，正式产物和 `InformationalVersion` 只使用纯版本号；未打对应标签的测试包使用 Git 标准的 `-<距最近标签提交数>-g<短CommitID>` 后缀，脏工作区再追加 `-dirty`。AppPatch、更新清单和包内协议版本始终使用纯语义版本，完整 Commit ID 继续写入程序集元数据。基线、完整包和 AppPatch 必须复用同一次发布生成的主程序文件，保证测试包身份可追溯且不影响更新比较。
- 代码改动一律走远程 PR，只有发布准备阶段仅更新 `Directory.Build.props` 版本号的 chore 可以直接提交到 `main`；该提交不得夹带代码、配置、测试或文档修改。提到哪个平台按问题来源决定：我们自己发现的 **bug** 先在 GitHub 开 issue，再提 PR 并在说明里关联那个 issue；性能、功能、工具、文档这类**不是 bug** 的改动直接提 PR，不必为了留痕再补一个 issue。别人在某平台提的 issue，PR 就提到那个平台（Gitee 的 issue 提 Gitee PR，GitHub 的 issue 提 GitHub PR）。PR 默认两边都提；目标可以用仓库根目录 `.env` 的 `PR_TARGET_HOST`（`gitee` / `github` / `both`，默认 `both`）或命令行 `-Target` 覆盖。用 `pwsh -NoProfile -File tools\Submit-ChangePr.ps1 -Title "<PR 标题>" [-Merge] -Confirmed` 推送分支、创建 PR，用快进合并（提交原样保留）、把主干对齐到同一个提交；**不加 `-Confirmed` 只打印计划并拒绝执行**，提交 PR 前必须先经人工确认。
- 发布顺序固定为：在功能分支提交并保持工作区干净 → 运行本地 CI → 提 PR 并合并到主干（快进合并，提交原样保留，不改写 SHA，不 squash）→ 同步主干 → 在合并后的提交上创建本地 `vX.Y.Z` 标签 → 先按 `docs/development/RELEASE_CHECKLIST.md` 做变更审计（竞态/性能/关键路径），再以该标签身份执行一次 Release 构建、全量测试、自动验收、打包和产物校验 → 只推送该标签到 GitHub/Gitee → 创建 Release 并上传已校验产物 → **把同一次构建出的 Microsoft Store MSIX 提交 Partner Center**。**Gitee 安装包按完整 Setup 大小分流：严格小于 100MB 时直接上传 `PackingProof_Setup_vX.Y.Z.exe`，否则再生成并上传 no-runtime 安装包**（`pwsh -NoProfile -File tools\Publish-NoRuntimePackage.ps1 -Tag vX.Y.Z -UploadGitee`；该脚本也会识别小体积完整 Setup 并直接复用）。macOS 包由 Mac 侧 `tools/Publish-MacRelease.sh` 挂到同一个 Release。标签必须指向已在主干上的提交且先于正式构建：既避免先构建测试身份再为正式标签重复编译，也避免 PR rebase 之后标签悬空。
- 打包脚本会在产物目录生成 `release_commits_v<X.Y.Z>.txt`（上一个正式版以来的全部提交，仅本地核对、不上传），并按需生成或保留 `RELEASE_NOTES_v<X.Y.Z>.md`；重新打包不会再冲掉已经写好的发布笔记。
- 发布脚本 `tools/Publish-Releases.ps1` 属于门禁的一部分：它校验发布笔记的分段与占位符、校验 `update_v<X.Y.Z>.json` 的 `title` 与 `notes` 是否已填写，并在打印提交清单后要求显式传入 `-ConfirmCommitCoverage`。只想自检用 `-ValidateOnly`，已经发布过的版本要补正文用 `-UpdateNotes`（只更新正文与标题，不重复上传附件）。
- 本地 CI 命令为 `pwsh -NoProfile -File tools/Test-CI.ps1`，它与 `.github/workflows/ci.yml` 保持同一还原、构建、单元测试和 JavaScript 语法检查门禁。发布前必须先通过本地 CI，再运行 `tools/Test-Release-Automated.ps1`；任一失败都不得推送标签或发布。
- 开始构建前先跑 `pwsh -NoProfile -File tools/Check-ReleasePrereqs.ps1` 自检本机发布条件（工作区、标签与版本一致性、启动器基线与逻辑输入指纹、gh/gitee 登录态、dotnet、7-Zip、Inno Setup）。只读检查，不构建、不上传、不打印凭据。启动器逻辑输入变化时它会直接要求先重建基线，避免打包到一半才返工。
- `.github/workflows/release-package.yml` 只响应 `v*.*.*` 标签或手动触发，不再响应普通 `main` push。GitHub 侧仅对已在本地通过门禁的标签执行一次发布包构建，避免每次提交都耗电打包。CI 配置 `STORE_IDENTITY_NAME` / `STORE_PUBLISHER` Secrets 时会一起构建 Store MSIX；未配置时只构建 Setup，并在日志里明确标记该产物不是正式发布包。
- 发布工作流不需要额外 FFmpeg Secret。仓库不提交 54MB 精简产物，标签构建会优先使用缓存或本地产物；没有时按清单中的固定 HTTPS 来源下载 Gyan essentials，并校验大小和 SHA256。第三方开发者也因此可以直接构建和出包，只需承担更大的包体积。
- 推荐运行 `打包脚本-增量.bat v<X.Y.Z>`。直接调用时使用；正式发布默认同时构建 Store MSIX，只有本地验证才允许加 `-SkipStorePackage`：

```powershell
pwsh -NoProfile -File tools\Publish-CleanPackage.ps1 -Version <X.Y.Z> -PatchBaselineVersion <基线版本> -BaselineAppDir "package\PackingProof+v<基线版本>\PackingProof+v<基线版本>\app"
```

- `<基线版本>` 在 0.0.86 及以后固定为 `0.0.84`；0.0.83/0.0.84 历史发布才使用 `0.0.18`。`tools\打包脚本-增量.bat` 与 `tools\Publish-CleanPackage.ps1` 的默认补丁基线都必须是 `0.0.84`，不要再复制旧命令里的 `0.0.18`。
- `-BaselineAppDir` 必须指向真实固定基线的 `app` 子目录并包含 `tools\ffmpeg.exe`。脚本从目录解析实际基线，并强制与更新清单和补丁清单一致，禁止手工伪造。
- Store MSIX 复用同一次 Release 的 `app\` 负载构建，不重新 `dotnet publish`；本地身份取 `tools\store-package.local.json`，缺身份或 SDK 工具时 `Check-ReleasePrereqs.ps1` 会提前阻断。产物目录同时生成 `store_package_v<X.Y.Z>.json`，记录 MSIX 的身份、版本、大小、SHA256，以及 Setup `app\` 的逐文件指纹；`Publish-Releases.ps1` 会解开 MSIX 与本次 Setup 负载逐个文件比对，旧 MSIX 或旧清单不能借同版本号通过门禁。
- 产物根目录同时生成 `package_integrity_v<X.Y.Z>.json`，记录 Setup 的文件名、大小、SHA256 和本次 exact `app\` 的逐文件指纹；`Publish-Releases.ps1` 会校验 Setup 未被替换，并要求 Setup 与 Store MSIX 指向同一份 exact 应用负载。
- `-ReuseExistingLauncherBaseline` 只用于同一发布标签重发；普通新版本不传。
- 正式标签构建通过后，只把 `vX.Y.Z` 标签推送到 GitHub 与组织 Gitee 仓库 `PackingProof/PackingProof-Desktop`（`main` 已在 PR 合并时更新，不再直接推送提交），然后创建 Release；同一版本的 Store MSIX 必须在 Partner Center 完成提交，才算这次发布完成。禁止普通 `main` push 触发发布包工作流。
- 发布前执行 `pwsh -NoProfile -File tools/Test-Release-Automated.ps1`。不得在未完成真实设备检查时传 `-ConfirmManualCoreChecks`。
- 采集链路的现场结论（走哪条后端、GPU 转换有没有真生效、每帧整帧拷贝要花多少）用 `pwsh -NoProfile -File tools/Diagnose-CameraPipeline.ps1` 一次跑出来：报告与探针日志写到输出目录，只读配置、录像和现场 `runtime.log`。整帧拷贝那一段不需要摄像头、也不用退出主程序，随手可跑；GPU/CPU 对照要独占摄像头，脚本会提示先关程序。
- `docs/development/RELEASE_CHECKLIST.md` 是发布前 AI/自动化照着跑的清单（命令与产物校验）；需要人和设备才能做的现场检查在 `docs/development/RELEASE_FIELD_CHECKS.md`，发布时提醒执行但不阻断，也不用写进发布说明。
- **打包前先审计**上一版本以来的完整变更，追踪录像、更新、授权、备份、删除和文件替换等关键路径；自动测试通过不能替代这一步。可信的正确性、数据安全、兼容性、性能或竞态问题均阻断发布，除非用户明确接受记录在案的例外。

## 发布笔记与资产

- 发布笔记必须使用 `docs/development/RELEASE_NOTES_TEMPLATE.md`，并**逐条**核对 `release_commits_v<X.Y.Z>.txt` 里的全部提交（等价于 `git log --oneline <上一正式版标签>..HEAD`）。按“功能与体验 / 问题修复 / 兼容与工程”填写，覆盖所有用户可见变化；纯工程或测试提交也要在《兼容与工程》里落到文字，不能只写几条最重要的就交付。《问题修复》只写**上一个正式版就存在**的缺陷；本版自己引入又在本版修掉的回归不算问题修复（对用户来说那只是新功能还没定稿），把最终行为并进对应功能的《功能与体验》，纯内部返工不写。
- 发布笔记写到该版本自己的产物目录 `package/PackingProof+v<X.Y.Z>/RELEASE_NOTES_v<X.Y.Z>.md`，在打包生成产物目录之后写入。禁止放在仓库根目录，也禁止提交进仓库；`package/*` 已被 Git 忽略。打包脚本会在文件不存在时按模板生成骨架，在文件已存在时原样保留，正常流程不需要手工改文件名。
- 标题固定为 `v<X.Y.Z> <一句话内容>`，且这句话必须点出本版本最核心的变化（本版投入最大的功能，或用户最痛的问题），例如「采集预览迁移 GPU 与存储判定重做」；只写版本号或只写“体验优化”都会被发布脚本拒绝（未传 `-Title` 直接报错）。
- 《下载与更新说明》必须与本次实际上传的资产逐条对齐：GitHub 的 Setup、Gitee 直接上传的完整 Setup 或 no-runtime 安装包（按 100MB 阈值二选一）、Microsoft Store 的 MSIX、macOS 的 DMG、建立新启动器基线时的 LauncherPatch；少写一项就属于发布不规范，多写未上传的资产、或写已经不存在的分发方式（例如整包 ZIP 免安装包）同样算错。
- 条目顺序按重要性从高到低：本版重点功能 → 重要修复 → 小优化与文案调整；`update_v<X.Y.Z>.json` 的 `notes` 保持同一顺序。同一件事（同一模块的默认值、迁移、提示、界面入口等）必须合并成一条，不允许拆成多条重复描述。预览版需在 GitHub 与 Gitee 标记 prerelease；预览版只写本次增量，正式版汇总上一正式版以来所有预览版。
- `update_vX.Y.Z.json` 的 `title` 与 Release 标题完全一致，`notes` 必须已经填写，发布脚本会直接拒绝“请填写更新标题”这类占位内容。`notes` 是供启动器直接显示的纯文本字符串数组，每项只写一条简洁、用户可见的变化；禁止 Markdown 标题、列表减号、序号、换行排版和“下载某某包更新”等说明。`notes` 按模块归并、控制在 15~20 条以内，但必须覆盖本版本全部用户可见变化，不能只写几条最重要的；超过 20 条发布脚本会拒绝。
- `update_vX.Y.Z.json` 的 `download_source` 控制增量包默认从哪个平台下载：`github`（出包缺省，便于观察 Release 附件下载量）、`gitee`（国内网络或大补丁可显式指定）或 `auto`（跟随更新清单来源）。无论选哪个，GitHub 连续下载失败后都会自动回退 Gitee。出包时通过 `Publish-CleanPackage.ps1 -DownloadSource github|gitee|auto` 写入。
- `notes` 面向的是店员等最终用户，只写他们能感知的变化。工程与内部改动一律不写，例如运行时版本、打包与增量包机制、CI 门禁、代码重构、测试补充；这些留在发布笔记的“兼容与工程”里。
- `notes` 每条要短：启动器会折行，但条目过长折成多行后整页看起来又乱又难读。单条建议 20~35 字，硬上限 40 字（含标点），发布脚本会拒绝超长条目。
- 不生成 AppFull 或 ManualUpdate，不上传旧名 `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`。`launcher_manifest` 和 `release_info` 仅作本地校验交接，默认不上传。

| 目标 | 上传资产 |
| --- | --- |
| GitHub | Setup、update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch；macOS 包 `PackingProof-macOS-<版本>.dmg`（在 Mac 上用 `tools/Publish-MacRelease.sh` 上传） |
| Gitee `PackingProof/PackingProof-Desktop` | update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch；完整 Setup 严格小于 100MB 时直接上传 `PackingProof_Setup_vX.Y.Z.exe`，否则由下面的 no-runtime 包补安装入口；macOS 包 `PackingProof-macOS-<版本>.dmg` 同上 |
| Gitee no-runtime（仅完整 Setup 达到 100MB 时） | `PackingProof_Setup_no-runtime_vX.Y.Z.exe`：不含 .NET 运行时的安装向导，给只能下载 100MB 以内附件的 Gitee 用户使用。用 `pwsh -NoProfile -File tools\Publish-NoRuntimePackage.ps1 -Tag vX.Y.Z -UploadGitee` 生成并上传；根启动器是 AOT 原生、直接复用正式包里的那一份，`app\` 由该脚本**自己**跑一次 `dotnet publish --self-contained false` 得到（不能再复用正式包的 app，那是自包含的、会把运行时也带进去），并从正式包里补上 `tools\ffmpeg.exe`、WinTts 程序集与 `tts_cache`（这类安装靠机器上的 .NET 10 Desktop Runtime 跑，也没有种子可落共享布局，扩展直接走系统运行时）。AppPatch 本身是自包含形态，这类目录吃到补丁后会变成自包含，往后一切正常。默认不上传 ZIP 免安装包（`PackingProof_no-runtime_vX.Y.Z.zip`），确有需要时加 `-IncludeZip` |
| Microsoft Store / Partner Center | `PackingProof_Store_vX.Y.Z.msix`：与 Setup 共用同一次 `app\` 负载，身份/版本/哈希记录在 `store_package_vX.Y.Z.json`；只提交 Partner Center，不上传 GitHub/Gitee Release。正式发布必须提交，不能把它当成可选的旁路产物 |

- 完整 7z 与完整 ZIP 都不再上传到任何 Release，默认也不生成，仅在本地确有需要时分别传入 `-IncludeSevenZip` 和 `-IncludeFullZip`。对外分发只有 Setup（Gitee 侧在完整 Setup 小于 100MB 时直接复用，否则换成 no-runtime 安装向导）、AppPatch、update JSON 与 macOS DMG；普通用户通过启动器自动更新，无需手动下载清单。

- Microsoft Store 包与 Setup 包是两套独立安装：商店包不包含 `AppPatch`/`LauncherPatch`，`Publish-StorePackage.ps1` 会写入 `PackingProof.PackageEdition.json` 标记并拒绝夹带 Setup 更新脚本；手动补丁脚本检测到 `WindowsApps`、`AppxManifest.xml`、商店包标记或商店版 `LocalState` 配置时直接拒绝，不允许把补丁写进 MSIX 包目录。两版数据目录分开，更新和卸载互不覆盖。
- `runtime-seed` 只服务 Setup 版的扩展市场（框架依赖扩展通过共享 `runtime\` 复用 .NET）；Store 版以 MSIX 包身份启动时会跳过运行时落成，因此 `Publish-StorePackage.ps1` 会从商店 payload 移除 `runtime-seed`，同源校验对这一段做显式排除。共享布局同时排除已退役的 WinForms 与 `WindowsFormsIntegration` 组件。
- 自动化隔离使用 `EPM_SKIP_LEGACY_INSTANCE_LOCK` 和 `EPM_HOST_LEASE_SCOPE`；`EPM_INSTANCE_SCOPE` 只隔离同版本实例锁，不再改变保存主机锁的作用域，避免测试作用域让两个实例同时成为主机。
- Gitee 发布令牌固定取仓库根目录 `.env` 的 `GITEE_TOKEN`，由脚本注入 `GITEE_TOKEN` 环境变量后交给 CLI；CLI 自己保存的登录态只作回退，而且它按身份字符串各存一份、`gitee auth status` 在令牌失效时仍返回 0，不能用来判断可用性。发布时对 `PackingProof/PackingProof-Desktop` 执行 `gitee release create --repo PackingProof/PackingProof-Desktop --target main` 和 `gitee release upload`；不再向旧个人仓库发布。
- 两个平台的 Release 可由 `pwsh -NoProfile -File tools/Publish-Releases.ps1 -Title "<一句话内容>" -ConfirmCommitCoverage [-Prerelease]` 一次创建，发布笔记默认取产物目录里的 `RELEASE_NOTES_v<X.Y.Z>.md`（要指别处才传 `-NotesFile`）。脚本要求工作区干净、当前提交有精确 tag，并确认发布笔记已覆盖 `release_commits_v<X.Y.Z>.txt` 的全部提交后才按上面的资产表挑文件上传，GitHub 创建失败会自动重试；已发布版本补写正文用 `-UpdateNotes`，只校验不发布用 `-ValidateOnly`。GitHub 登录态由 gh CLI 维护，Gitee 令牌由脚本从 `.env` 读取后注入环境变量，不打印、不落盘、不提交。

## 重发同一版本（热修已发布版本）

正常发布跑：`Publish-CleanPackage.ps1`（同时出 Setup 与 Store MSIX）→ `Publish-Releases.ps1`（建 Release 传 Git 资产并校验 Store 包）→ Partner Center 上传同版本 MSIX。完整 Setup 严格小于 100MB 时，`Publish-Releases.ps1` 会直接把它传到 Gitee，不再需要额外的 no-runtime 步骤；达到 100MB 时才追加
`Publish-NoRuntimePackage.ps1 -Tag v<X.Y.Z> -UploadGitee`（Gitee no-runtime 包）。
**给已经发出去的版本补修复**是另一件事，已经收敛成一条命令（重建 → 替换两边资产 → 按尺寸补齐 Gitee 安装入口）：

```powershell
pwsh -NoProfile -File tools\Republish-SameVersion.ps1 -Version <X.Y.Z> -Title "<一句话内容>" -ConfirmCommitCoverage -Confirmed
```

重发会覆盖两个平台上已发布的资产，属"对外动作"：不加 `-Confirmed` 只打印计划并停下，`-DryRun` 只看计划不报错。
脚本内部按下面的顺序执行，出问题就在对应那步停下：

1. 发布标签必须指向已合并到主干的提交，且修复在里面；HEAD 可以比标签多提交，但只允许工具与文档差异 ——
   应用侧代码（`ExpressPackingMonitoring*`、`Installer`）与标签不一致时脚本直接拒绝，避免发出去的包不是标签内容
2. **清掉产物目录再全量重打**：`Publish-CleanPackage.ps1` 自己会重建产物目录，并保留人工写好的发布笔记。
   脚本结尾有"产物新鲜度守卫"：`app\ExpressPackingMonitoring.dll` 早于应用侧最新源码时直接报错，
   所以复用旧产物这条路已经被堵住（见下方"内容守卫"）
3. 打包结束后核对三处：产物目录里有没有 `PackingProof_LauncherPatch_v<X.Y.Z>.zip`（若本版建立新基线）、
   `RELEASE_NOTES_v<X.Y.Z>.md` 是不是自己写的那份（不是骨架）、`update_v<X.Y.Z>.json` 的 `title`/`notes`/
   `launcher_package` 是不是本次的值
4. 两边替换资产：`Publish-Releases.ps1 -UpdateNotes -ReplaceAssets` —— GitHub 用 `gh release upload --clobber`；
   Gitee 先删同名旧附件再上传（Gitee 不会覆盖同名附件）；同一命令会校验本次重建出的 `store_package_v<X.Y.Z>.json` 与 MSIX
5. Gitee 安装包：完整 Setup 严格小于 100MB 时由 `Publish-Releases.ps1` 直接上传并删除旧的 no-runtime 安装包；达到 100MB 时才重做 `Publish-NoRuntimePackage.ps1 -Tag v<X.Y.Z> -UploadGitee`（上传前同样先删同名旧附件）
6. Microsoft Store：把本次重建的 `PackingProof_Store_v<X.Y.Z>.msix` 重新提交 Partner Center，不能沿用旧包
7. macOS 侧：在 Mac 上 `SIGN_IDENTITY="Developer ID Application: ..." NOTARIZE=1 tools/Publish-MacRelease.sh <版本> both`。
   **签名必须在 Mac 桌面会话的终端里跑**：SSH 会话拿不到登录钥匙串里的签名私钥，会报 `errSecInternalComponent`

产物目录名带提交后缀（`-2-g<sha>`、`-dirty`），标签外多提交一次、甚至工作区变脏就会换目录名。
所以发布笔记不按目录写死：打包时会在本次产物目录、`package\.release-notes\` 和同版本历史产物目录里
找最新一份人工正文（模板骨架不算），保留时再同步一份到 `package\.release-notes\` —— 换目录名重打也不会
把写好的笔记换成模板

## 发布内容守卫（防止"看着是新的、内容是旧的"）

历史事故：源码已改，产物目录里的 app 负载仍是上一次的旧文件，脚本只报"成功"，于是发出去的包
内容仍是旧的。现在发布链路上有三道内容校验，任何一道不过都直接失败：

1. **产物新鲜度**（`Publish-CleanPackage.ps1`）：`app\ExpressPackingMonitoring.dll` 不得早于应用侧工程
   里最新的 `.cs`/`.xaml`，比源码旧就要求清理产物目录后重新打包
2. **产物一致性**（`Publish-Releases.ps1`）：解开 AppPatch，与产物目录里的
   `ExpressPackingMonitoring.dll` / `ExpressPackingMonitoring.UpdateCore.dll` 做 SHA256 比对，不一致停止发布
3. **启动器补丁存在性**（`Publish-Releases.ps1`）：更新清单声明本版提供 LauncherPatch
   （`launcher_package.version` == 本版版本）时，产物目录里必须有对应 zip，否则停止发布

排查现场"更新不到新版本"时，先用字符串标记确认包内容而不是看时间戳：例如本次修复会在
`ExpressPackingMonitoring.UpdateCore.dll` 里留下 `release list scanned`，而 `per_page=30` 是旧逻辑特征
（Windows 的 AppPatch、macOS 的 DMG 都可以这样验）。这一步已沉淀成可复用工具：

```powershell
pwsh -NoProfile -File tools\Test-PackageContent.ps1 -PackagePath package\PackingProof+v<X.Y.Z>\PackingProof_AppPatch_v<X.Y.Z>.zip -Require "release list scanned" -Forbid "per_page=30"
```

zip 会自动解开、目录会递归扫 `dll`/`exe`；退出码 0 通过、1 不通过，可直接接进发布门禁或人工核对。

发布出去之后还要对一遍"用户到底看不看得见、下不下得动"：

```powershell
pwsh -NoProfile -File tools\Test-UpdateReachability.ps1 [-Version <X.Y.Z>] [-AllowLegacyBlind]
```

它对着线上两个平台的 release 复盘：目标版本两边是否都带 `update_v*.json` 与 AppPatch、清单里的
`size`/`sha256` 是否和真实增量包一致、两个平台是否指向同一份内容；再按 0.0.73 / 0.0.74 老客户端的
挑选口径（Gitee 只看第一页且是旧 → 新、GitHub 新 → 旧）模拟一遍挑版本，挑不到就直接失败并打印
"要删哪些老 release、要删哪些老 `update_v*.json`"。只读、不改任何发布内容；等客户端全部升级到
带分页挑选的版本之后，可用 `-AllowLegacyBlind` 放行这一项。
