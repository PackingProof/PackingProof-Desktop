# 软件配置

`BrandProfile` 在构建时选择 `branding/profiles/<配置名>/Brand.props`。默认配置为 `packingproof`：

```powershell
dotnet build ExpressPackingMonitoring.sln -c Debug
dotnet build ExpressPackingMonitoring.sln -c Debug -p:BrandProfile=software-template
```

发布脚本通过同名环境变量选择软件配置：

```powershell
$env:BRAND_PROFILE = "software-template"
pwsh -NoProfile -File tools\Publish-CleanPackage.ps1
```

也可以把软件配置放在仓库外。复制 `profiles.local.props.example` 为
`branding/profiles.local.props`，填写一次外部根目录：

```xml
<Project>
  <PropertyGroup>
    <BrandExternalProfilesRoot>D:\PackingProof-Brands</BrandExternalProfilesRoot>
  </PropertyGroup>
</Project>
```

外部根目录下按配置名建立自包含目录：

```text
D:\PackingProof-Brands\
├─ customer-a\
│  ├─ Brand.props
│  ├─ Features.json
│  ├─ app.ico
│  ├─ logo.png
│  └─ launcher-baseline.json
└─ customer-b\
   ├─ Brand.props
   ├─ Features.json
   ├─ app.ico
   ├─ logo.png
   └─ launcher-baseline.json
```

之后只需切换配置名，不再传完整路径：

```powershell
dotnet build ExpressPackingMonitoring.sln -c Debug -p:BrandProfile=customer-a

$env:BRAND_PROFILE = "customer-b"
pwsh -NoProfile -File tools\Publish-CleanPackage.ps1
```

查找优先级依次为：命令行或环境指定的 `BrandProfileDirectory`、`BRAND_PROFILE_DIR`、
`BRAND_PROFILE_ROOT`、`branding/profiles.local.props` 中的外部根目录，最后才是仓库内
`branding/profiles/<配置名>`。`profiles.local.props` 保存本机绝对路径，必须保持未跟踪；
模板文件 `profiles.local.props.example` 可以提交。配置了外部根目录但其中没有当前配置时，
如果仓库内有同名配置则自动回退到内置配置，因此默认 `packingproof` 构建不受影响。
`software-template/launcher-baseline.json` 只用于验证模板构建链，不随公共 Release 发布。
正式软件配置必须运行 `Publish-LauncherBaseline.ps1` 生成自己的基线、提交清单并创建对应组件标签。

软件配置可以在 `Brand.props` 中用 `BrandLocalizationRelativePath` 指向配置目录内的
`BrandLocalizations.json`，为任意界面语言提供软件名称：

```json
{
  "locales": {
    "zh-Hans": {
      "productName": "示例监控 6.0",
      "displayName": "示例监控 6.0",
      "windowTitle": "示例监控 6.0",
      "description": "示例监控"
    },
    "en-US": {
      "productName": "ExampleMonitoring",
      "displayName": "ExampleMonitoring",
      "windowTitle": "ExampleMonitoring",
      "description": "ExampleMonitoring"
    },
    "th-TH": {
      "productName": "ExampleMonitoring",
      "displayName": "ExampleMonitoring",
      "windowTitle": "ExampleMonitoring",
      "description": "ExampleMonitoring"
    }
  }
}
```

新增语言只需增加 locale 键，不需要修改公共代码；缺少当前语言时回退到 `Brand.props`
里的基础字段。要让应用整体支持一门新语言，仍需补齐该语言的 UI 资源、网页词条、字体和布局验证。

每个软件配置必须通过 `BrandFeaturesRelativePath` 指向配置目录内的能力清单：

```xml
<BrandFeaturesRelativePath>Features.json</BrandFeaturesRelativePath>
```

`Features.json` 只接受当前支持的结构和字段，未知字段、重复字段、缺少字段或能力依赖冲突都会让
`dotnet build` 失败。文件中的 `capabilities` 表示软件能力，`inputs` 表示摄像头、麦克风、扫码枪、
摄像头扫码和 OBB 等输入能力；依赖冲突会由 validator 给出具体能力 ID 和原因。

