# PackingProof Desktop Agent Guidelines

本文件是所有任务必须遵守的根规范。专题规则按任务范围加载，不需要每次通读全部低频细节。

非 Codex 运行环境如果存在全局规则文件，请一并阅读：

- Windows：`%USERPROFILE%\.codex\AGENTS.md`
- macOS / Linux：`~/.codex/AGENTS.md`

## 项目结构与常用命令

- `ExpressPackingMonitoring.sln`：主解决方案
- `src/ExpressPackingMonitoring/`：WPF 主程序、服务、SQLite、录像逻辑和 `Web/index.html`
- `src/ExpressPackingMonitoring.Launcher/`：清爽发布包的根启动器
- `tests/ExpressPackingMonitoring.Tests/`：自动化回归测试
- `tools/Publish-CleanPackage.ps1`：Setup、完整包、AppPatch、更新清单和可选 LauncherPatch
- `Scripts/PackingProof-Order-Integration-KDZS.user.js`：官方快递助手联动脚本
- `Test/HTML/`：脚本解析和调试用网页样本，不是自动测试套件

```powershell
dotnet restore ExpressPackingMonitoring.sln
dotnet build ExpressPackingMonitoring.sln -c Debug
dotnet test tests\ExpressPackingMonitoring.Tests\ExpressPackingMonitoring.Tests.csproj -c Debug
dotnet run --project src\ExpressPackingMonitoring
pwsh -NoProfile -File tools\Publish-CleanPackage.ps1
pwsh -NoProfile -File tools\Test-Release-Automated.ps1
```

## 开发决策顺序

1. 用户提出新功能时，先搜索现有代码、设置、Web 接口、文档和测试，确认是否已经全部或部分实现。不能仅因入口不明显、名称不同或用户不知道，就重新开发一套。
2. 已有能力满足时，先说明功能位置、使用方法和限制；部分满足时列出现状和真实缺口，只补缺失部分。
3. ERP、油猴脚本、称重设备、聊天机器人及其他第三方适配，优先使用 `docs/EXTENSION_API_V1.md` 的扩展 API、授权和脚本规范，尽量不修改核心源码。
4. 扩展能力不足时，优先补充可复用的通用接口，不为单一平台硬编码专用分支。禁止第三方直接读写数据库、运行时配置、录像目录或 NAS 凭据，也不能把第三方业务逻辑塞进录像、存储、备份和播放核心。
5. 只有现有能力和扩展 API 都无法满足，且需求属于通用核心能力，或扩展方案存在明显的安全、性能、可靠性或体验问题时，才修改核心源码，并先说明理由。

扩展 API 不是低质量旁路；公开接口必须具备独立授权、最小权限、输入校验、兼容性、限流、状态可观测、文档和自动化测试。

## 油猴脚本安装地址不变量

- 所有新生成的油猴安装地址、`@updateURL` 和 `@downloadURL` 必须以 `.user.js` 结尾；通用地址固定为 `/api/userscripts/{scriptId}/download.user.js`，确保浏览器脚本管理器能够识别并进入安装界面。
- 无后缀 `/api/userscripts/{scriptId}/download` 仅允许作为旧版本兼容路由被服务端解析，禁止把它重新生成到安装向导、页面链接或脚本元数据中。
- 未经用户明确同意，不得删除、弱化或改写上述约束及对应架构守卫。修改脚本路由时必须同时验证安装链接、`@updateURL`、`@downloadURL`、JavaScript 响应类型和旧路由兼容性。

## 专题规范路由

任务命中下列范围时，实施前必须完整阅读对应专题。专题与本文件具有同等约束力；同时命中多个范围时全部读取。

| 任务范围 | 必读文档 |
| --- | --- |
| 发布、打包、更新、启动器、FFmpeg、LibVLC、TTS | `docs/development/RELEASE_AND_RUNTIME.md` |
| 存储、缓存、NAS、归档、备份、删除、文件替换、跨端备份协议 | `docs/development/STORAGE_AND_DATA_SAFETY.md` |
| 第三方扩展、ERP、脚本、称重设备、聊天机器人 | `docs/EXTENSION_API_V1.md` |

禁止把草稿路线图当成已实现规范；代码、协议、测试变化时同步维护相关专题。

## 本地环境与安全边界

