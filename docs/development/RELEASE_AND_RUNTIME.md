# 发布与运行时开发规范

修改发布、启动器、更新、FFmpeg、LibVLC 或 TTS 前必须阅读本文件。通用开发、测试、提交和安全规则仍以仓库根目录 `AGENTS.md` 为准。

## 运行时与发布包布局

- 发布脚本默认只生成目录包和 Setup；完整 7z 与完整 ZIP 仅在分别传入 `-IncludeSevenZip`、`-IncludeFullZip` 时作为本地产物生成，都不上传到 Release。清爽包根目录主要包含 `ExpressPackingMonitoring.exe` 与 `app\`；应用负载、依赖、Web 文件、LibVLC 和 `tools\ffmpeg.exe` 位于 `app\`。
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
- 代码改动一律走远程 PR，不再直接向 `main` 推送提交。默认先提交到 Gitee，再同步到 GitHub；目标可以用仓库根目录 `.env` 的 `PR_TARGET_HOST`（`gitee` / `github` / `both`，默认 `gitee`）或命令行 `-Target` 覆盖。用 `pwsh -NoProfile -File Tools\Submit-ChangePr.ps1 -Title "<PR 标题>" [-Merge]` 推送分支、创建 PR，并在需要时用 rebase 合并、把主干同步到另一个远端。
- PR 提到哪个远端按问题来源决定：自己发现的 bug 默认提 Gitee；别人在某个平台提的 issue，PR 就提到那个平台（Gitee 的 issue 提 Gitee PR，GitHub 的 issue 提 GitHub PR），合并后再把主干同步到另一个远端。
- 发布顺序固定为：在功能分支提交并保持工作区干净 → 运行本地 CI → 提 PR 并合并到主干（rebase 合并，保留每个提交，不 squash）→ 同步主干 → 在合并后的提交上创建本地 `vX.Y.Z` 标签 → 以该标签身份执行一次 Release 构建、全量测试、自动验收、打包和产物校验 → 只推送该标签到 GitHub/Gitee → 创建 Release 并上传已校验产物。标签必须指向已在主干上的提交且先于正式构建：既避免先构建测试身份再为正式标签重复编译，也避免 PR rebase 之后标签悬空。
- 打包脚本会在产物目录生成 `release_commits_v<X.Y.Z>.txt`（上一个正式版以来的全部提交，仅本地核对、不上传），并按需生成或保留 `RELEASE_NOTES_v<X.Y.Z>.md`；重新打包不会再冲掉已经写好的发布笔记。
- 发布脚本 `Tools/Publish-Releases.ps1` 属于门禁的一部分：它校验发布笔记的分段与占位符、校验 `update_v<X.Y.Z>.json` 的 `title` 与 `notes` 是否已填写，并在打印提交清单后要求显式传入 `-ConfirmCommitCoverage`。只想自检用 `-ValidateOnly`，已经发布过的版本要补正文用 `-UpdateNotes`（只更新正文与标题，不重复上传附件）。
- 本地 CI 命令为 `pwsh -NoProfile -File Tools/Test-CI.ps1`，它与 `.github/workflows/ci.yml` 保持同一还原、构建、单元测试和 JavaScript 语法检查门禁。发布前必须先通过本地 CI，再运行 `Tools/Test-Release-Automated.ps1`；任一失败都不得推送标签或发布。
- 开始构建前先跑 `pwsh -NoProfile -File Tools/Check-ReleasePrereqs.ps1` 自检本机发布条件（工作区、标签与版本一致性、gh/gitee 登录态、dotnet、7-Zip、Inno Setup）。只读检查，不构建、不上传、不打印凭据。
- `.github/workflows/release-package.yml` 只响应 `v*.*.*` 标签或手动触发，不再响应普通 `main` push。GitHub 侧仅对已在本地通过门禁的标签执行一次发布包构建，避免每次提交都耗电打包。
- 推荐运行 `打包脚本-增量.bat v<X.Y.Z>`。直接调用时使用：

```powershell
pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1 -Version <X.Y.Z> -PatchBaselineVersion 0.0.18 -BaselineAppDir "package\ExpressPackingMonitoring+v0.0.18\ExpressPackingMonitoring+v0.0.18\app"
```

- `-BaselineAppDir` 必须指向真实固定基线的 `app` 子目录并包含 `tools\ffmpeg.exe`。脚本从目录解析实际基线，并强制与更新清单和补丁清单一致，禁止手工伪造。
- `-ReuseExistingLauncherBaseline` 只用于同一发布标签重发；普通新版本不传。
- 正式标签构建通过后，只把 `vX.Y.Z` 标签推送到 GitHub 与组织 Gitee 仓库 `PackingProof/PackingProof-Desktop`（`main` 已在 PR 合并时更新，不再直接推送提交），然后创建 Release。禁止普通 `main` push 触发发布包工作流。
- 发布前执行 `pwsh -NoProfile -File Tools/Test-Release-Automated.ps1`。不得在未完成真实设备检查时传 `-ConfirmManualCoreChecks`；未验证场景必须报告。
- `RELEASE_CHECKLIST.md` 中的真实设备场景建议执行但不阻断发布；未验证项必须在交付和发布说明中明确列出。
- 自动测试通过后仍要审计上一版本以来的完整变更，追踪录像、更新、授权、备份、删除和文件替换等关键路径；可信的正确性、数据安全、兼容性、性能或竞态问题均阻断发布，除非用户明确接受记录在案的例外。

## 发布笔记与资产

- 发布笔记必须使用 `RELEASE_NOTES_TEMPLATE.md`，并**逐条**核对 `release_commits_v<X.Y.Z>.txt` 里的全部提交（等价于 `git log --oneline <上一正式版标签>..HEAD`）。按“功能与体验 / 问题修复 / 兼容与工程”填写，覆盖所有用户可见变化和未验证事项；纯工程或测试提交也要在《兼容与工程》里落到文字，不能只写几条最重要的就交付。
- 发布笔记写到该版本自己的产物目录 `package/PackingProof+v<X.Y.Z>/RELEASE_NOTES_v<X.Y.Z>.md`，在打包生成产物目录之后写入。禁止放在仓库根目录，也禁止提交进仓库；`package/*` 已被 Git 忽略。打包脚本会在文件不存在时按模板生成骨架，在文件已存在时原样保留，正常流程不需要手工改文件名。
- 标题固定为 `v<X.Y.Z> <一句话内容>`，且这句话必须点出本版本最核心的变化（本版投入最大的功能，或用户最痛的问题），例如「采集预览迁移 GPU 与存储判定重做」；只写版本号或只写“体验优化”都会被发布脚本拒绝（未传 `-Title` 直接报错）。
- 条目顺序按重要性从高到低：本版重点功能 → 重要修复 → 小优化与文案调整；`update_v<X.Y.Z>.json` 的 `notes` 保持同一顺序。同一件事（同一模块的默认值、迁移、提示、界面入口等）必须合并成一条，不允许拆成多条重复描述。预览版需在 GitHub 与 Gitee 标记 prerelease；预览版只写本次增量，正式版汇总上一正式版以来所有预览版。
- `update_vX.Y.Z.json` 的 `title` 与 Release 标题完全一致，`notes` 必须已经填写，发布脚本会直接拒绝“请填写更新标题”这类占位内容。`notes` 是供启动器直接显示的纯文本字符串数组，每项只写一条简洁、用户可见的变化；禁止 Markdown 标题、列表减号、序号、换行排版和“下载某某包更新”等说明。`notes` 按模块归并、控制在 15~20 条以内，但必须覆盖本版本全部用户可见变化，不能只写几条最重要的；超过 20 条发布脚本会拒绝。
- `notes` 面向的是店员等最终用户，只写他们能感知的变化。工程与内部改动一律不写，例如运行时版本、打包与增量包机制、CI 门禁、代码重构、测试补充；这些留在发布笔记的“兼容与工程”里。
- `notes` 每条要短：启动器会折行，但条目过长折成多行后整页看起来又乱又难读。单条建议 20~35 字，硬上限 40 字（含标点），发布脚本会拒绝超长条目。
- 不生成 AppFull 或 ManualUpdate，不上传旧名 `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`。`launcher_manifest` 和 `release_info` 仅作本地校验交接，默认不上传。

| 目标 | 上传资产 |
| --- | --- |
| GitHub | Setup、update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch |
| Gitee `PackingProof/PackingProof-Desktop` | update JSON、可选 `PackingProof_AppPatch`；仅新启动器基线时上传 LauncherPatch，不上传 Setup |

- 完整 7z 与完整 ZIP 都不再上传到任何 Release。二者默认也不生成，仅在本地确有需要时分别传入 `-IncludeSevenZip` 和 `-IncludeFullZip`；免安装分发统一由 Setup 和目录包承担。

- Gitee 发布令牌固定取仓库根目录 `.env` 的 `GITEE_TOKEN`，由脚本注入 `GITEE_TOKEN` 环境变量后交给 CLI；CLI 自己保存的登录态只作回退，而且它按身份字符串各存一份、`gitee auth status` 在令牌失效时仍返回 0，不能用来判断可用性。发布时对 `PackingProof/PackingProof-Desktop` 执行 `gitee release create --repo PackingProof/PackingProof-Desktop --target main` 和 `gitee release upload`；不再向旧个人仓库发布。
- 两个平台的 Release 可由 `pwsh -NoProfile -File Tools/Publish-Releases.ps1 -Title "<一句话内容>" -ConfirmCommitCoverage [-Prerelease]` 一次创建，发布笔记默认取产物目录里的 `RELEASE_NOTES_v<X.Y.Z>.md`（要指别处才传 `-NotesFile`）。脚本要求工作区干净、当前提交有精确 tag，并确认发布笔记已覆盖 `release_commits_v<X.Y.Z>.txt` 的全部提交后才按上面的资产表挑文件上传，GitHub 创建失败会自动重试；已发布版本补写正文用 `-UpdateNotes`，只校验不发布用 `-ValidateOnly`。GitHub 登录态由 gh CLI 维护，Gitee 令牌由脚本从 `.env` 读取后注入环境变量，不打印、不落盘、不提交。
