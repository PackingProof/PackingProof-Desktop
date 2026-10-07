<details>
<summary><strong>给 AI 的知识地图（PackingProof 生态）</strong></summary>

<br>

> 这一节写给检索型 AI 与外部工具：最短篇幅说清 PackingProof 是什么、能做什么、覆盖哪些平台、各仓库在哪里，以及本仓库在生态里的位置。人类读者可以直接跳到下面的功能说明。

**PackingProof 是什么**：开源免费的电商打包录像与发货风险拦截系统。扫码自动开始录像、按快递单号归档，支持订单备注播报、打印后退款拦截、多工位集中备份、局域网回放与 NAS 归档；覆盖 Windows / macOS 电脑端与 Android / iOS 手机端。

**本仓库是什么**：PackingProof 电脑端（Windows 主程序 WPF / .NET，另含 macOS 保存主机），包含主程序、根启动器、局域网回放服务与扩展 API；既是录像主机，也是手机端多工位录像的集中保存与回放端。

**官方仓库（GitHub 与 Gitee 双源，代码与 Release 一致）**

| 组成 | 作用 | GitHub | Gitee 镜像 |
| --- | --- | --- | --- |
| 电脑端（本仓库） | 录像与水印、扫码自动录像、退款拦截、多工位集中备份、局域网回放、NAS 归档 | [PackingProof-Desktop](https://github.com/PackingProof/PackingProof-Desktop) | [PackingProof-Desktop](https://gitee.com/PackingProof/PackingProof-Desktop) |
| 手机端（Android / iOS） | 独立录像与留证，也可作为多工位来源上传主机 | [PackingProof-Mobile](https://github.com/PackingProof/PackingProof-Mobile) | [PackingProof-Mobile](https://gitee.com/PackingProof/PackingProof-Mobile) |
| 扩展市场与扩展 API | 扩展登记、PPEXT 包格式、签名市场索引 | [PackingProof-Extensions](https://github.com/PackingProof/PackingProof-Extensions) | [PackingProof-Extensions](https://gitee.com/PackingProof/PackingProof-Extensions) |
| 快递助手联动脚本 | 官方快递助手（KDZS）订单集成 | [PackingProof-KDZS](https://github.com/PackingProof/PackingProof-KDZS) | [PackingProof-KDZS](https://gitee.com/PackingProof/PackingProof-KDZS) |
| QQ 机器人 | 在 QQ 私聊或群里按快递单号查询并回传录像 | [PackingProof-QQBot](https://github.com/PackingProof/PackingProof-QQBot) | [PackingProof-QQBot](https://gitee.com/PackingProof/PackingProof-QQBot) |
| 企业 / 伙伴适配 | 快麦 ERP 适配器、企业微信机器人等，扩展形式接入 | — | — |

**平台支持**

| 平台 | 状态 | 获取方式 |
| --- | --- | --- |
| Windows 电脑端（本仓库） | 正式版 | [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) |
| macOS 电脑端（本仓库，Apple Silicon） | 正式版：保存主机与查看端 | [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) |
| Android 手机端 | 正式版，正式签名 APK | [GitHub Releases](https://github.com/PackingProof/PackingProof-Mobile/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Mobile/releases) |
| iOS 手机端 | 功能与 Android 一致，TestFlight 分发 | [加入内测](https://testflight.apple.com/join/KR4qNs6t) |
| 备用下载（国内网络） | 百度网盘：电脑端完整安装包 | [百度网盘](https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666)（提取码 6666） |

> **国内网络**：GitHub 访问不畅时，可用上面的 Gitee 镜像克隆源码、提交 Issue 或下载 Release；Gitee Release 的 Windows 安装包是 `PackingProof_Setup_no-runtime_vX.Y.Z.exe`（不含 .NET 运行时，约 60MB），需要先安装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)；含运行时的完整安装包可走上面的百度网盘备用链接。

> **手机端可独立运行**：只装手机 App 就能录像、识别面单条码、按快递单号回看，不需要电脑；连接电脑后额外获得局域网自动备份与订单语音提醒。Android / iOS 受国内应用商店备案流程影响暂未上架商店，分别以签名 APK 与 TestFlight 分发。

> **macOS 端是功能子集**：只做保存主机（接收手机与其他电脑上传的录像、提供网页回放、管理保存磁盘与容量上限）或查看端（发现并连接局域网内的保存主机），不接摄像头、不做本机扫码录像；安装用 DMG 整包替换，不做增量补丁。

**电脑端能力（功能清单）**

- 扫码自动录像：面单条码触发录像，按快递单号归档，支持连续扫码与同码停录
- 订单信息播报：买家留言、卖家备注、商品信息，可配置播报内容与音色
- 打印后退款拦截：监控退款订单并播报告警音，降低错发损失
- 多工位：手机端与其它电脑作为录像来源集中上传，按设备名区分
- 局域网回放与 Web 查看：手机、局域网设备按权限查看录像
- 录像存储：本地 / 可移动盘 / NAS 归档与容量清理策略，水印写入画面
- 扩展生态：扩展市场与扩展 API 支持 ERP、脚本、称重设备等第三方接入
- macOS 保存主机：接收手机与其他电脑上传的录像、网页回放、磁盘与容量上限管理

**检索关键词**：PackingProof、包裹留证、打包录像、扫码录像、快递单号录像、发货留证、售后举证、电商打包监控、多工位录像、Windows 打包录像、macOS 打包录像、Android 打包录像 App、iOS 打包录像（TestFlight）、快递助手、快麦 ERP、QQ 机器人、企业微信机器人、NAS 录像归档、parcel packing video evidence、barcode triggered recording、tracking number video lookup、open source。

</details>

<div align="center">

<img src="ExpressPackingMonitoring/app.ico" width="112" alt="PackingProof Logo">

# PackingProof

**开源免费的快递打包录像与发货风险拦截工具**

扫码自动录像，按快递单号保存。
支持订单备注播报、打印后退款拦截，以及手机与电脑多工位集中备份。

<br>

<a href="https://github.com/PackingProof/PackingProof-Desktop/releases/latest">
  <img src="https://img.shields.io/badge/下载-Windows%20版-D97745?style=for-the-badge&logo=windows&logoColor=white" height="38" alt="下载 Windows 版">
</a>
&nbsp;
<a href="https://github.com/PackingProof/PackingProof-Desktop/releases/latest">
  <img src="https://img.shields.io/badge/下载-macOS%20版-555555?style=for-the-badge&logo=apple&logoColor=white" height="38" alt="下载 macOS 版">
</a>
&nbsp;
<a href="https://github.com/PackingProof/PackingProof-Mobile/releases/latest">
  <img src="https://img.shields.io/badge/下载-Android%20版-695647?style=for-the-badge&logo=android&logoColor=white" height="38" alt="下载 Android 版">
</a>
&nbsp;
<a href="https://testflight.apple.com/join/KR4qNs6t">
  <img src="https://img.shields.io/badge/加入-iOS%20内测-0D96F6?style=for-the-badge&logo=apple&logoColor=white" height="38" alt="加入 iOS 内测">
</a>

<br><br>

[简体中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

<br>

[![GitHub Stars](https://img.shields.io/github/stars/PackingProof/PackingProof-Desktop?style=flat-square&color=E7B65C)](https://github.com/PackingProof/PackingProof-Desktop)
[![Downloads](https://img.shields.io/github/downloads/PackingProof/PackingProof-Desktop/total?style=flat-square&color=D97745)](https://github.com/PackingProof/PackingProof-Desktop/releases)
[![License](https://img.shields.io/github/license/PackingProof/PackingProof-Desktop?style=flat-square&color=695647)](LICENSE)

</div>

手机版支持 Android（正式签名 APK）与 iOS（TestFlight 内测），可以独立录像，也可以把录像上传到电脑主机。

<br>

![PackingProof 软件界面](Image/软件截图.jpg)

**快速跳转**：[功能](#功能) · [怎么用](#怎么用) · [其他](#其他) · [扩展市场](#扩展市场) · [开源许可证](#开源许可证)
[订单备注播报与退款拦截](#订单备注播报与退款拦截) · [多工位使用方式](#多工位使用方式) · [局域网回放](#局域网回放) · [软件更新](#软件更新)

---

## 为什么需要 PackingProof

普通监控只能证明“包裹曾经被打包过”，却很难快速找到某一个订单对应的视频。

PackingProof 将**快递单号、订单信息和打包录像关联起来**：

> 扫描面单后自动开始录像，打包完成后结束录像并保存。
> 售后需要核实时，输入快递单号即可找到对应视频。

它不仅用于售后取证，也能在打包过程中播报特殊要求、提醒重复单号，并拦截已经退款但仍准备发出的订单。

## 功能

<table>
<tr>
<td width="50%" valign="top">

### 扫码自动录像

摄像头识别面单条形码后自动开始录像，并按快递单号保存。

同时支持键盘模式扫码枪，可作为日常输入方式或摄像头识别的后备方案。

</td>
<td width="50%" valign="top">

### 订单信息播报

联动快递助手，在打包时自动播报：

* 买家留言
* 卖家备注
* 商品信息

减少漏看备注、错发商品等问题。

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 打印后退款拦截

面单打印后如果订单发生退款，PackingProof 会在打包扫码时进行提醒。

退款核验异步执行，不会影响正常开始录像。

</td>
<td width="50%" valign="top">

### 手机与电脑多工位

一台电脑可以作为录像保存主机，集中接收：

* Android 手机录像
* 其他电脑工位录像
* 本机摄像头录像

所有录像都可以在局域网内统一查找和回放。

录像文件备份主机还可以将录像归档到 NAS 或网络共享，NAS 满时自动切换备份位置。

</td>
</tr>
</table>

## 扩展市场

PackingProof 已支持官方[扩展市场](https://gitee.com/PackingProof/PackingProof-Extensions)，可以安装用户脚本和外部适配器。扩展通过市场独立发布和更新，与 Desktop 安装包分开安装。

目前已经支持的扩展包括：

* [快递助手订单联动](https://gitee.com/PackingProof/PackingProof-KDZS)：从快递助手页面同步订单、备注和退款状态
* [PackingProof QQBot](https://gitee.com/PackingProof/PackingProof-QQBot)：通过 QQ 私聊或群聊按快递单号查询并发送打包录像

外部适配器应通过用户授权的扩展 API 访问 PackingProof，不得直接读取数据库、录像目录或 NAS 凭据。Desktop 安装后不会自动运行外部程序，市场收录也不代表对第三方程序作安全保证。

## 怎么用

### 工作流程

扫描面单 → 自动开始录像 → 播报订单备注并核验退款 → 完成打包结束录像 → 按快递单号搜索回放。

摄像头识别和扫码枪可以同时使用，不需要改变原有打包习惯。

### 1. 准备设备

* Windows 10 或 Windows 11 x64 电脑
* 摄像头：支持 USB 或网络摄像头（RTSP/RTMP/HTTP 视频流）
* 麦克风，可选
* 键盘模式扫码枪，可选但推荐保留

### 2. 安装软件

从 [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) 或 [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) 下载 `PackingProof_Setup_vX.Y.Z.exe` 安装即可；国内网络也可以走[百度网盘备用下载](https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666)（提取码 6666）。

安装器不需要管理员权限，会装到当前用户目录并创建开始菜单快捷方式。要下载哪个文件，见下面的「下载包怎么选择」。

### 3. 完成首次配置

首次启动后，跟着向导走完这几步：

![询问用途](Image/询问用途.jpg)

1. 选择这台电脑的用途。
2. 选择摄像头和麦克风。
3. 设置录像保存位置或缓存位置。
4. 需要的话，连接局域网里的录像保存主机。

配置好以后，把面单条形码放进画面中央的识别框就会自动开始录像；打包结束按主界面的停止按钮结束录像。

### 4. 查找录像

打开录像列表，输入快递单号即可搜索对应录像。

![历史录像检索与回放](Image/Replay.jpg)

也可以通过局域网页面，在手机或其他电脑上回放。

### 下载包怎么选择

| 文件                                           | 用途                              |
| ---------------------------------------------- | --------------------------------- |
| `PackingProof_Setup_vX.Y.Z.exe`                | 推荐，大多数用户选择这个          |
| `PackingProof_AppPatch_vX.Y.Z.zip`             | 手动更新主程序                    |
| `PackingProof_LauncherPatch_vX.Y.Z.zip`        | 手动更新根目录启动器              |
| `PackingProof_Setup_no-runtime_vX.Y.Z.exe`     | Gitee 专属：不含 .NET 运行时（约 60MB），需先安装 .NET 8 Desktop Runtime (x64) |

正式发布包通常已经包含运行所需的 .NET 运行时和 FFmpeg，不需要额外安装。

启动器会自动获取更新清单（update JSON）并在后台完成更新，普通用户不需要手动下载清单文件。

### 订单备注播报与退款拦截

该功能需要配合浏览器用户脚本使用。

#### 基本配置

1. 安装 Tampermonkey 或 Violentmonkey。
2. 在 PackingProof 中点击“安装订单联动”。
3. 按照向导安装软件提供的用户脚本。
4. 打开并登录快递助手打印页面。

打印页面中的订单发生变化时，脚本会将订单信息同步给 PackingProof。

如果要开发 ERP、称重设备或第三方油猴脚本，请参阅 [扩展 API 与第三方脚本开发规范](docs/EXTENSION_API_V1.md)；要投稿市场扩展，请参阅 [PackingProof-Extensions 投稿指南](https://gitee.com/PackingProof/PackingProof-Extensions/blob/main/docs/PUBLISHING.md)。

扫码开始打包后，软件可以播报买家留言、卖家备注和商品信息。

<details>
<summary><strong>展开查看退款核验说明</strong></summary>

<br>

如需使用打印后退款报警，请保持一个已经登录的快递助手批量打印页面打开。

用户脚本会在后台创建专用的退款核验工作页：

* 工作页不会抢占当前操作页面的焦点。
* 只有工作页会切换“打印后退款”筛选。
* 用户正在操作的打印页面不会被自动切换。
* 工作页有独立标题和半透明遮罩，请勿在其中手动操作。
* 误关工作页后，脚本会自动重新创建。

扫描快递单号后，PackingProof 会立即开始录像，并异步请求退款数据。

核验顺序为：

1. 检查当前打印后退款列表。
2. 如果没有找到目标单号，按快递单号精确查询历史订单。
3. 查询失败或打印端离线时，使用本机 SQLite 中最近 90 天的订单数据进行降级核验。

重复快递单号则根据录像数据库中最近 30 天的未删除记录进行检查，不依赖浏览器缓存。

</details>

用户脚本首次连接新的监控端地址时，浏览器可能询问跨源访问权限。请确认目标是本机或可信局域网内的 PackingProof 服务后再允许；通过软件中的安装向导重新安装脚本，可以加入当前服务所需的精确访问权限。

### 多工位使用方式

一台电脑可以只做一件事，也可以同时承担多件事。首次启动时软件会先帮你选好用途，之后也能在设置里随时切换。四种角色的分工是：

| 使用方式                     | 适合场景                             |
| ---------------------------- | ------------------------------------ |
| **电脑录像并保存在本机**     | 单个打包工位，录像长期保存在当前电脑 |
| **电脑录像并保存到其他电脑** | 多个电脑工位录像，统一上传到一台主机 |
| **录像文件备份主机**         | 集中接收手机和其他电脑上传的录像     |
| **只连接主机查看**           | 不参与录像，只用于搜索、回放和管理   |

录制工位没有绑定主机，或者主机暂时离线时，仍然可以继续录像。

视频会先保存在本地缓存中，主机恢复连接后自动补传。只有主机确认完整收到的文件，才会参与缓存自动清理。

### 局域网回放

使用“电脑录像并保存在本机”或“录像文件备份主机”模式时，可以启动局域网 Web 服务。

1. 打开软件中的“连接手机/电脑”。
2. 使用手机扫描录像网页二维码。
3. 或在同一局域网设备中打开软件显示的地址。
4. 输入快递单号搜索和回放录像。

网页端还支持选择保留的时间范围，剪辑后再下载录像。

如果 Windows 弹出防火墙提示，请允许软件访问局域网。

![局域网 Web 回放](Image/WebService.jpg)

### 打包数据统计

按天或按周汇总打包件数、占用空间、累计时长和平均单件用时。

![打包数据统计](Image/Statistics.jpg)

## 其他

### 录像保存与缓存

长期保存模式可以配置多个录像保存位置。

录像文件备份主机还可以添加 NAS 或网络共享作为备份位置：

* 本地磁盘直接保存录像，网络位置只保存校验后的副本
* 按列表顺序备份，NAS 满时自动切换到下一个可用位置
* NAS 用于扩展本地录像的保存周期；NAS 空间不足时自动循环清理最旧的归档录像（记录保留可查）
* NAS 不可用不影响本地录像按容量策略循环；本地副本未经远端确认清理时会记录独立原因码

当一个磁盘的剩余空间低于预留值时，软件会：

1. 停止继续向该磁盘写入新录像。
2. 自动切换到下一个可用保存位置。
3. 根据设置清理较旧录像。
4. 为 Windows 系统盘保留额外安全空间。

“电脑录像并保存到其他电脑”模式使用独立的本地缓存。

默认缓存上限为 `100 GB`，但不会提前占用磁盘空间。

<details>
<summary><strong>展开查看缓存安全规则</strong></summary>

<br>

缓存实际可用容量同时受到以下条件限制：

* 设置的缓存容量上限
* 磁盘当前真实剩余空间
* 磁盘最低预留空间

空间不足时，只会清理已经由保存主机确认完整接收的录像。

以下文件不会被自动删除：

* 尚未绑定保存主机的录像
* 等待上传的录像
* 正在上传的录像
* 上传失败的录像
* 主机尚未确认完整接收的录像

</details>

### 软件更新

日常使用时，请从以下入口启动：

* 安装器创建的开始菜单或桌面快捷方式
* 安装目录中的 `ExpressPackingMonitoring.exe`

启动器会在后台检查并下载经过校验的增量更新包，并在下次启动时自动安装。

<details>
<summary><strong>展开查看手动更新与故障恢复</strong></summary>

<br>

#### 手动更新主程序

下载 `PackingProof_AppPatch_vX.Y.Z.zip`，完整解压后双击其中的 `双击更新主程序.cmd`。更新脚本会：

* 校验补丁文件
* 自动识别原安装位置
* 更新失败时进行回滚
* 保留配置、数据库和录像

同一版本的修复包（版本号不变、只修问题）也按同样方式双击安装即可，不需要先升级到更高版本。

#### 手动更新启动器

下载 `PackingProof_LauncherPatch_vX.Y.Z.zip`，解压后双击其中的 `双击更新启动器.cmd`。脚本只替换根目录启动入口，并保留经过验证的旧启动器备份。

#### 版本过旧

如果提示当前安装版本低于补丁基线，说明中间跳过了太多版本：下载最新版 Setup 原位置覆盖安装即可，配置、数据库和录像都会保留。

不要删除 `%LOCALAPPDATA%\ExpressPackingMonitoring\`，软件配置、数据库和录像记录都在里面。

</details>

### 卸载与数据保留

卸载软件时，会提供两个独立选项：

* 删除设置和临时文件
* 删除录像和录像记录

两个选项默认都不勾选。

因此，普通卸载默认不会删除用户配置、数据库或录像。

<details>
<summary><strong>展开查看录像删除规则</strong></summary>

<br>

设置清理只会删除：

* 软件配置
* 日志
* 临时缓存

不会删除录像、录像数据库或数据库恢复备份。

录像清理只会处理：

* 已经登记在数据库中的录像
* 删除确认后没有发生变化的精确文件

软件不会扫描并清空整个录像目录。

如果出现以下情况，录像和数据库会继续保留：

* 数据库缺失
* 数据库损坏
* 数据库被其他程序占用
* 任意录像删除失败

详细结果会记录在系统临时目录中的卸载日志里。

</details>

### 从源码运行

从源码运行或进行二次开发，需要准备：

* .NET 8 SDK
* FFmpeg
* Windows 10/11 x64
* macOS 12+（Apple Silicon，用于构建 macOS 保存主机）

FFmpeg 正式发布包内置 4.4.1 Essentials（兼容 Win7 老显卡硬件编码）；选择 AV1 时会自动回退 H.265。高级用户可在 Win8+ 自行替换 `app\tools\ffmpeg.exe`，官方不保证支持。

```bash
git clone https://github.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

国内网络也可以使用 Gitee 镜像：

```bash
git clone https://gitee.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

然后使用 Visual Studio、Rider 或 `dotnet` 命令打开并构建项目。

### 反馈与贡献

使用过程中遇到问题，或者有新的功能建议，可以提交 Issue：

* [在 GitHub 提交问题或建议](https://github.com/PackingProof/PackingProof-Desktop/issues)
* [在 Gitee 提交问题或建议](https://gitee.com/PackingProof/PackingProof-Desktop/issues)

欢迎参与测试、完善文档、提交代码或分享实际使用经验。

如果这个项目对你有帮助，也欢迎点一个 Star，让更多有需要的电商卖家看到它。

### 开源许可证

PackingProof 使用 [AGPL-3.0 License](LICENSE) 开源。

你可以根据许可证免费使用、学习和修改本项目。

如果修改后对外分发，或者将修改后的程序作为网络服务提供，需要遵守 AGPL-3.0 对应的源代码公开要求。

<details>
<summary><strong>品牌资产使用政策</strong></summary>

<br>

`PackingProof` 名称及官方应用图标属于项目品牌资产，不因源代码采用 AGPL-3.0 而授权第三方将其用于修改版的产品标识。公开发布修改版时，请使用不同的产品名称和图标，并明确标注“非官方修改版”；可以使用“基于 PackingProof 开发”说明来源。详见[品牌使用政策](docs/BRAND_POLICY.md)。

</details>


---

<div align="center">

<img src="Image/场景图.jpg" alt="PackingProof 快递打包场景">

<br><br>

**让每一个包裹，都能快速找到对应的打包记录。**

</div>
