# 发布与运行时开发规范

修改发布、启动器、更新、FFmpeg、LibVLC 或 TTS 前必须阅读本文件。通用开发、测试、提交和安全规则仍以仓库根目录 `AGENTS.md` 为准。

## 运行时与发布包布局

- 发布脚本默认生成目录包和匹配的 7z；完整 ZIP 仅在传入 `-IncludeFullZip` 时作为本地兼容产物生成，不上传到 Release。清爽包根目录主要包含 `ExpressPackingMonitoring.exe` 与 `app\`；应用负载、依赖、Web 文件、LibVLC 和 `tools\ffmpeg.exe` 位于 `app\`。
- 运行时数据统一存放在 `%LOCALAPPDATA%\ExpressPackingMonitoring\`。发布包不得包含 `config.json`、`videos.db`、缓存、日志、录像或其他本机状态。
- `ffmpeg.exe` 可从 `app\tools\ffmpeg.exe`、应用运行目录或系统 `PATH` 解析。AppPatch 不携带 FFmpeg，因此应用逻辑必须兼容用户机器长期保留的旧版本。
- LibVLC 收录播放所需的解码、解封装、字幕、滤镜和输出插件，只排除 `access_output`、`mux`、`services_discovery`、`stream_out`、`visualization`、`lua`；规则集中在 `ExpressPackingMonitoring.csproj`。发布时移除设计时程序集。
- Edge TTS 是默认在线语音；Kokoro 模型和运行时默认不随包发布。完整包包含生成的默认 Edge TTS 缓存，AppPatch 必须排除 TTS 缓存。

## FFmpeg 兼容基线

- 正式基线固定为 FFmpeg 4.4.1 Essentials，以兼容 Win7 老显卡的 NVENC API 11.1。AV1 不作为产品能力，选择 AV1 时回退 H.265；暂不实施 8.0.1 + 4.4.1 双基线。
- 高级用户可在 Win8+ 自行替换 `app\tools\ffmpeg.exe`，官方不保证该自定义环境。
- 禁止假设 CLI 参数跨版本通用：FFmpeg 8.x 已移除 RTSP `-stimeout`；4.4.x 的 RTSP `-timeout` 会挂起，因此网络摄像头不传 socket 超时参数，由应用层 15 秒连接超时和断流看门狗兜底。`-fps_mode` 仅 5.1+ 可用，旧版回退 `-vsync passthrough`。参数策略集中在 `NetworkCameraSource.BuildArguments`。
- 修改任何 FFmpeg 调用前，必须使用 `Tools/ffmpeg-baseline.json` 锁定的 4.4.1 和至少一个其他受支持主版本（如 8.0.1）验证受影响流程；同步更新 `NetworkCameraSourceTests` 参数断言和随包 FFmpeg 参数识别测试。

## 启动器与增量更新

- 用户应从包根目录启动器进入。启动器立即启动主程序、后台检查并下载经过校验的 AppPatch 到 `cache\updates`，下次启动时安装。
- 主程序可通过独立校验的 `launcher_package` 更新根启动器：等待旧进程退出、持有共享更新互斥体、只替换标准根启动器，失败时恢复旧文件。
- AppPatch 是固定基线累计补丁，当前默认基线为 `0.0.18`；启动器基线与 AppPatch 基线相互独立。
- AppPatch 只新增或覆盖清单中的文件，不删除已从新发布目录移除的路径。功能迁移不得把安装目录残留文件视为用户仍在使用该功能；确需删除发布文件时，应另行设计带安全白名单和回滚能力的删除清单。
- 启动器基线由 `Tools/launcher-baseline.json` 锁定。普通应用发布复用锁定字节，不重建或重复上传 LauncherPatch。启动器逻辑输入变化时运行 `Tools/Publish-LauncherBaseline.ps1`、提交新锁并创建普通 `launcher-vX.Y.Z` 标签，不为该组件标签创建 Release。
- AppPatch 与 LauncherPatch 是两个独立 ZIP，各自带手动安装器和说明；AppPatch 绝不能包含启动器。
- 更新地址通过环境变量或 `.env` 配置；默认检查 GitHub latest release API。

## 打包与发布流程

- 发布版本维护在 `ExpressPackingMonitoring/ExpressPackingMonitoring.csproj` 的 `<Version>`，并与 `vX.Y.Z` 标签一致。对应版本标签位于 `HEAD` 且工作区干净时，正式产物和 `InformationalVersion` 只使用纯版本号；未打对应标签的测试包使用 Git 标准的 `-<距最近标签提交数>-g<短CommitID>` 后缀，脏工作区再追加 `-dirty`。AppPatch、更新清单和包内协议版本始终使用纯语义版本，完整 Commit ID 继续写入程序集元数据。基线、完整包和 AppPatch 必须复用同一次发布生成的主程序文件，保证测试包身份可追溯且不影响更新比较。
- 推荐运行 `打包脚本-增量.bat v<X.Y.Z>`。直接调用时使用：

```powershell
pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1 -Version <X.Y.Z> -PatchBaselineVersion 0.0.18 -BaselineAppDir "package\ExpressPackingMonitoring+v0.0.18\ExpressPackingMonitoring+v0.0.18\app"
```

- `-BaselineAppDir` 必须指向真实固定基线的 `app` 子目录并包含 `tools\ffmpeg.exe`。脚本从目录解析实际基线，并强制与更新清单和补丁清单一致，禁止手工伪造。
- `-ReuseExistingLauncherBaseline` 只用于同一发布标签重发；普通新版本不传。
- 先完成 Release 构建、全量测试、自动验收和发布包校验，再推送 `main` 到 GitHub 与组织 Gitee 仓库 `PackingProof/PackingProof-Desktop`，最后创建并同步标签。禁止先推标签再编译。
- 发布前执行 `pwsh -NoProfile -File Tools/Test-Release-Automated.ps1`。不得在未完成真实设备检查时传 `-ConfirmManualCoreChecks`；未验证场景必须报告。
- `RELEASE_CHECKLIST.md` 中的真实设备场景建议执行但不阻断发布；未验证项必须在交付和发布说明中明确列出。
- 自动测试通过后仍要审计上一版本以来的完整变更，追踪录像、更新、授权、备份、删除和文件替换等关键路径；可信的正确性、数据安全、兼容性、性能或竞态问题均阻断发布，除非用户明确接受记录在案的例外。

## 发布笔记与资产

- 发布笔记必须使用 `RELEASE_NOTES_TEMPLATE.md`，并先以 `git log --oneline <上一正式版标签>..HEAD` 核对全部提交。按“功能与体验 / 问题修复 / 兼容与工程”填写，覆盖所有用户可见变化和未验证事项。
- 标题固定为 `v<X.Y.Z> <一句话内容>`。预览版需在 GitHub 与 Gitee 标记 prerelease；预览版只写本次增量，正式版汇总上一正式版以来所有预览版。
- `update_vX.Y.Z.json` 的 `title` 与 Release 标题完全一致。`notes` 是供启动器直接显示的纯文本字符串数组，每项只写一条简洁、用户可见的变化；禁止 Markdown 标题、列表减号、序号、换行排版和“下载某某包更新”等说明。启动器会自动添加列表符号，完整内容留在 Release 页面。
- 不生成 AppFull 或 ManualUpdate，不上传旧名 `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`。`launcher_manifest` 和 `release_info` 仅作本地校验交接，默认不上传。

| 目标 | 上传资产 |
| --- | --- |
| GitHub | Setup、完整 7z、update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch |
| Gitee `PackingProof/PackingProof-Desktop` | update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch，不上传 Setup 或完整 7z |

- Gitee 使用 CLI：先运行 `gitee auth status`，再对 `PackingProof/PackingProof-Desktop` 执行 `gitee release create --repo PackingProof/PackingProof-Desktop --target main` 和 `gitee release upload`；不再向旧个人仓库发布。
