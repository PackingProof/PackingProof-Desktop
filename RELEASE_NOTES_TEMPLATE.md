# 发布笔记模板（每次发布前必须按此填写）

> 发布前复制本模板并替换全部 `<...>` 占位符；完整笔记用于 GitHub/Gitee Release 页面，`update_vX.Y.Z.json` 的 `title` 必须与 Release 标题一致、`notes` 只放简洁摘要。
> Release 标题与 `update_vX.Y.Z.json` 的 `title` 固定为：`v<X.Y.Z> <一句话内容>`（版本号开头，不加产品名或“发布”等前缀），例如 `v0.0.42 体验优化与兼容修复`。
> 预览版本必须在 GitHub 与 Gitee 上将 Release 标记为 prerelease，并在正文首行注明“预览版”。
> 更新日志范围：预览版只写本预览版相对上一版本的增量更新内容；正式版必须汇总自上一个正式版以来（含中间所有预览版）的全部更新内容，避免正式版用户缺少中间版本的变更记录。
> 编写前必须先执行 `git log --oneline <上一正式版标签>..HEAD` 逐条核对全部提交，发布笔记必须覆盖所有用户可见变更，禁止凭印象编写或遗漏提交。
> 发布目标为 GitHub、Gitee（PackingProof/PackingProof-Desktop）与旧 Gitee（chenjjian/ExpressPackingMonitoring）三个远端；旧 Gitee 同样创建 Release 并上传 update JSON、AppPatch（含双别名）与 LauncherPatch。
> `update_vX.Y.Z.json` 的 `title` 与 Release 标题一致，`notes` 只保留简洁更新摘要（分类要点加一句下载提示），完整笔记以 Release 页面为准。

<完整下载页/网盘链接，取自 .env 的 FULL_DOWNLOAD_PAGE；没有则删除本行>

# PackingProof v<X.Y.Z>

## 更新内容

### 功能与体验

- <模块>：<一句话描述>

### 问题修复

- <模块>：<一句话描述>

### 兼容与工程

- <模块>：<一句话描述>

## 下载与更新说明

- 安装向导：PackingProof_Setup_v<X.Y.Z>.exe（未签名时注明：未签名，SmartScreen 可能提示未知发布者）
- 完整包 7z / ZIP：免安装，用于系统原生解压和故障恢复
- 已安装用户：启动器会自动下载 AppPatch；如需手动更新，可完整解压 AppPatch 后双击包内更新脚本
- <仅新启动器基线时保留>：本版本建立新启动器基线 launcher-v<X.Y.Z>，升级后的主程序会自动应用 LauncherPatch

## 未验证事项

- 真实设备场景：摄像头/扫码枪断连、语音听感、真实退货（未实测则逐项列出）
- 平台/硬件专项：如 Windows 7、老显卡 NVENC 等（未实测则列出）
