# 发布前自动化与 AI 自查清单

面向 AI 与自动化：每条都对应能跑的命令或能核对的文件。需要人和设备才能做的现场检查在
`docs/development/RELEASE_FIELD_CHECKS.md`（非阻断建议：发布时提醒现场执行）。

## 竞态与性能审计（自动测试覆盖不到）

- 先做这一步：审计发现问题就直接停下来改，不要先花时间跑门禁和打包
- 逐条过一遍上一版本以来的并发与生命周期改动：线程/任务边界、事件订阅与退订、释放顺序，确认没有"谁先跑不确定"的假设
- 录像与预览链路：确认界面线程上没有逐帧同步等待，预览降档或暂停不影响录像与识别
- 新增或改动的循环、定时器、缓存：确认有退出条件和容量上限，长时间运行内存不持续上涨
- 跨线程共享状态有锁或原子保护；新加的"后写覆盖先写"逻辑不会让旧值盖掉新值
- 上述任一项存在可疑的正确性、数据安全、兼容性、性能或竞态问题均阻断发布，除非明确记录并接受例外

## 自动化门禁

- 先运行 `pwsh -NoProfile -File Tools/Test-CI.ps1`，确认本地 CI 与 `.github/workflows/ci.yml` 同步通过
- 运行 `pwsh -NoProfile -File Tools/Test-Release-Automated.ps1`
- 运行 `pwsh -NoProfile -File Tools/Check-ReleasePrereqs.ps1`：工作区、标签与版本一致性、启动器基线指纹、发布渠道登录态与工具链一次看清；启动器逻辑输入变化必须先重建基线，不要等到打包才拦
- 确认隔离 WPF 启停、userscript 并发/延时/多监控端和 Web 播放/剪辑界面自动验收全部通过
- 确认必需的核心测试均存在，没有被删除或改名绕过
- 确认 Release 全量测试全部通过
- 确认 Web JavaScript 语法检查通过
- 确认完整解决方案 Release 构建为 0 警告、0 错误

## 产物与发布校验