旧的 `BrandMobileAppEnabled` 仅在迁移期继续兼容。已有外置配置可先生成候选文件，审核后再放回
配置目录；迁移工具会读取该配置里的旧字段，也不会静默覆盖软件配置：

```powershell
dotnet run --project tools\ExpressPackingMonitoring.BrandFeatures.Validator `
  -p:SkipBrandFeatureValidation=true -- `
  --profile-directory C:\PackingProof-Brands\customer-a `
  --migration-output C:\Temp\customer-a-Features.json
```

创建独立软件配置时复制 `profiles/software-template`，替换软件名称、图标、网址、更新地址和所有身份字段。每个并行安装的应用版本必须使用不同的：

- `BrandDataDirectoryName`
- `BrandInstanceNamePrefix`
- `BrandInstallDirectoryName`
- `BrandSetupAppId`
- `BrandStoreIdentityName`

独立软件配置还必须替换 `BrandExtensionMarketPublicKeyId` 和 `BrandExtensionMarketPublicKeySpki`，填入该配置自己的扩展市场签名公钥。
模板中的 `BrandExtensionMarketPublicKeyIsPlaceholder=true` 会让 `Check-ReleasePrereqs.ps1` 阻止正式发布；只有替换公钥并改为 `false` 后才能出正式版。

发布凭据也从外置配置读取，不写进源码仓库：

```xml
<BrandGitHubReleaseTokenFile>%USERPROFILE%\.config\PackingProof\github-release-update\token.txt</BrandGitHubReleaseTokenFile>
<BrandGiteeReleaseTokenFile>%USERPROFILE%\.config\PackingProof\gitee-release-update\token.txt</BrandGiteeReleaseTokenFile>
<BrandGiteeReleaseUsername>PackingProof-Distribution-Bot</BrandGiteeReleaseUsername>
```

这两个 token 必须属于该软件配置自己的专用发布账号，文件放在仓库外。桌面更新渠道必须允许匿名读取：源码仓库可以私有，但 `BrandUpdateGiteeCheckUrl`、`BrandUpdateGithubCheckUrl` 和 Release 附件必须来自公开更新仓库或公开静态地址。

如果该软件配置暂不开放手机 App，把 `Features.json` 的
`capabilities.mobileApp` 改为 `false`。

能力消费切换完成后，这会隐藏 Android/iOS 下载入口、停止手机版本检查，并跳过首次移动连接提示；
手机网页回放与电脑备份不受影响。迁移期内生产界面仍兼容旧的 `BrandMobileAppEnabled`，
新软件配置必须同时维护显式能力清单。

`BrandWebServerPort` 与 `BrandDiscoveryPort` 也必须使用该软件配置自己的端口。默认 PackingProof 固定保留
`5280/5281`；模板使用 `5380/5381`，确保多个应用版本同时运行时 Web 服务和 UDP 主机发现不会抢占同一端口。

`BrandCompatibilityId`、数据目录和扩展协议字段属于兼容契约。仅更换显示名称时不要修改默认 `packingproof` 配置中的这些字段。

`software-template` 里的图标只是占位文件，正式发布前必须替换为对应授权资产。

软件配置、图标和 `BrandMetadata` 会进入启动器逻辑指纹。改变这些文件后，下一次正式发布必须在目标版本上重新生成 Launcher 基线并创建对应的组件标签，不能继续沿用旧基线。默认 PackingProof 保持 `launcher-vX.Y.Z`，独立软件配置使用 `launcher-<BrandArtifactPrefix>-vX.Y.Z`，避免多个应用版本争用同一个 Git 标签。

AppPatch 与 LauncherPatch 的清单会携带软件配置契约；手动安装脚本只接受软件名、数据目录和实例前缀都匹配的安装目录，禁止把独立软件配置补丁装到默认版本，反向也一样。
