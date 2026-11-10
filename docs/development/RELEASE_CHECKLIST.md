# 发布前自动化与 AI 自查清单

面向 AI 与自动化：每条都对应能跑的命令或能核对的文件。需要人和设备才能做的现场检查在
`docs/development/RELEASE_FIELD_CHECKS.md`（非阻断建议：发布时提醒现场执行）。

## 竞态与性能审计（自动测试覆盖不到）

- 先做这一步：审计发现问题就直接停下来改，不要先花时间跑门禁和打包
- **先做改动量自评**：按"独立评审门槛"（见 `RELEASE_AND_RUNTIME.md`）算一次 `git diff <上个正式版标签>..HEAD` 的量级。小改动可只跑门禁；大改动或触碰关键路径（更新/自更新/打包、录像/存储/删除、扩展 API 授权）必须发起**不带上下文**的外部独立评审，范围是完整差集、按风险分域、可以并行多个评审者；自评结论与评审安排先报给用户
- 独立评审的**阻断项必须先修完、复评通过**才能打包；发布记录里写明评审者、范围、结论与处理情况
- **更新安装与启动必须互斥**：任何"启动主程序"的路径（含被重定向的直接启动、用户在安装过程中再次点击启动器）都必须排在拿到更新互斥体、且补丁已经装完之后；拿不到锁要等，绝不能跳过安装直接启动 —— 0.0.83 现场就是"跳过安装直接启动"，把主程序拉到正在被覆盖的目录上，起不来只能手动再开。验证入口：`pwsh -NoProfile -File tools\Test-UpgradeFromLegacyLauncher.ps1 -LegacyLauncherPath <启动器> -SimulateSecondInstance`（断言第一条「已启动主程序」晚于「更新完成」）
- **启动器是原生 AOT**：它引用的 UpdateCore 里不得出现反射式 JSON（`JsonSerializer`）或其它需要运行时代码生成的用法；AOT 发布只有警告、运行时才炸。`LauncherAotCompatibilityTests` 守这条，发布前也看一眼 `dotnet publish -r win-x64` 的 ILC 警告是否为 0
- 逐条过一遍上一版本以来的并发与生命周期改动：线程/任务边界、事件订阅与退订、释放顺序，确认没有"谁先跑不确定"的假设
- 录像与预览链路：确认界面线程上没有逐帧同步等待，预览降档或暂停不影响录像与识别
- 新增或改动的循环、定时器、缓存：确认有退出条件和容量上限，长时间运行内存不持续上涨
- 跨线程共享状态有锁或原子保护；新加的"后写覆盖先写"逻辑不会让旧值盖掉新值
- 上述任一项存在可疑的正确性、数据安全、兼容性、性能或竞态问题均阻断发布，除非明确记录并接受例外

## 自动化门禁

- 先运行 `pwsh -NoProfile -File tools/Test-CI.ps1`，确认本地 CI 与 `.github/workflows/ci.yml` 同步通过
- 运行 `pwsh -NoProfile -File tools/Test-Release-Automated.ps1`
- 运行 `pwsh -NoProfile -File tools/Check-ReleasePrereqs.ps1`：工作区、标签与版本一致性、启动器基线指纹、发布渠道登录态与工具链一次看清；启动器逻辑输入变化必须先重建基线，不要等到打包才拦
- 确认 `tools\store-package.local.json` 是 Partner Center 的真实身份，且 `makeappx`、`makepri`、`signtool` 可用；Store MSIX 是正式发布必跑项，不是可选旁路
- 确认隔离 WPF 启停、userscript 并发/延时/多监控端和 Web 播放/剪辑界面自动验收全部通过
- 确认必需的核心测试均存在，没有被删除或改名绕过
- 确认 Release 全量测试全部通过
- 确认 Web JavaScript 语法检查通过
- 确认完整解决方案 Release 构建为 0 警告、0 错误
- 确认 `tools\vision-model-baseline.json` 的 `source.repository` 指向 `PackingProof/PackingProof-Vision`，对应模型 Release 已先于 Desktop 发布，且 GitHub/Gitee 两个地址都能下载相同 SHA256 的 `vision-obb.onnx`

## 产物与发布校验