- 核对 `/api/node-info` 公布的备份协议、enrollment、鉴权版本及双端最低版本与兼容策略代码一致，更新提示中的手机和电脑下载地址均可用
- 提高任一客户端最低版本时，先发布并确认另一端已有兼容安装包，再发布启用新门禁的主机版本
- 完整包不包含配置、数据库、日志、录像和其他本机运行状态
- 完整包必须包含 `app/tools/ffmpeg.exe`，并与 `Tools/ffmpeg-baseline.json` 中锁定的版本、大小和 SHA256 一致；缓存缺失时允许从两个固定来源下载，任何校验失败均阻止发布
- 使用包内 FFmpeg 验证 H.264/H.265 软编码、三类显卡硬件探测、MKV 封装、MP4 转换、缩略图、Web 转码和剪辑；无可用 AV1 硬件编码器时必须明确回退 H.265
- 发布包的 LibVLC 只移除网络访问、推流、复用、服务发现、可视化和 Lua 组件；必须保留本地文件访问、全部解码器、解复用器、封包解析器、字幕、音视频输出、滤镜和 HRTF
- 默认使用 7z 等级 5 与 ZIP/AppPatch `Optimal` 均衡压缩，Setup 保持 `lzma2/ultra64`；仅在明确需要对比时才手动指定 7z 等级 9 或 ZIP `SmallestSize`
- Setup 从同一干净发布目录生成，不包含配置、数据库、日志、缓存或录像；固定 AppId、当前用户 x64 模式和固定安装目录均正确
- 完整包包含预生成的默认 Edge TTS 语音缓存，首次使用固定文案不需要现场生成
- 增量包不包含 TTS 缓存，并验证补丁清单、`update_vX.Y.Z.json`、启动器基线清单、标签和程序版本号一致
- 发布前把 `ExpressPackingMonitoring/ExpressPackingMonitoring.csproj` 的 `<Version>` 更新为本次版本，并与 `vX.Y.Z` 标签、`update_vX.Y.Z.json` 保持一致
- 发布流程顺序：改动经 PR 合并到主干并通过本地 CI → 在合并后的提交上创建本地 `vX.Y.Z` 标签 → 以标签身份完成一次 Release 构建、全量测试、自动验收和发布包校验 → 推送标签到 GitHub 与 Gitee → 创建并同步 Release（含 Mac 侧上传的 macOS DMG）→ 生成并上传 Gitee 专属的 no-runtime 安装向导；禁止普通 `main` push 触发发布包工作流
- 发布笔记按 `RELEASE_NOTES_TEMPLATE.md` 填写：更新内容三类齐全、下载与更新说明与实际上传资产一致（含 Gitee 的 no-runtime 安装向导与 macOS DMG）、且与 `update_vX.Y.Z.json` 的标题和说明同步
- 预览版本必须在 GitHub 与 Gitee 上将 Release 标记为 prerelease，发布笔记正文首行注明“预览版”；正式版本不得标记 prerelease
- 更新日志范围：预览版只写本预览版增量内容；正式版必须汇总上一个正式版以来（含中间所有预览版）的全部更新内容
- 生成 AppPatch 前必须验证固定基线 FFmpeg 的大小和 SHA256 位于兼容白名单，并确认当前保留的每个 LibVLC 必需文件在基线中存在且哈希一致；基线多出的旧 VLC 插件可以保留
- 兼容基线生成的 AppPatch 不得包含 `tools/ffmpeg.exe`、任何 `libvlc/` 文件或 VLC 删除记录；无法证明兼容时不得生成大型补丁，更新 JSON 必须关闭 Patch、清空补丁信息并引导用户下载完整版本
- AppPatch 包含 `patch_manifest.json`、`files/`、`双击更新主程序.cmd`、`apply_app_patch.ps1` 和主程序更新说明，不再生成或嵌套 ManualUpdate 包
- 使用 Git 比较当前启动器逻辑输入与 `Tools/launcher-baseline.json` 对应的 `launcher-vX.Y.Z` 组件标签；组件标签只推送普通 Git 标签，不创建独立 Release
- 启动器未变化时不执行 Native AOT 重编译、不生成或上传本版本 LauncherPatch；完整包根启动器必须与锁定基线 EXE 的大小和 SHA256 完全一致
- 启动器变化时先运行 `Tools/Publish-LauncherBaseline.ps1` 建立不可变基线；LauncherPatch 只包含根启动器、`launcher_patch_manifest.json`、`双击更新启动器.cmd`、`apply_launcher_patch.ps1` 和启动器更新说明
- 本地基线文件缺失时允许从正式 App Release 下载，但必须验证 LauncherPatch 与包内 EXE 的大小、SHA256 和固定条目；任何不一致均阻止发布
- 启动器包下载默认使用 GitHub，单次失败可立即使用更新清单中的 Gitee 地址兜底；连续失败达到阈值后优先 Gitee，成功或命中已验证缓存后必须清零失败计数
- `update_vX.Y.Z.json` 的更新内容与最终发布说明一致，合并发布时包含尚未正式发布版本的有效改动
- GitHub 上传 Setup、完整 7z、兼容 ZIP、更新 JSON、可用的 AppPatch，以及仅在本版本建立新基线时生成的 LauncherPatch；默认不上传启动器清单和发布信息文件，未签名时发布说明明确提示 SmartScreen。AppPatch 只生成并上传 `PackingProof_AppPatch_vX.Y.Z.zip`，不再生成旧别名 `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`
- Gitee 仅向组织仓库 `PackingProof/PackingProof-Desktop` 发布：先确认 `gitee auth status` 已登录，显式指定 `--repo PackingProof/PackingProof-Desktop` 和 `--target main` 创建 Release，再上传更新 JSON、可用的 AppPatch（只上传 `PackingProof_AppPatch_vX.Y.Z.zip`），以及仅在本版本建立新基线时生成的 LauncherPatch；不上传 Setup、完整 7z、完整 ZIP、启动器清单和发布信息文件，完整包默认使用外部完整下载页

## 现场检查（提醒，不阻断）

需要人和设备才能做的项在 `docs/development/RELEASE_FIELD_CHECKS.md`：发布开始时提醒现场执行，
打包脚本不因它们中断（`-ConfirmManualCoreChecks` 只记录确认状态）。
