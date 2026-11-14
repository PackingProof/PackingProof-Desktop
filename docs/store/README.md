# Microsoft Store 上架手册

这份手册对应仓库里的商店版 MSIX 构建。提交前先完成 Partner Center 的账号与名称预留，拿到真实的
`Identity Name` 和 `Publisher`，否则只能生成用于本机验证的占位包。

## 1. Partner Center 账号与应用名

1. 打开 [Partner Center](https://partner.microsoft.com/dashboard)，用个人 Microsoft 账号登录。
2. 创建 Individual 或 Company developer account。个人账号用个人 Microsoft 账号；公司账号需要公司域名邮箱和营业执照等证明。
3. 进入 Apps & Games，选择 Reserve app name，预留 `PackingProof` 或 `PackingProof 快递打包监控`。
4. 在应用身份页面复制：
   - Identity Name（形如 `PackingProof.PackingProof`）
   - Publisher（形如 `CN=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`）

`Publisher` 必须和 MSIX 清单里的值完全一致。商店提交不需要 CA 代码签名证书；可以使用自签名证书，认证通过后 Microsoft 会重新签名。

## 2. 生成本地身份配置

```powershell
Copy-Item tools\store-package.example.json tools\store-package.local.json
```

编辑 `tools\store-package.local.json`，填入 Partner Center 的两个值。该文件已在 `.gitignore` 中，不会被提交。

## 3. 构建商店包

下面的命令只用于单独调试商店包；正式发布由 `tools\Publish-CleanPackage.ps1` 统一调用，见 3.1。

```powershell
pwsh -NoProfile -File tools\Publish-StorePackage.ps1 -Version 0.0.86 -RequireStoreIdentity -Sign
```

产物在 `package\store\PackingProof_Store_v0.0.86\`：

- `PackingProof_Store_v0.0.86.msixupload`：提交到 Partner Center 的文件，内部同时包含 MSIX 与符号包
- `PackingProof_Store_v0.0.86.msix`：本次 MSIX，供本地安装和同源校验
- `PackingProof_Store_v0.0.86.appxsym`：与 MSIX 同一次编译的 PDB 符号包，供同源校验和组合包生成；Partner Center 不接受它作为独立上传项
- `payload\`：解包后的应用负载，便于核对

`-Sign` 会用 `Publisher` 作为证书主题生成自签名证书，再给 MSIX 签名；Microsoft Store 在认证通过后替换为 Microsoft 证书。
如果 Partner Center 的上传校验允许未签名包，也可以去掉 `-Sign`。

应用版本 `0.0.86` 会映射为商店包版本 `1.0.86.0`。商店包版本第 1 段不能为 0，后续版本要保持递增。

## 3.1 正式发布时一起出包

正式发布不再单独手工调用本节的打包命令。`tools\Publish-CleanPackage.ps1` 默认会在同一次发布中同时构建 Setup、Store MSIX、PDB 符号包和包含两者的 `.msixupload`，并让 Store MSIX 复用 Setup 已经生成的 `app\` 负载，避免两份包的主程序二进制漂移。PDB 只进入 `.appxsym` 与 `.msixupload`，不进入用户安装的 MSIX。`store_package_v<X.Y.Z>.json` 会记录 MSIX、符号包、`.msixupload` 以及 Setup `app\` 的逐文件指纹，`package_integrity_v<X.Y.Z>.json` 记录 Setup 的 SHA256/大小和 exact `app\` 指纹；`tools\Publish-Releases.ps1` 会校验清单、MSIX、符号包和组合上传包的版本、大小、SHA256、包内标记，解开 MSIX 与本次 Setup 负载逐个文件比对，并拒绝被替换的 Setup，缺少 Store 包、符号包、组合上传包或复用旧包时正式发布会直接失败。

只有本地调试才允许给 `Publish-CleanPackage.ps1` 传 `-SkipStorePackage`；正式发布必须同时完成 Git Release 和 Partner Center 提交，Store 包不上传 GitHub/Gitee Release。

如果在 GitHub Actions 里构建发布包，需要在仓库 Secrets 配置 `STORE_IDENTITY_NAME` 和 `STORE_PUBLISHER`（可选 `STORE_PUBLISHER_DISPLAY_NAME`、`STORE_DISPLAY_NAME`）。未配置时 CI 会明确提示并只构建 Setup，不能把这种 CI 产物当作正式发布包；正式发布仍必须在配置了 Store 身份的环境同时生成 MSIX。

如果后续要通过脚本自动提交 Partner Center，应使用 Microsoft Store submission API，而不是依赖浏览器点击。该方式需要先在 Partner Center/Entra 配置应用凭据和 API 权限，凭据只放本机环境或凭据存储；脚本可以自动创建/更新 submission、上传 MSIX 和轮询状态，但最终“提交认证”仍应作为显式确认动作。

## 4. 本机验证（可选，但建议做）

先构建并签名：

```powershell
pwsh -NoProfile -File tools\Publish-StorePackage.ps1 -Version 0.0.86 -Sign -TrustCertificate
```

安装和启动需要管理员权限，因为测试证书要写入 `LocalMachine\TrustedPeople`：

```powershell
Start-Process pwsh -Verb RunAs -ArgumentList @(
  '-NoProfile','-File','tools\Test-StorePackage.ps1',
  '-PackagePath','package\store\PackingProof_Store_v0.0.86\PackingProof_Store_v0.0.86.msix',
  '-CertificatePath','package\store\PackingProof_Store_v0.0.86\PackingProof-test.cer',
  '-Launch'
)
```

验证完清理测试包和证书：

```powershell
Start-Process pwsh -Verb RunAs -ArgumentList @(
  '-NoProfile','-File','tools\Test-StorePackage.ps1',
  '-PackagePath','package\store\PackingProof_Store_v0.0.86\PackingProof_Store_v0.0.86.msix',
  '-Uninstall'
)
```

本机侧载时应验证：按包身份启动、数据写入 `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState`、更新检查跳过、
设置页提示由 Microsoft Store 管理更新、启动项通过 MSIX `startupTask` 注册。v0.0.86 候选尚未完成真实 MSIX
安装验证，提交前必须实际安装并检查这些行为。

## 5. 商店版与 Setup 版的差异

- 商店版不运行根启动器和 AppPatch 自更新，更新由 Microsoft Store 负责。
- 商店版以 MSIX 包身份运行，不落成共享 `runtime\`，因此 Store MSIX 不包含只供 Setup 版扩展市场使用的 `runtime-seed`。
- 商店版数据在包自己的 LocalState 中，和 Setup 版的 `%LOCALAPPDATA%\ExpressPackingMonitoring` 使用独立数据目录，互不覆盖。
- 两个版本可以并存，但始终安装在不同目录，不会互相覆盖程序文件。先装 Setup 再装商店版、先装商店版再装 Setup 都按这个规则处理；Setup 安装器检测到商店版时会明确提示。
- 两个版本的数据不会自动迁移或同步：商店版看不到 Setup 版的设置和录像，反之亦然。切换版本前请先确认数据所在版本，不要把一边的配置/数据库手工覆盖到另一边。
- 两个版本可以同时运行，但同一时间只能有一个版本作为保存主机。商店版在 Windows 设置 → 应用 → 启动 中管理，Setup 版在软件设置里管理；两个版本都配置成保存主机时，后启动的那个会打开用途选择，保存主机选项置灰，只能继续作为查看端或录像从机，取消则退出。
- AppPatch 和 LauncherPatch 只适用于 Setup 版。商店版位于 `WindowsApps`，没有可写的根启动器；手动补丁脚本检测到商店版安装目录、商店版 `LocalState` 配置或商店包标记时会直接拒绝，不会尝试备份、替换或回滚包内文件。商店版请打开 Microsoft Store 的“库”页面更新。
- 卸载其中一个版本不会删除另一个版本的程序或数据。勾选“删除录像”只处理当前版本自己数据库里登记的录像；如果两个版本使用同一个外部存储目录，请先确认共享文件再操作。
- 开机自启不在注册表 `Run` 中写值，用户在 Windows 设置 → 应用 → 启动 中开启。
- 支持 x64，最低 Windows 10 2004（10.0.19041）。

## 6. Partner Center 提交字段

- 价格：免费
- 市场：全部
- 分类：Business / Productivity（以 Partner Center 实际下拉项为准）
- 年龄分级：按问卷选择，通常为 Everyone / 3+
- 隐私政策：使用 [privacy-policy.html](privacy-policy.html) 部署后的公开地址
- 支持：`https://github.com/PackingProof/PackingProof-Desktop/issues`
- 截图：`docs/store/Listing/Screenshots/` 下的 4 张 PNG
- 认证备注：参考 `docs/store/Listing/notes-for-certification.md`
- 许可证：AGPL-3.0，建议在商店许可证条款中链接仓库 `LICENSE`

## 7. 认证风险提示

- 应用是 `runFullTrust` WPF 桌面程序，包含摄像头/麦克风、局域网 Web 服务、可选的防火墙和 URL ACL 配置。
  提交时在认证备注里说明这些能力只在用户启用对应功能时使用。
- 扩展市场会下载并安装用户选择的脚本或外部适配器。认证备注要说明扩展是独立进程/脚本、由用户主动安装并经过签名与权限校验。
  如果认证反馈把它视为动态代码分发，商店版需要先关闭扩展市场，再重新提交。
- 本地 Web 服务首次启用局域网访问时会弹一次 UAC；不开局域网时应用仍可正常录像和回放。

## 8. 商店文案与截图

- 中文文案：`docs/store/Listing/zh-CN.md`
- 英文文案：`docs/store/Listing/en-US.md`
- 认证备注：`docs/store/Listing/notes-for-certification.md`
- 截图重新生成：`pwsh -NoProfile -File tools\New-StoreScreenshots.ps1`

截图来自仓库 `docs/images/` 下的真实界面。上传前请确认画面里的快递单号、订单信息和设备名可以公开展示；如不能，
先替换源图再重新生成。