- 核对 `/api/node-info` 公布的备份协议、enrollment、鉴权版本及双端最低版本与兼容策略代码一致，更新提示中的手机和电脑下载地址均可用
- 提高任一客户端最低版本时，先发布并确认另一端已有兼容安装包，再发布启用新门禁的主机版本
- 完整包不包含配置、数据库、日志、录像和其他本机运行状态
- 完整包必须包含 `app/tools/ffmpeg.exe`，并与 `tools/ffmpeg-baseline.json` 登记的任一兼容身份大小和 SHA256 一致。优先使用自建精简构建以减小包体积；本地产物缺失时允许使用按哈希校验下载的 Gyan essentials，不阻断第三方开发者出包。另用 `pwsh -NoProfile -File tools\ffmpeg-capability-check.ps1` 验证实际选中的二进制可在干净 PATH 启动且能力矩阵完整
- 使用包内 FFmpeg 验证 H.264/H.265 软编码、三类显卡硬件探测、MKV 封装、MP4 转换、缩略图、Web 转码和剪辑；无可用 AV1 硬件编码器时必须明确回退 H.265
- 发布包的 LibVLC 按 `ExpressPackingMonitoring.csproj` 白名单发布；必须保留 `filesystem`、`imem`、解码器、解封装器、封包解析器、字幕和音视频输出。UNC NAS 回放通过 `StreamMediaInput` + `imem`，不依赖 `smb` 插件；发布前必须用发布包实际播放仅存 NAS 归档的 UNC 录像并拖动进度条
- 默认使用 7z 等级 5 与 ZIP/AppPatch `Optimal` 均衡压缩，Setup 保持 `lzma2/ultra64`；仅在明确需要对比时才手动指定 7z 等级 9 或 ZIP `SmallestSize`
- Setup 从同一干净发布目录生成，不包含配置、数据库、日志、缓存或录像；固定 AppId、当前用户 x64 模式和固定安装目录均正确
- 完整包包含预生成的默认 Edge TTS 语音缓存，首次使用固定文案不需要现场生成
- 增量包不包含 TTS 缓存，并验证补丁清单、`update_vX.Y.Z.json`、启动器基线清单、标签和程序版本号一致
- 发布前把仓根 `Directory.Build.props` 的 `<Version>` 更新为本次版本（这是唯一的版本来源，工程文件不得再各自写 `<Version>`），并与 `vX.Y.Z` 标签、`update_vX.Y.Z.json` 保持一致
- AppPatch 运行时稳定性核对：默认必须保持 `-AppRuntimeFrameworkVersion 10.0.13`，打包日志不得出现“检测到 .NET/WPF 运行时发生变化”；如确需升级应用运行时，必须改走完整包/Store 发布并重新评估补丁体积
- 补丁基线：0.0.83、0.0.84 历史发布使用 `-PatchBaselineVersion 0.0.18`；**0.0.84 之后的下一个正式版（0.0.86）起统一使用 `-PatchBaselineVersion 0.0.84`**，0.0.85 从未正式发布，不单独作为基线版本；把 0.0.84 定成所有老装机必须经过的补丁基线。低于 0.0.84 的客户端会先通过 0.0.84 的 `update_v0.0.84.json` 与 AppPatch 自动升到基线，再继续升级到 0.0.86；只有中间基线包不可用时才提示完整安装包，因此 0.0.84 的 Release 与 AppPatch 必须保留。**主动强制完整包的标志位是 `patch_supported: false`**；`required_launcher_protocol` 只会先触发启动器更新，不是强制完整包。打包脚本会拒绝 0.0.86+ 使用更低的基线；`tools\打包脚本-增量.bat` 和 `tools\Publish-CleanPackage.ps1` 的默认补丁基线都必须是 `0.0.84`
- 自包含发布形态核对：`app\` 有 `coreclr.dll`/`hostfxr.dll`、包根没有 `runtime\`、`app\runtime-seed\layout.json` 与布局清单逐文件一致；`pwsh -NoProfile -File tools\Test-RuntimeLayoutSeed.ps1` 全绿（含"框架依赖的探针带 DOTNET_ROOT 从落成的布局加载运行时"）
- 升级时序字段核对：带 `root=runtime` 载荷的补丁必须声明 `required_launcher_protocol >= 2`；`launcher_package.required_app_protocol` 只在"新启动器需要更新的主程序"时才填
- 清理列表核对：包内 `patch_manifest.json` 的 `deletes` 与 `tools\app-patch-retire-list.json`（累计退役清单）一致、不含白名单（`tts_cache` / `ffmpeg.exe` / `libvlc`）、不含主程序核心文件；**退役清单若有变化要随发布一起提交**；用 `pwsh -NoProfile -File tools\Test-UpgradeFromLegacyLauncher.ps1 -LegacyLauncherPath <新启动器> -PackageDir <产物目录> -SimulateSecondInstance` 验证"装完才启动 + 12 个旧文件确实清掉"
- 发布流程顺序：改动经 PR 合并到主干并通过本地 CI → 在合并后的提交上创建本地 `vX.Y.Z` 标签 → 以标签身份完成一次 Release 构建、全量测试、自动验收和发布包校验（含 Store MSIX）→ 推送标签到 GitHub 与 Gitee → 创建并同步 Release（含 Mac 侧上传的 macOS DMG）→ 将同版本 Store MSIX 提交 Partner Center。Gitee 安装包按完整 Setup 是否严格小于 100MB 分流：小于时直接上传完整 Setup，达到时才生成并上传 no-runtime 安装向导；禁止普通 `main` push 触发发布包工作流
- 发布笔记按 `RELEASE_NOTES_TEMPLATE.md` 填写：更新内容三类齐全、《问题修复》只写上一正式版就存在的缺陷（本版自己引入又修掉的不写）、下载与更新说明与实际上传资产一致（含 Gitee 实际使用的完整 Setup 或 no-runtime 安装向导、Microsoft Store MSIX、macOS DMG，且不写已不存在的免安装整包）、且与 `update_vX.Y.Z.json` 的标题和说明同步
- 预览版本必须在 GitHub 与 Gitee 上将 Release 标记为 prerelease，发布笔记正文首行注明“预览版”；正式版本不得标记 prerelease
- 更新日志范围：预览版只写本预览版增量内容；正式版必须汇总上一个正式版以来（含中间所有预览版）的全部更新内容
- 生成 AppPatch 前必须验证固定基线 FFmpeg 的大小和 SHA256 位于兼容白名单，并确认当前保留的每个 LibVLC 必需文件在基线中存在且哈希一致；基线多出的旧 VLC 插件可以保留
- 兼容基线生成的 AppPatch 不得包含 `tools/ffmpeg.exe`、任何 `libvlc/` 文件或 VLC 删除记录；无法证明兼容时不得生成大型补丁，更新 JSON 必须关闭 Patch、清空补丁信息并引导用户下载完整版本
- AppPatch 包含 `patch_manifest.json`、`files/`、`双击更新主程序.cmd`、`apply_app_patch.ps1` 和主程序更新说明，不再生成或嵌套 ManualUpdate 包
- Store MSIX 必须与 Setup 来自同一次 `app\` 负载；核对 `package\store\PackingProof_Store_v<X.Y.Z>\PackingProof_Store_v<X.Y.Z>.msix`、`store_package_v<X.Y.Z>.json` 的身份/版本/SHA256、`app_payload_sha256` 与本次 Setup `app\` 一致，以及包内 `PackingProof.PackageEdition.json`、AppxManifest 身份/版本和“无 AppPatch/LauncherPatch 脚本”守卫；同时核对 `package_integrity_v<X.Y.Z>.json` 的 Setup SHA256/大小和 exact `app\` 指纹
- 使用 Git 比较当前启动器逻辑输入与 `tools/launcher-baseline.json` 对应的 `launcher-vX.Y.Z` 组件标签；组件标签只推送普通 Git 标签，不创建独立 Release
- 启动器未变化时不执行 Native AOT 重编译、不生成或上传本版本 LauncherPatch；完整包根启动器必须与锁定基线 EXE 的大小和 SHA256 完全一致
- 启动器变化时先运行 `tools/Publish-LauncherBaseline.ps1` 建立不可变基线；LauncherPatch 只包含根启动器、`launcher_patch_manifest.json`、`双击更新启动器.cmd`、`apply_launcher_patch.ps1` 和启动器更新说明
- 本地基线文件缺失时允许从正式 App Release 下载，但必须验证 LauncherPatch 与包内 EXE 的大小、SHA256 和固定条目；任何不一致均阻止发布
- 启动器包下载默认使用 GitHub，单次失败可立即使用更新清单中的 Gitee 地址兜底；连续失败达到阈值后优先 Gitee，成功或命中已验证缓存后必须清零失败计数
- `update_vX.Y.Z.json` 的更新内容与最终发布说明一致，合并发布时包含尚未正式发布版本的有效改动
- GitHub 上传 Setup、更新 JSON、可用的 AppPatch，以及仅在本版本建立新基线时生成的 LauncherPatch；完整 7z / ZIP 不再生成也不上传；默认不上传启动器清单和发布信息文件，未签名时发布说明明确提示 SmartScreen。AppPatch 只生成并上传 `PackingProof_AppPatch_vX.Y.Z.zip`，不再生成旧别名 `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`
- Gitee 仅向组织仓库 `PackingProof/PackingProof-Desktop` 发布：先确认 `gitee auth status` 已登录，显式指定 `--repo PackingProof/PackingProof-Desktop` 和 `--target main` 创建 Release，再上传更新 JSON、可用的 AppPatch（只上传 `PackingProof_AppPatch_vX.Y.Z.zip`），以及仅在本版本建立新基线时生成的 LauncherPatch；完整 Setup 严格小于 100MB 时直接上传 Setup，达到 100MB 时改传 no-runtime 安装向导；启动器清单和发布信息文件不上传

## 现场检查（提醒，不阻断）

需要人和设备才能做的项在 `docs/development/RELEASE_FIELD_CHECKS.md`：发布开始时提醒现场执行，
打包脚本不因它们中断（`-ConfirmManualCoreChecks` 只记录确认状态）。