- 日常构建和测试优先使用本机或局域网编译机，不依赖 GitHub CI。机器地址、账号和连接方式只保存在本机笔记，禁止提交或推送。
- Gitee 发布认证固定从项目根目录 `.env` 的 `GITEE_TOKEN` 读取；若缺失或失效，先读取并同步本机 `C:\Users\Administrator\.gitee\apikey.txt`，不得要求用户手工上传发布资产。`.env` 已被 Git 忽略，禁止提交 token。
- Mac 负责 iOS/Xcode 与 macOS 保存主机（`tools/Publish-MacHost.sh`、`tools/Publish-MacRelease.sh`）；Windows 负责桌面端构建与测试。双机同步优先 rebase，禁止为同步制造本地 merge 提交。
- 不提交配置、数据库、日志、缓存、录像、`.env`、证书、签名材料、密钥或其他本机状态。运行时数据属于 `%LOCALAPPDATA%\ExpressPackingMonitoring\`。
- 本机工具链固定为 .NET 10 SDK（`global.json` 钉在 10.0.400，`tools\Build-SharedRuntimeLayout.ps1` 等脚本按它取运行时）。.NET 10 的 `minimumMSBuildVersion` 是 18.0.0，所以 IDE 必须是 Visual Studio 2026（18.x）；用 VS 2022 打开解决方案会报"未能正确加载解决方案中的一个或多个项目"，命令行 `dotnet` 构建与发布脚本不受影响，别去改 `.sln` 或回退 TFM 掩盖它。

## 代码与界面

- 使用启用 nullable 和 implicit usings 的 C#。公共成员使用 `PascalCase`，局部变量使用 `camelCase`，私有字段使用 `_camelCase`；保持现有 WPF/MVVM 风格。
- 修改应聚焦且最小。不要夹带无关重构、格式化、依赖升级或功能；发现无关问题单独报告。
- 生产 C# 文件原则上保持在 1000 行以内，超过 1000 行后新增独立职责时应优先抽取服务、策略、协调器、仓储或 ViewModel 分部；除架构守卫登记的历史例外外，单文件不得超过 2000 行。
- `WebServer.cs`、`VideoDatabase.cs`、`SettingsWindow.xaml.cs` 是冻结规模的历史例外，只允许缩小，不得净增长。相关功能应按领域抽到可独立测试的类型中，禁止继续向例外文件堆叠路由、协议、查询或界面逻辑。
- 不得通过压缩排版、合并语句、删除有价值的空行或拆成无职责边界的 `partial` 文件规避文件规模约束；拆分应形成可命名、可测试、依赖方向清晰的职责边界。
- 保持 UTF-8，避免整文件重写、换行和编码抖动，尤其是中文、XAML、HTML 和 userscript。
- 新界面复用默认字体 `Microsoft YaHei UI, Segoe UI` 以及现有字号、字重和控件风格；使用不同字体时必须明确说明理由。
- 用户可见文本最后一句不以中文句号结尾；多句文案只移除末尾句号，中间句号保留。
- 网页下载弹层等固定宽度卡片内的操作按钮必须使用可换行或单列布局，不能只靠缩短翻译；用 Playwright 检查按钮不越界、不重叠、文字不被裁切。
- 网页顶栏标题要优先获得内容所需宽度，由右侧操作区先收缩；用 Playwright 比较标题 `scrollWidth`/`clientWidth`，不能提前显示省略号。
- 长内容对话框或选择窗口的操作按钮必须固定在滚动区外，内容过高时只让主体滚动；用回归测试确认按钮的可视父链中没有 `ScrollViewer`。
- LibVLC 回放 UNC/NAS 路径必须走 `PlaybackMediaSource` 的 `FileStream` + `StreamMediaInput`（`imem`），沿用 Windows SMB 凭据；不要把 UNC 直接转成 `new Uri` 交给 `filesystem`，也不要为 NAS 回放重新加入 `libsmb_plugin.dll`。
- 新增设置项时必须先定归属，并在提交说明里交代四件事：放在哪个页签/卡片、按哪个 `SettingsCapabilities` 能力门控（没有合适的能力就先补一个派生属性）、是否受高级模式控制、三语文案是否补齐。只有四个角色都该看到时才允许不加门控；不要靠事后补守卫测试来兜底。

## 国际化与运行时文案

- 所有用户可见文本都要走本地化：XAML 静态文案、代码生成的按钮/状态/选项、ToolTip、无障碍名称、Toast、Alert 和预览提示都算。默认资源、`zh-Hans`、`ja-JP` 三份词条必须同时补齐。
- 不要直接把中文写进 `DisplayName`、`Name`、`Label` 等显示字段。界面自己的选项要用稳定 `Value`/ID 映射到资源（如 `GpuEncoderOption.BuildDisplayName`）；设备名、单号、买家留言等用户数据保持原样，不做标签拆解翻译。
- 使用 `DisplayMemberPath`、`ItemTemplate` 或 `ContentPresenter` 的下拉框要单独验证选中值和展开项。资源存在不等于界面生效，`LocalizationTests` 中的 `ContentPresenter_StringContentUsesRuntimeLocalization` 和 `GpuEncoderOptionDisplayNames_DoNotUseChineseLiterals` 是这两类回归的守卫。
- 自定义 ToolTip、图表坐标轴和图表悬浮卡片要在数据生成时本地化（如 `PlaybackTooltipBuilder`、`StatisticsWindow.FormatChartDate`），不要依赖 ToolTip 弹出后的可视化树遍历；日期、星期、周次和单位都要按当前界面语言生成。
- 代码直接给 `Text`、`Content`、`Header`、`ToolTip` 赋值的状态文案也要有资源；`CodeAssignedUiText_AllHaveEnglishResources` 会检查跨行赋值、三元表达式和拼接，新增分页、检查更新等状态文案时不能只写中文字面量。
- `ContextMenu`、`MenuItem.Header`、动态菜单项和 DataGrid 单元格不在常规可视树遍历里；静态中文必须用 `{loc:Localized ...}`，代码生成菜单必须用 `AppLanguage.Get/Format`，并保持 `WpfViews_MenuItemHeadersUseLocalizedExtension` 通过。
- `AppDialog` 的标题和正文要按当前语言生成；多行详情用 `AppLanguage.Get/Format` 组合并补针对性测试，不能直接拼一整段中文。
- 网页端 `Web/index.html` 的 `map` 与 `jaMap` 必须同步维护；动态 `textContent`、`title`、`aria-label`、`new Option`、自定义 tooltip 和导出/剪辑提示都必须经过 `tWeb()`。新增文案后要跑 `WebLocalizationTests` 和 `tools\Test-WebUi.ps1`。
- Toast、扫码告警、预览大提示不能只写中文字面量。新增 `ShowToast`、`PublishScannerAlert`、`AlertRequest.Message` 或 `AppLanguage.Format` 句式时，同步补资源或模板词条，并让 `ToastLiterals_AllHaveEnglishResources`、`ScannerAlertLiterals_AllHaveEnglishResources` 通过。
- 翻译优先使用短、自然、可直接上控件的表达；按钮、下拉框、页签和固定列宽区域必须用英文/日文实际布局检查，不能只看词条是否存在。静态中文列标题必须用 `{loc:Localized ...}`，并保持 `WpfViews_DataGridColumnHeadersUseLocalizedExtension` 通过。

## 测试与验证

- 每个完整改动至少运行对应回归测试、`dotnet test tests/ExpressPackingMonitoring.Tests/ExpressPackingMonitoring.Tests.csproj -c Debug` 和 `dotnet build ExpressPackingMonitoring.sln -c Debug`。
- 录像、Web 播放、TTS、打包或 FFmpeg 变更还要验证受影响的实际流程；油猴解析使用 `Test/HTML/` 样本。
- 测试环境缺少依赖或生成文件时，优先使用原始仓库或局域网编译环境。任何未完成的验证及原因都必须明确报告。
- 不得把失败或不稳定测试直接归为“无关”；先调查原因。构建和测试成功也不能替代关键路径、性能、资源生命周期和竞态审计。
- 正式发布必须同时构建并校验 Setup 与 Microsoft Store MSIX；Store MSIX 提交 Partner Center，不得当成可选旁路产物，也不能上传到 GitHub/Gitee Release。

## Git 与提交

- 修改或提交前按需检查 `git status` 和 `git diff`。每个独立功能、修复、重构、文档或维护任务分别提交，不 squash，不把无关改动放进同一提交。
- 同一功能连续完善时，只有此前提交由当前代理创建、尚未推送或被他人依赖，且中间没有其他提交，才可 amend；否则创建新提交。
- 分支整合优先 rebase，保持直线历史。已共享分支不得随意 rebase；merge 只用于共享功能分支、发布/长期分支或平台强制场景。
- 除仅更新发布版本号的 chore 提交外，改动一律通过远程 PR 合并到主干，禁止直接向 `main` 推送提交。发布准备阶段可以单独提交只修改 `Directory.Build.props` 中 `<Version>` 的 chore 到 `main`，但不得同时夹带代码、配置、测试或文档修改。
- 提交 PR 必须人工确认：先把分支、目标平台、PR 标题与改动范围报给用户，得到明确同意后才能推送分支、创建 PR 与合并（`tools\Submit-ChangePr.ps1` 不加 `-Confirmed` 只打印计划并拒绝执行）。
- PR 提到哪个平台按问题来源定：我们自己发现的 **bug**，先在 GitHub 开 issue，再提 PR 并在 PR 说明里关联那个 issue；性能、功能、工具、文档这类**不是 bug** 的改动直接提 PR，不必为了留痕再补一个 issue。别人在某平台提的 issue，就把 PR 提到那个平台（Gitee 的 issue 交 Gitee PR，GitHub 的交 GitHub PR）。PR 默认 Gitee 和 GitHub 两边都提。
- 合并后把主干对齐到同一个提交。PR 目标可通过仓库根目录 `.env` 的 `PR_TARGET_HOST`（`gitee` / `github` / `both`，默认 `both`）配置，命令行 `-Target` 优先；统一用 `tools\Submit-ChangePr.ps1` 提交。
- PR 提到哪个平台按问题来源决定：自己发现的 bug 默认提 Gitee；别人在某个平台提的 issue，PR 就提到那个平台（在 Gitee 提的 issue 提 Gitee PR，在 GitHub 提的 issue 提 GitHub PR）。
- PR 合并使用快进（fast-forward），提交原样保留，不改写 SHA、不做 merge 提交、不 squash；发布标签必须打在已合并到主干的提交上。
- 提交格式为 `<type>: <简洁主题>`，通常使用中文；正文需要时用一两句说明修改内容与原因，保持简洁、不罗列实现细节。提交前检查 staged diff。
- 提交信息不得包含个人身份、设备信息、绝对本机路径、账号、内部 URL、令牌、密钥、证书或签名材料。
- Pull Request 应包含摘要、验证步骤、关联问题；UI、播放或打包变更附截图或录像。
